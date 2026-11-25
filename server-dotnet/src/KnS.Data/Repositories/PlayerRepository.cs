namespace KnS.Data.Repositories;

/// <summary>A registered player (gameuser row) as the login and lookup code needs it.</summary>
public sealed record PlayerRecord
{
    public long UserNumber { get; init; }

    /// <summary>The Hub uid the player registered with ("-1" for Hive guest logins).</summary>
    public string? HubUid { get; init; }

    public string? Nickname { get; init; }
}

/// <summary>A player's wallet and progress, as stored on their gameuser row.</summary>
public sealed record PlayerAccountRecord
{
    public long Gold { get; init; }
    public long Gems { get; init; }
    public long HeroPcid { get; init; }
    public long Level { get; init; }
    public long Experience { get; init; }

    /// <summary>Item bag slots bought on top of the free ones (user_inventory_size_cash).</summary>
    public long PurchasedItemSlots { get; init; }

    /// <summary>Ally bag slots bought (user_fellow_inventory_size_cash).</summary>
    public long PurchasedAllySlots { get; init; }

    /// <summary>Ally equipment bag slots bought (user_fellow_item_inventory_size_cash).</summary>
    public long PurchasedAllyItemSlots { get; init; }
}

public sealed record StaminaRecord
{
    public long Stamina { get; init; }
    public long LastGain { get; init; }
}

/// <summary>The three bags a player can buy slots for.</summary>
public enum BagKind
{
    Items,
    Allies,
    FellowItems,
}

/// <summary>Players (gameuser) and the extra devices linked to them (device_link).</summary>
public sealed class PlayerRepository(DataSession session)
{
    private const string PlayerColumns = "user_no AS UserNumber, hub_uid AS HubUid, nickname AS Nickname";

    /// <summary>Records activity at most once a minute.</summary>
    public void TouchSeen(long userNumber, long now) => session.Execute(
        "UPDATE gameuser SET last_seen=@now WHERE user_no=@userNumber AND COALESCE(last_seen,0)<@now-60", new { userNumber, now });

    /// <summary>Protocol 60 sent server_time: the client's clock base for "Last Played".</summary>
    public void MarkSync(long userNumber, long now) => session.Execute(
        "UPDATE gameuser SET last_sync=@now, last_seen=@now WHERE user_no=@userNumber", new { userNumber, now });

    /// <summary>(last activity or creation time, last protocol 60 time) in unix seconds.</summary>
    public (long Seen, long Sync) GetActivity(long userNumber)
    {
        var row = session.QueryFirstOrDefault<ActivityRow>(
            "SELECT COALESCE(last_seen,0) AS Seen, COALESCE(last_sync,0) AS Sync, COALESCE(created,0) AS Created FROM gameuser WHERE user_no=@userNumber",
            new { userNumber });
        return row is null ? (0, 0) : (row.Seen > 0 ? row.Seen : (long)row.Created, row.Sync);
    }

    private sealed record ActivityRow
    {
        public long Seen { get; init; }
        public long Sync { get; init; }
        public double Created { get; init; }
    }

    public PlayerRecord? FindByUserNumber(long userNumber) =>
        session.QueryFirstOrDefault<PlayerRecord>(
            $"SELECT {PlayerColumns} FROM gameuser WHERE user_no=@userNumber", new { userNumber });

    /// <summary>The player a linked extra device plays as (e.g. the KnS Dev app next to the normal one).</summary>
    public PlayerRecord? FindByLinkedDevice(string deviceId) =>
        session.QueryFirstOrDefault<PlayerRecord>(
            "SELECT g.user_no AS UserNumber, g.hub_uid AS HubUid, g.nickname AS Nickname FROM device_link d"
            + " JOIN gameuser g ON g.user_no=d.user_no WHERE d.hub_did=@deviceId",
            new { deviceId });

    public PlayerRecord? FindByDevice(string deviceId) =>
        session.QueryFirstOrDefault<PlayerRecord>(
            $"SELECT {PlayerColumns} FROM gameuser WHERE hub_did=@deviceId", new { deviceId });

    /// <summary>The device a player registered with, or null for an unknown user number.</summary>
    public string? FindDeviceId(long userNumber) =>
        session.QueryFirstOrDefault<string>("SELECT hub_did FROM gameuser WHERE user_no=@userNumber", new { userNumber });

    /// <summary>Other real players, newest first (for friend suggestions).</summary>
    public IReadOnlyList<PlayerRecord> ListOthers(long userNumber, int limit) =>
        session.Query<PlayerRecord>(
            $"SELECT {PlayerColumns} FROM gameuser WHERE user_no>0 AND user_no<>@userNumber ORDER BY user_no DESC LIMIT @limit",
            new { userNumber, limit });

    /// <summary>Players whose stored nickname matches (case-insensitive); the default "Squire&lt;no&gt;" names are matched by the caller.</summary>
    public IReadOnlyList<PlayerRecord> FindByNickname(string nickname) =>
        session.Query<PlayerRecord>(
            $"SELECT {PlayerColumns} FROM gameuser WHERE user_no>0 AND LOWER(nickname)=LOWER(@nickname)", new { nickname });

    /// <summary>Updates one registered player's displayed name.</summary>
    public void SetNickname(long userNumber, string nickname) =>
        session.Execute("UPDATE gameuser SET nickname=@nickname WHERE user_no=@userNumber", new { nickname, userNumber });

    /// <summary>True when another player already displays this name (case-insensitive); empty names never count.</summary>
    public bool IsNicknameTaken(string nickname) =>
        nickname.Length > 0
        && session.Exists("SELECT 1 FROM gameuser WHERE user_no>0 AND LOWER(nickname)=LOWER(@nickname)", new { nickname });

    /// <summary>True when a different player already displays this name (case-insensitive); empty names never count.</summary>
    public bool IsNicknameTakenByOther(string nickname, long excludeUserNumber) =>
        nickname.Length > 0
        && session.Exists(
            "SELECT 1 FROM gameuser WHERE user_no>0 AND user_no<>@excludeUserNumber AND LOWER(nickname)=LOWER(@nickname)",
            new { nickname, excludeUserNumber });

    public PlayerRecord? FindNewest() =>
        session.QueryFirstOrDefault<PlayerRecord>(
            $"SELECT {PlayerColumns} FROM gameuser WHERE user_no>0 ORDER BY user_no DESC LIMIT 1");

    /// <summary>Registers a device and returns the new player's user number.</summary>
    public long Create(string deviceId, string hubUid, double createdAt, string nickname = "")
    {
        long rowId = session.Insert(
            "INSERT INTO gameuser(user_no, hub_did, hub_uid, nickname, created)"
            + " VALUES(0, @deviceId, @hubUid, @nickname, @createdAt)",
            new { deviceId, hubUid, nickname, createdAt });
        long userNumber = WireIdentifiers.UserNumberOffset + rowId;
        session.Execute("UPDATE gameuser SET user_no=@userNumber WHERE rowid_alias=@rowId", new { userNumber, rowId });
        return userNumber;
    }

    public PlayerAccountRecord? FindAccount(long userNumber) =>
        session.QueryFirstOrDefault<PlayerAccountRecord>(
            "SELECT gold AS Gold, cash AS Gems, hero_pcid AS HeroPcid, level AS Level, exp AS Experience,"
            + " inventory_cash AS PurchasedItemSlots, fellow_inventory_cash AS PurchasedAllySlots,"
            + " fellow_item_inventory_cash AS PurchasedAllyItemSlots"
            + " FROM gameuser WHERE user_no=@userNumber",
            new { userNumber });

    /// <summary>Reads gold and locks the wallet through the active write transaction.</summary>
    public long GoldForUpdate(long userNumber)
    {
        string locking = session.Dialect is KnS.Data.Dialects.MySqlDialect ? " FOR UPDATE" : "";
        return session.QueryFirstOrDefault<long>("SELECT gold FROM gameuser WHERE user_no=@userNumber" + locking,
            new { userNumber });
    }

    /// <summary>Adds (or, with negative amounts, takes) gold and gems.</summary>
    public void AddCurrency(long userNumber, long gold = 0, long gems = 0)
    {
        session.Execute(
            "UPDATE gameuser SET gold=gold+@gold, cash=cash+@gems WHERE user_no=@userNumber",
            new { gold, gems, userNumber });
        if (gold > 0) new AchievementRepository(session).AddProgress(userNumber, 40009, gold);
    }

    /// <summary>Takes gems when the player has enough; false (nothing taken) otherwise.</summary>
    public bool TrySpendGems(long userNumber, long gems)
    {
        if ((FindAccount(userNumber)?.Gems ?? 0) < gems)
        {
            return false;
        }

        AddCurrency(userNumber, gems: -gems);
        return true;
    }

    /// <summary>The stored hunger bar and the time it was stored; null while the bar has never been spent.</summary>
    public StaminaRecord? FindStamina(long userNumber) =>
        session.QueryFirstOrDefault<StaminaRecord>(
            "SELECT stamina AS Stamina, last_gain AS LastGain FROM player_stamina WHERE user_no=@userNumber", new { userNumber });

    public void SetStamina(long userNumber, long stamina, long lastGain) =>
        session.Execute(
            $"{session.Dialect.InsertOrReplace} INTO player_stamina(user_no, stamina, last_gain) VALUES(@userNumber, @stamina, @lastGain)",
            new { userNumber, stamina, lastGain });

    /// <summary>Writes a new player's whole start: wallet, level, active hero and bag slots.</summary>
    public void SetStart(long userNumber, long gold, long gems, long level) =>
        session.Execute(
            "UPDATE gameuser SET gold=@gold, cash=@gems, level=@level, exp=0, hero_pcid=@heroPcid, inventory_cash=0, fellow_inventory_cash=0, fellow_item_inventory_cash=0"
            + " WHERE user_no=@userNumber",
            new { gold, gems, level, heroPcid = StoredPlayerDefaults.HeroPcid, userNumber });

    public void SetLevel(long userNumber, long level, long experience) =>
        session.Execute(
            "UPDATE gameuser SET level=@level, exp=@experience WHERE user_no=@userNumber",
            new { level, experience, userNumber });

    public void SetActiveHero(long userNumber, long heroPcid) =>
        session.Execute("UPDATE gameuser SET hero_pcid=@heroPcid WHERE user_no=@userNumber", new { heroPcid, userNumber });

    public void SetPurchasedSlots(long userNumber, BagKind bag, long purchasedSlots)
    {
        string column = bag switch
        {
            BagKind.Items => "inventory_cash",
            BagKind.Allies => "fellow_inventory_cash",
            BagKind.FellowItems => "fellow_item_inventory_cash",
            _ => throw new ArgumentOutOfRangeException(nameof(bag)),
        };
        session.Execute($"UPDATE gameuser SET {column}=@purchasedSlots WHERE user_no=@userNumber", new { purchasedSlots, userNumber });
    }
}
