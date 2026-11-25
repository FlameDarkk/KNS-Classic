using KnS.Data.Dialects;
using Microsoft.Extensions.Logging;

namespace KnS.Data.Schema;

/// <summary>
/// Creates the tables (when missing) and adds columns newer server versions introduced. The
/// layout is exactly the Python server's, so both servers run on the same saved data.
/// </summary>
internal sealed class GameSchema(DataSession session, string masterDataDirectory, ILogger logger)
{
    private SqlDialect Sql => session.Dialect;

    public void Create()
    {
        CreatePlatformTables();
        CreatePlayerTables();

        var seeder = new MasterDataSeeder(session, masterDataDirectory, logger);
        foreach (MasterTable master in MasterTables.All)
        {
            seeder.Seed(master);
        }

        CreateInventoryTables();
        CreateFellowTables();
        CreateStageTables();
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS reward_mail(
                rowid_alias {Sql.AutoIncrementPrimaryKey}, user_no {Sql.IntegerColumn},
                item_id1 {Sql.IntegerColumn}, value1 {Sql.IntegerColumn},
                item_id2 {Sql.IntegerColumn}, value2 {Sql.IntegerColumn},
                message {Sql.TextColumn}, text_code {Sql.IntegerColumn}, type {Sql.IntegerColumn}, reg_date {Sql.IntegerColumn},
                title {Sql.TextColumn})
            """);
        AddMissingTextColumns("reward_mail", ["title"]);
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS chat_history(
                id {Sql.AutoIncrementPrimaryKey}, chat_group {Sql.IntegerColumn},
                payload {Sql.TextColumn}, created_at {Sql.IntegerColumn}, pin_id {Sql.IntegerColumn} DEFAULT 0)
            """);
        AddMissingIntegerColumns("chat_history", [("pin_id", 0)]);
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS chat_pin(
                id {Sql.AutoIncrementPrimaryKey}, message {Sql.TextColumn},
                start_time {Sql.IntegerColumn}, end_time {Sql.IntegerColumn}, created_at {Sql.IntegerColumn})
            """);
    }

    /// <summary>Hive / Hub guest accounts of the Com2uS platform endpoints.</summary>
    private void CreatePlatformTables()
    {
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS account(
                uid {Sql.KeyTextColumn} PRIMARY KEY, guest_uid {Sql.TextColumn}, login_key {Sql.TextColumn}, session_key {Sql.TextColumn},
                did {Sql.TextColumn}, created {Sql.RealColumn})
            """);

        // Gateway login sessions: a hash of each session key protocol 51 issued.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS player_session(
                session_hash {Sql.KeyTextColumn} PRIMARY KEY, user_no {Sql.IntegerColumn}, created {Sql.RealColumn})
            """);

        // Id and password accounts of the browser build. did ties an account to its player (gameuser.hub_did).
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS login_account(
                login_id {Sql.KeyTextColumn} PRIMARY KEY, password_hash {Sql.TextColumn}, did {Sql.KeyTextColumn} UNIQUE,
                guest_uid {Sql.TextColumn}, nickname {Sql.TextColumn}, created {Sql.RealColumn})
            """);
        AddMissingTextColumns("login_account", ["nickname"]);

        // Sign-in tokens of login_account ids (SHA-256 of the token; 30 days, renewed on use).
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS account_session(
                token_hash {Sql.KeyTextColumn} PRIMARY KEY, login_id {Sql.KeyTextColumn}, created {Sql.RealColumn}, expires {Sql.RealColumn})
            """);

        // The tester allowlist (KNS_TESTERS_ONLY): an email, the device attached to it, a revoke flag.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS tester(
                tester_id {Sql.AutoIncrementPrimaryKey},
                email {Sql.KeyTextColumn} UNIQUE, device_id {Sql.KeyTextColumn}, note {Sql.TextColumn},
                revoked {Sql.IntegerColumn}, created {Sql.RealColumn})
            """);

        // Server-side feature switches, changed without a restart (a missing row falls back to FeatureFlags.Defaults).
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS feature_flag(
                feature_key {Sql.KeyTextColumn} PRIMARY KEY, enabled {Sql.IntegerColumn},
                created_date {Sql.RealColumn}, update_date {Sql.RealColumn})
            """);
        // Server-wide values. progress_epoch: when every player's progress was last reset (tools/reset_progress.py);
        // clients drop their local game save once when it changes (GET/POST /api/progress).
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS server_state(
                state_key {Sql.KeyTextColumn} PRIMARY KEY, value {Sql.IntegerColumn})
            """);
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS access_attempt(
                device_id {Sql.KeyTextColumn} PRIMARY KEY, user_no {Sql.IntegerColumn}, attempts {Sql.IntegerColumn}, last_seen {Sql.RealColumn})
            """);
    }

    private void CreatePlayerTables()
    {
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS gameuser(
                rowid_alias {Sql.AutoIncrementPrimaryKey},
                user_no {Sql.IntegerColumn}, hub_did {Sql.KeyTextColumn} UNIQUE, hub_uid {Sql.TextColumn},
                nickname {Sql.TextColumn}, created {Sql.RealColumn})
            """);

        // Extra devices that play an existing account: a device's Hive guest id -> its user_no.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS device_link(
                hub_did {Sql.KeyTextColumn} PRIMARY KEY, user_no {Sql.IntegerColumn}, created {Sql.RealColumn})
            """);

        // Wallet, active hero, level, and the bag slots bought on top of the free ones.
        AddMissingIntegerColumns("gameuser", [
            ("gold", StoredPlayerDefaults.Gold),
            ("cash", StoredPlayerDefaults.Gems),
            ("hero_pcid", StoredPlayerDefaults.HeroPcid),
            ("level", StoredPlayerDefaults.Level),
            ("exp", 0),
            ("inventory_cash", 0),
            ("fellow_inventory_cash", 0),
            ("fellow_item_inventory_cash", 0),
        ]);

        // Heroes a user owns; hero_no is the per-user id the client echoes back (the hero_pcid).
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS user_hero(
                user_no {Sql.IntegerColumn}, hero_no {Sql.IntegerColumn}, hero_pcid {Sql.IntegerColumn},
                PRIMARY KEY(user_no, hero_no))
            """);

        // Stages a user has cleared, per stage mode (1 normal, 2 hard).
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS stage_clear(
                user_no {Sql.IntegerColumn}, stage_id {Sql.IntegerColumn}, stage_mode {Sql.IntegerColumn}, clear_count {Sql.IntegerColumn},
                PRIMARY KEY(user_no, stage_id, stage_mode))
            """);
    }

    private void CreateInventoryTables()
    {
        // Starter items (fixed numbers, never stored in `item`) the user sold or used up.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS starter_sold(
                user_no {Sql.IntegerColumn}, item_no {Sql.IntegerColumn}, PRIMARY KEY(user_no, item_no))
            """);

        // Items a user owns (claimed=1) or was offered by the current stage start (claimed=0).
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS item(
                rowid_alias {Sql.AutoIncrementPrimaryKey},
                user_no {Sql.IntegerColumn}, item_id {Sql.IntegerColumn}, claimed {Sql.IntegerColumn} DEFAULT 0,
                upgrade {Sql.IntegerColumn} DEFAULT 0, attack {Sql.IntegerColumn} DEFAULT 0,
                defence {Sql.IntegerColumn} DEFAULT 0, hp {Sql.IntegerColumn} DEFAULT 0, locked {Sql.IntegerColumn} DEFAULT 0)
            """);

        // The item each hero wears per equipment range (200000 = weapon, ...). No row = starter item.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS hero_equip(
                user_no {Sql.IntegerColumn}, hero_no {Sql.IntegerColumn}, category {Sql.IntegerColumn}, item_no {Sql.IntegerColumn},
                PRIMARY KEY(user_no, hero_no, category))
            """);

        // Counted etc items (19xxxxx: tickets, collectibles, ...). Gold and gems stay on gameuser.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS user_goods(
                user_no {Sql.IntegerColumn}, item_id {Sql.IntegerColumn}, count {Sql.IntegerColumn} DEFAULT 0,
                PRIMARY KEY(user_no, item_id))
            """);

        // Ally gear a player owns (Ally Item Draw); fellow_no 0 = in the bag.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS fellow_item(
                rowid_alias {Sql.AutoIncrementPrimaryKey}, user_no {Sql.IntegerColumn}, fellow_item_id {Sql.IntegerColumn},
                upgrade {Sql.IntegerColumn} DEFAULT 0, locked {Sql.IntegerColumn} DEFAULT 0, fellow_no {Sql.IntegerColumn} DEFAULT 0,
                equip_index {Sql.IntegerColumn} DEFAULT 0)
            """);
        // upgrade_exp: Steel Anvil power-up exp inside the current level (fellow_item_upgrade_exp).
        AddMissingIntegerColumns("fellow_item", [("equip_index", 0), ("upgrade_exp", 0)]);

        // High-water mark for legacy stamina mistakenly stored as counted goods. The original row remains intact.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS stamina_recovery(
                user_no {Sql.IntegerColumn} PRIMARY KEY, credited {Sql.IntegerColumn} DEFAULT 0)
            """);

        // Hunger: the bar at last_gain (unix seconds); points regrow from there. No row = a full bar.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS player_stamina(
                user_no {Sql.IntegerColumn} PRIMARY KEY, stamina {Sql.IntegerColumn}, last_gain {Sql.IntegerColumn})
            """);

        // Login rewards (protocol 67) and the one-time tutorial reward (602/603). Days are UTC day numbers.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS login_reward(
                user_no {Sql.IntegerColumn} PRIMARY KEY, tutorial_claimed {Sql.IntegerColumn} DEFAULT 0,
                last_day {Sql.IntegerColumn} DEFAULT 0, calendar_month {Sql.IntegerColumn} DEFAULT 0,
                calendar_days {Sql.IntegerColumn} DEFAULT 0, gift_day {Sql.IntegerColumn} DEFAULT 0,
                gift_active {Sql.IntegerColumn} DEFAULT 0, gift_used {Sql.IntegerColumn} DEFAULT 0)
            """);

        // Tutorial quest chain (601 reports quest_id, 602/603 finish it). No row = first quest; quest_id 0 = finished.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS tutorial_progress(
                user_no {Sql.IntegerColumn} PRIMARY KEY, quest_id {Sql.IntegerColumn})
            """);

        // Per-quest tutorial reward claims (5000002 = 30 Gems, 5000005 = 1,000 Gold, 5000012 = 1,000 Gold).
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS tutorial_quest_claim(
                user_no {Sql.IntegerColumn}, quest_id {Sql.IntegerColumn}, PRIMARY KEY(user_no, quest_id))
            """);

        // Starter gear has no item row, so its locks live here.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS starter_item_lock(
                user_no {Sql.IntegerColumn}, item_no {Sql.IntegerColumn}, PRIMARY KEY(user_no, item_no))
            """);

        // Friends: status 1 = user_no asked friend_no and waits; 2 = friends (stored in both directions).
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS friend_link(
                user_no {Sql.IntegerColumn}, friend_no {Sql.IntegerColumn}, status {Sql.IntegerColumn},
                created {Sql.IntegerColumn} DEFAULT 0, last_sent {Sql.IntegerColumn} DEFAULT 0,
                PRIMARY KEY(user_no, friend_no))
            """);
        // When user_no last used friend_no as a dungeon helper (the helper then gets Friendship Points by mail).
        AddMissingIntegerColumns("friend_link", [("last_help", 0)]);

        // Daily social caps from dt_etc_setting.json (requests 30/day, deletes 5/day, FP send/recv 100/day).
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS friend_daily_limit(
                user_no {Sql.IntegerColumn}, day_no {Sql.IntegerColumn},
                requests {Sql.IntegerColumn} DEFAULT 0, deletes {Sql.IntegerColumn} DEFAULT 0,
                sent_fp {Sql.IntegerColumn} DEFAULT 0, recv_fp {Sql.IntegerColumn} DEFAULT 0,
                PRIMARY KEY(user_no, day_no))
            """);

        // Ally missions on a player's board. status 0 = open, 1 = running (end_time, the allies and soldiers sent), rewards "item:value;...".
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS fellow_mission(
                rowid_alias {Sql.AutoIncrementPrimaryKey}, user_no {Sql.IntegerColumn}, mission_id {Sql.IntegerColumn},
                status {Sql.IntegerColumn} DEFAULT 0, fellows {Sql.TextColumn}, soldiers {Sql.TextColumn},
                end_time {Sql.IntegerColumn} DEFAULT 0, success_rate {Sql.IntegerColumn} DEFAULT 0, rewards {Sql.TextColumn})
            """);
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS fellow_mission_state(
                user_no {Sql.IntegerColumn} PRIMARY KEY, next_refresh {Sql.IntegerColumn} DEFAULT 0,
                free_soldiers_used {Sql.IntegerColumn} DEFAULT 0)
            """);
        AddMissingIntegerColumns("fellow_mission_state", [("free_soldiers_day", 0), ("refresh_count", 0), ("refresh_day", 0)]);

        session.Execute($"""
            CREATE TABLE IF NOT EXISTS fellow_mission_reputation(
                user_no {Sql.IntegerColumn}, country {Sql.IntegerColumn}, amount {Sql.IntegerColumn} DEFAULT 0,
                PRIMARY KEY(user_no, country))
            """);

        session.Execute($"""
            CREATE TABLE IF NOT EXISTS fellow_mission_tutorial(
                user_no {Sql.IntegerColumn} PRIMARY KEY, status {Sql.IntegerColumn} DEFAULT 0,
                fellows {Sql.TextColumn}, soldiers {Sql.TextColumn}, end_time {Sql.IntegerColumn} DEFAULT 0)
            """);

        // Arena: current standings. Existing records become season 1 without changing player balances.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS pvp_stat(
                user_no {Sql.IntegerColumn} PRIMARY KEY, points {Sql.IntegerColumn} DEFAULT 0, battles {Sql.IntegerColumn} DEFAULT 0,
                wins {Sql.IntegerColumn} DEFAULT 0, streak {Sql.IntegerColumn} DEFAULT 0)
            """);

        session.Execute($"""
            CREATE TABLE IF NOT EXISTS pvp_control(
                id {Sql.IntegerColumn} PRIMARY KEY, generation {Sql.IntegerColumn} NOT NULL,
                season_no {Sql.IntegerColumn} NOT NULL, ends_at {Sql.IntegerColumn} NOT NULL, closed {Sql.IntegerColumn} NOT NULL DEFAULT 0)
            """);
        AddMissingIntegerColumns("pvp_control", [("featured_ally", 0)]);
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS pvp_history(
                generation {Sql.IntegerColumn}, user_no {Sql.IntegerColumn}, points {Sql.IntegerColumn} DEFAULT 0,
                battles {Sql.IntegerColumn} DEFAULT 0, wins {Sql.IntegerColumn} DEFAULT 0, streak {Sql.IntegerColumn} DEFAULT 0,
                PRIMARY KEY(generation,user_no))
            """);
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS pvp_attempt_season(
                user_no {Sql.IntegerColumn} PRIMARY KEY, generation {Sql.IntegerColumn} NOT NULL)
            """);

        session.Execute($"""
            CREATE TABLE IF NOT EXISTS pvp_spirit_clock(
                user_no {Sql.IntegerColumn} PRIMARY KEY, last_gain {Sql.IntegerColumn} NOT NULL)
            """);

        // Raid: what each player has scored against each week's boss.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS raid_score(
                raid_no {Sql.IntegerColumn}, user_no {Sql.IntegerColumn}, score {Sql.IntegerColumn} DEFAULT 0, plays {Sql.IntegerColumn} DEFAULT 0,
                PRIMARY KEY(raid_no, user_no))
            """);

        // A result must match this player's last started opponent and settle only once.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS pvp_play(
                user_no {Sql.IntegerColumn} PRIMARY KEY, opponent_no {Sql.IntegerColumn},
                result {Sql.IntegerColumn} DEFAULT 0, complete {Sql.IntegerColumn} DEFAULT 0)
            """);

        // The last result against each listed opponent (pvp_user_info.pvp_result); cleared when the list refreshes.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS pvp_opponent_result(
                user_no {Sql.IntegerColumn}, opponent_no {Sql.IntegerColumn}, result {Sql.IntegerColumn} DEFAULT 0,
                PRIMARY KEY(user_no, opponent_no))
            """);

        // The opponents currently listed on Battle Opponent, in display order; replaced only when the list rotates.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS pvp_opponent_list(
                user_no {Sql.IntegerColumn}, slot {Sql.IntegerColumn}, opponent_no {Sql.IntegerColumn},
                PRIMARY KEY(user_no, slot))
            """);

        // Arena score missions already paid, per season generation.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS pvp_mission_paid(
                generation {Sql.IntegerColumn}, user_no {Sql.IntegerColumn}, grade {Sql.IntegerColumn},
                PRIMARY KEY(generation, user_no, grade))
            """);

        // Arena season ranking rewards already mailed, per season generation (final rank, participant count and paid Reward 1 kept for 404).
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS pvp_rank_paid(
                generation {Sql.IntegerColumn}, user_no {Sql.IntegerColumn}, grade {Sql.IntegerColumn},
                ranking {Sql.IntegerColumn}, total {Sql.IntegerColumn},
                PRIMARY KEY(generation, user_no))
            """);
        AddMissingIntegerColumns("pvp_rank_paid", [("reward1_item", 0), ("reward1_value", 0)]);

        // Optional global test schedule. Absence keeps the calendar-based raid rotation.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS raid_control(
                id {Sql.IntegerColumn} PRIMARY KEY, raid_no {Sql.IntegerColumn} NOT NULL, ends_at {Sql.IntegerColumn} NOT NULL)
            """);

        // Challenge milestones are per boss; weekly payouts use a frozen ranking and a separate claim marker.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS raid_challenge_progress(
                user_no {Sql.IntegerColumn}, raid_id {Sql.IntegerColumn}, score {Sql.IntegerColumn} DEFAULT 0,
                PRIMARY KEY(user_no, raid_id))
            """);
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS raid_challenge_claim(
                user_no {Sql.IntegerColumn}, challenge_id {Sql.IntegerColumn},
                PRIMARY KEY(user_no, challenge_id))
            """);

        session.Execute($"""
            CREATE TABLE IF NOT EXISTS raid_rank_settlement(
                raid_no {Sql.IntegerColumn} PRIMARY KEY)
            """);
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS raid_rank_snapshot(
                raid_no {Sql.IntegerColumn}, user_no {Sql.IntegerColumn}, rank_no {Sql.IntegerColumn}, total_users {Sql.IntegerColumn},
                PRIMARY KEY(raid_no, user_no))
            """);
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS raid_rank_claim(
                raid_no {Sql.IntegerColumn}, user_no {Sql.IntegerColumn},
                PRIMARY KEY(raid_no, user_no))
            """);

        // One pending/completed raid attempt per player; a result belongs to the week captured at start.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS raid_play(
                user_no {Sql.IntegerColumn} PRIMARY KEY, raid_no {Sql.IntegerColumn},
                score {Sql.IntegerColumn} DEFAULT 0, complete {Sql.IntegerColumn} DEFAULT 0)
            """);

        // Raid admission is per UTC day, independent of weekly scores and quest reward claims.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS raid_daily_entry(
                user_no {Sql.IntegerColumn}, day_no {Sql.IntegerColumn}, entries {Sql.IntegerColumn} DEFAULT 0,
                PRIMARY KEY(user_no, day_no))
            """);

        // Quest progress is isolated by UTC day; historical rows do not affect the current day.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS daily_quest_progress(
                user_no {Sql.IntegerColumn}, day_no {Sql.IntegerColumn}, quest_id {Sql.IntegerColumn},
                quest_count {Sql.IntegerColumn} DEFAULT 0, complete {Sql.IntegerColumn} DEFAULT 0,
                PRIMARY KEY(user_no, day_no, quest_id))
            """);

        // Achievement rewards collected (protocol 123). A rule is one row; its chain predecessor must be present first.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS achievement_claim(
                user_no {Sql.IntegerColumn}, achievement_id {Sql.IntegerColumn},
                PRIMARY KEY(user_no, achievement_id))
            """);

        // Client-counted achievement totals (achievement_list on 72, 93, 116, 408, ...), sent back by protocol 121 at login.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS achievement_progress(
                user_no {Sql.IntegerColumn}, type {Sql.IntegerColumn}, sub_type {Sql.IntegerColumn} DEFAULT 0,
                count {Sql.IntegerColumn} DEFAULT 0,
                PRIMARY KEY(user_no, type, sub_type))
            """);

        session.Execute($"""
            CREATE TABLE IF NOT EXISTS story_completion(
                user_no {Sql.IntegerColumn}, story_id {Sql.IntegerColumn},
                PRIMARY KEY(user_no, story_id))
            """);

        session.Execute($"""
            CREATE TABLE IF NOT EXISTS story_progress(
                user_no {Sql.IntegerColumn}, story_id {Sql.IntegerColumn}, quest_count {Sql.IntegerColumn} DEFAULT 0,
                PRIMARY KEY(user_no, story_id))
            """);

        // When each player's Black Market stock expires (unix seconds).
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS black_market(
                user_no {Sql.IntegerColumn} PRIMARY KEY, next_refresh {Sql.IntegerColumn} DEFAULT 0)
            """);

        // Options (enchantments) of an item, up to 6 slots.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS item_option(
                item_no {Sql.IntegerColumn}, slot {Sql.IntegerColumn}, user_no {Sql.IntegerColumn}, option_id {Sql.IntegerColumn},
                value1 {Sql.IntegerColumn}, value2 {Sql.IntegerColumn}, PRIMARY KEY(item_no, slot))
            """);

        AddMissingIntegerColumns("item", [("upgrade", 0), ("attack", 0), ("defence", 0), ("hp", 0), ("locked", 0)]);
        AddMissingIntegerColumns("pvp_stat", [("refresh_until", 0), ("refresh_seed", 0), ("recovery_count", 0), ("recovery_day", 0), ("streak_until", 0)]);
        // last_seen: last authorized request (Last Played). last_sync: server_time sent by protocol 60, the client's clock base.
        AddMissingIntegerColumns("gameuser", [("last_seen", 0), ("last_sync", 0)]);
    }

    private void CreateFellowTables()
    {
        // Fellows ("allies") a user owns.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS fellow(
                rowid_alias {Sql.AutoIncrementPrimaryKey},
                user_no {Sql.IntegerColumn}, fellow_id {Sql.IntegerColumn}, level {Sql.IntegerColumn} DEFAULT 1, exp {Sql.IntegerColumn} DEFAULT 0,
                upgrade {Sql.IntegerColumn} DEFAULT 0, locked {Sql.IntegerColumn} DEFAULT 0)
            """);

        // One row per random ally grant. Written in the same transaction as payment and ownership.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS fellow_draw_audit(
                fellow_no {Sql.IntegerColumn} PRIMARY KEY, batch_id {Sql.KeyTextColumn}, drawn_at {Sql.IntegerColumn},
                user_no {Sql.IntegerColumn}, username {Sql.TextColumn}, nickname {Sql.TextColumn},
                source {Sql.KeyTextColumn}, package_id {Sql.IntegerColumn}, payment_item {Sql.IntegerColumn}, payment_count {Sql.IntegerColumn},
                fellow_id {Sql.IntegerColumn}, fellow_name {Sql.TextColumn}, grade {Sql.IntegerColumn})
            """);

        // The per-user collection record: one row per fellow_id ever owned.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS fellow_book(
                user_no {Sql.IntegerColumn}, fellow_id {Sql.IntegerColumn}, recv_reward {Sql.IntegerColumn} DEFAULT 0,
                exp {Sql.IntegerColumn} DEFAULT 0, level {Sql.IntegerColumn} DEFAULT 1, PRIMARY KEY(user_no, fellow_id))
            """);

        // Active fellow slots: one row per (setup type 1-3, position 0-2); fellow_no 0 = empty.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS fellow_active(
                user_no {Sql.IntegerColumn}, type {Sql.IntegerColumn}, position {Sql.IntegerColumn}, fellow_no {Sql.IntegerColumn},
                PRIMARY KEY(user_no, type, position))
            """);

        // fail_count = the Power-up Points a failed power-up adds to the next try; attack_bonus = the "+N" the power-ups and enhances carry over.
        // awaken: evolutions done; max_level_bonus: MAX Level they added; talent1/2: mission skills (0 = the master default).
        AddMissingIntegerColumns("story_progress", [("quest2_count", 0), ("quest3_count", 0), ("quest4_count", 0)]);
        AddMissingIntegerColumns("fellow", [("fail_count", 0), ("attack_bonus", 0), ("awaken", 0), ("max_level_bonus", 0), ("talent1", 0), ("talent2", 0)]);
    }

    private void CreateStageTables()
    {
        // Regular event calendar saved by Studio "Open all event dungeons"; restored by "Restore regular schedule".
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS event_schedule_backup(
                stage_id {Sql.IntegerColumn} PRIMARY KEY, start_time {Sql.IntegerColumn} NOT NULL, end_time {Sql.IntegerColumn} NOT NULL,
                duration_seconds {Sql.IntegerColumn} NOT NULL, period_seconds {Sql.IntegerColumn} NOT NULL)
            """);
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS event_stage_play(
                user_no {Sql.IntegerColumn} PRIMARY KEY, stage_id {Sql.IntegerColumn}, complete {Sql.IntegerColumn} DEFAULT 0)
            """);

        // The counted etc items the current stage start offered. Replaced on every stage start.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS stage_event_offer(
                rowid_alias {Sql.AutoIncrementPrimaryKey},
                user_no {Sql.IntegerColumn}, item_id {Sql.IntegerColumn}, count {Sql.IntegerColumn})
            """);

        // Area-clear rewards a user has claimed, by area and stage mode.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS stage_area_reward(
                user_no {Sql.IntegerColumn}, area_id {Sql.IntegerColumn}, stage_mode {Sql.IntegerColumn},
                PRIMARY KEY(user_no, area_id, stage_mode))
            """);

        // Account buffs bought in the Hero Point shop (buff_etc_info), e.g. type 5 Auto Skill.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS user_buff(
                user_no {Sql.IntegerColumn}, buff_type {Sql.IntegerColumn}, end_time {Sql.IntegerColumn} DEFAULT 0,
                PRIMARY KEY(user_no, buff_type))
            """);

        // Hero Point stamina upgrades bought: MAX Stamina (901) and Recovery Time (902) counts.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS stamina_upgrade(
                user_no {Sql.IntegerColumn} PRIMARY KEY, max_count {Sql.IntegerColumn} DEFAULT 0, time_count {Sql.IntegerColumn} DEFAULT 0)
            """);

        // The reward slots the user's last stage start rolled.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS stage_roll(
                user_no {Sql.IntegerColumn}, slot {Sql.IntegerColumn}, item_id {Sql.IntegerColumn}, count {Sql.IntegerColumn},
                PRIMARY KEY(user_no, slot))
            """);

        // An open stage start (71); the result (72) consumes it, so the reported gold is paid once per start.
        session.Execute($"""
            CREATE TABLE IF NOT EXISTS stage_start(
                user_no {Sql.IntegerColumn} PRIMARY KEY, stage_id {Sql.IntegerColumn})
            """);
        AddMissingIntegerColumns("stage_start", [("start_time", 0), ("stamina_cost", 0), ("stage_mode", 1)]);
    }

    private void AddMissingIntegerColumns(string table, IEnumerable<(string Column, long Default)> columns)
    {
        var existing = session.GetColumnNames(table).ToHashSet(StringComparer.Ordinal);
        foreach ((string column, long defaultValue) in columns)
        {
            if (!existing.Contains(column))
            {
                session.Execute($"ALTER TABLE {table} ADD COLUMN {column} {Sql.IntegerColumn} DEFAULT {defaultValue}");
            }
        }
    }

    private void AddMissingTextColumns(string table, IEnumerable<string> columns)
    {
        var existing = session.GetColumnNames(table).ToHashSet(StringComparer.Ordinal);
        foreach (string column in columns)
        {
            if (!existing.Contains(column))
            {
                session.Execute($"ALTER TABLE {table} ADD COLUMN {column} {Sql.TextColumn}");
            }
        }
    }
}
