using KnS.Data.Repositories;
using KnS.Game.Models;
using KnS.Game.Rules;

namespace KnS.Game.Players;

/// <summary>A player's account: wallet, level, exp and the user_info / goods views of it.</summary>
public sealed class PlayerProfileService(
    PlayerRepository players,
    GoodsRepository goods,
    BlackMarketRepository blackMarket,
    MasterDataRepository masterData,
    MailRepository mail,
    BuffRepository buffs,
    TimeProvider clock)
{
    public PlayerAccount GetAccount(long userNumber)
    {
        PlayerAccountRecord? record = players.FindAccount(userNumber);
        if (record is null)
        {
            return PlayerAccount.NewPlayer(MaxExperienceAt(Data.StoredPlayerDefaults.Level));
        }

        return new PlayerAccount(
            record.Gold, record.Gems, record.HeroPcid, record.Level, record.Experience,
            MaxExperienceAt(record.Level), record.PurchasedItemSlots, record.PurchasedAllySlots);
    }

    /// <summary>Adds (or, with negative amounts, takes) gold and gems.</summary>
    public void AddCurrency(long userNumber, long gold = 0, long gems = 0) =>
        players.AddCurrency(userNumber, gold, gems);

    /// <summary>Takes a price from the wallet (gold or gems; other price items cost nothing here).</summary>
    public void Pay(long userNumber, Price price) =>
        players.AddCurrency(
            userNumber,
            gold: price.PriceItemId == CurrencyItems.Gold ? -price.Amount : 0,
            gems: price.PriceItemId == CurrencyItems.Gems ? -price.Amount : 0);

    /// <summary>The bar's capacity and seconds per point, with the player's Hero Point stamina upgrades.</summary>
    public (long Max, long RefillSeconds) GetStaminaLimits(long userNumber)
    {
        (long maxCount, long timeCount) = buffs.GetStaminaUpgrades(userNumber);
        return (PlayerRules.MaxStaminaWithUpgrades(BaseMaxStaminaAt(GetAccount(userNumber).Level), maxCount), PlayerRules.StaminaRefillSecondsAfter(timeCount));
    }

    /// <summary>The hunger bar now: the stored value plus a point per elapsed refill period, capped at full.</summary>
    public (long Stamina, long LastGain) GetStamina(long userNumber)
    {
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        (long max, long refill) = GetStaminaLimits(userNumber);
        long legacy = goods.ReserveLegacyStamina(userNumber, CurrencyItems.Stamina);
        StaminaRecord? stored = players.FindStamina(userNumber);
        if (stored is not null && stored.LastGain > now)
        {
            players.SetStamina(userNumber, stored.Stamina, now);
            stored = stored with { LastGain = now };
        }
        long current = stored is null ? max : stored.Stamina + Math.Max(0, now - stored.LastGain) / refill;
        long lastGain = stored is null || current >= max
            ? now
            : stored.LastGain + (Math.Max(0, now - stored.LastGain) / refill * refill);
        if (current >= max && stored is not null) current = Math.Max(stored.Stamina, max);
        if (legacy > 0)
        {
            current = checked(current + legacy);
            players.SetStamina(userNumber, current, lastGain);
        }
        return (current, lastGain);
    }

    /// <summary>Stores the bar and its refill clock as settled by <see cref="GetStamina"/>.</summary>
    public void SetStamina(long userNumber, long stamina, long lastGain) => players.SetStamina(userNumber, stamina, lastGain);

    /// <summary>Takes a stage's hunger if the bar has it. The refill clock keeps its phase unless the bar was full.</summary>
    public bool TrySpendStamina(long userNumber, long cost)
    {
        (long stamina, long lastGain) = GetStamina(userNumber);
        if (stamina < cost)
        {
            return false;
        }

        players.SetStamina(userNumber, stamina - cost, lastGain);
        return true;
    }

    /// <summary>Gives back stamina a stage start took (a quit in the first seconds). The refill clock keeps its phase.</summary>
    public void RefundStamina(long userNumber, long cost)
    {
        if (cost <= 0) return;
        (long stamina, long lastGain) = GetStamina(userNumber);
        players.SetStamina(userNumber, stamina + cost, lastGain);
    }

    /// <summary>Takes an amount of any currency if the player has it: gold, gems or a counted item. Returns false otherwise.</summary>
    /// <summary>How much of a currency or counted item the player has.</summary>
    public long GetBalance(long userNumber, long itemId)
    {
        PlayerAccount account = GetAccount(userNumber);
        return itemId switch
        {
            CurrencyItems.Gold => account.Gold,
            CurrencyItems.Gems => account.Gems,
            _ => goods.GetAll(userNumber).FirstOrDefault(row => row.ItemId == itemId)?.Count ?? 0,
        };
    }

    public bool TrySpend(long userNumber, long priceItemId, long amount)
    {
        long balance = GetBalance(userNumber, priceItemId);
        if (balance < amount)
        {
            return false;
        }

        switch (priceItemId)
        {
            case CurrencyItems.Gold:
                AddCurrency(userNumber, gold: -amount);
                break;
            case CurrencyItems.Gems:
                AddCurrency(userNumber, gems: -amount);
                break;
            default:
                goods.Add(userNumber, priceItemId, -amount);
                break;
        }

        return true;
    }

    /// <summary>Awards a cleared stage's exp, levelling up while the bar is full. Returns the exp gained.</summary>
    public long AwardStageExperience(long userNumber, long stageId)
    {
        long stageExperience = masterData.GetStageExperience(stageId) ?? 0;
        return AwardExperience(userNumber, ApplyExpBoost(userNumber, stageExperience));
    }

    /// <summary>Applies the active Book of Secrets +50% EXP boost (buff type 2), if running.</summary>
    public long ApplyExpBoost(long userNumber, long baseExperience) =>
        Protocols.Items.ExpBoostRules.Apply(baseExperience,
            buffs.IsActive(userNumber, Protocols.Items.ExpBoostRules.BuffType, clock.GetUtcNow().ToUnixTimeSeconds()));

    /// <summary>Awards hero experience and applies level-ups.</summary>
    public long AwardExperience(long userNumber, long amount)
    {
        long gain = Math.Max(0, amount);
        Dictionary<long, long> experienceToNext = masterData.GetExperienceTable();

        PlayerAccount account = GetAccount(userNumber);
        long level = account.Level;
        long experience = account.Experience + gain;
        // The last level never fills.
        while (experienceToNext.ContainsKey(level + 1) && experience >= experienceToNext[level])
        {
            experience -= experienceToNext[level];
            level++;
        }

        // Settle at the old cap before changing levels, so elapsed regeneration and overflow are preserved.
        (long stamina, long lastGain) = level > account.Level ? GetStamina(userNumber) : (0, 0);
        players.SetLevel(userNumber, level, experience);
        if (level > account.Level)
        {
            long max = GetStaminaLimits(userNumber).Max;
            players.SetStamina(userNumber, Math.Max(stamina, max), stamina < max ? clock.GetUtcNow().ToUnixTimeSeconds() : lastGain);
        }
        return gain;
    }

    public UserInfo BuildUserInfo(Player player)
    {
        PlayerAccount account = GetAccount(player.UserNumber);
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        (long stamina, long lastGain) = GetStamina(player.UserNumber);
        (long max, long refill) = GetStaminaLimits(player.UserNumber);
        (long maxUpgrades, long timeUpgrades) = buffs.GetStaminaUpgrades(player.UserNumber);
        return new UserInfo
        {
            UserNumber = player.UserNumber,
            HubId = player.Nickname,
            Nickname = player.Nickname,
            Level = account.Level,
            Experience = account.Experience,
            MaxExperience = account.MaxExperience,
            Gold = account.Gold,
            Gems = account.Gems,
            Stamina = stamina,
            // The client adds the MAX Stamina bonus itself from user_max_stamina_buff (UpdateUserInfo@0x14ad90).
            MaxStamina = BaseMaxStaminaAt(account.Level),
            MaxStaminaBuff = maxUpgrades,
            StaminaTimeBuff = timeUpgrades,
            StaminaRefillSeconds = stamina >= max ? 0 : Math.Max(0, lastGain + refill - now),
            FreeItemSlots = PlayerRules.FreeItemSlots,
            PurchasedItemSlots = account.PurchasedItemSlots,
            PurchasedAllySlots = account.PurchasedAllySlots,
            ActiveHeroPcid = account.HeroPcid,
            HeroAttack = PlayerRules.HeroAttack,
            HeroDefence = PlayerRules.HeroDefence,
            HeroHp = PlayerRules.HeroHp,
            NextShopUpdateTime = Math.Max(now, blackMarket.GetNextRefresh(player.UserNumber)),
            NewMail = mail.Count(player.UserNumber) > 0 ? 1 : 0,
        };
    }

    /// <summary>
    /// item_etc_info: gold and gems first, then the counted items. The client keeps only the ids
    /// it knows and ignores the rest.
    /// </summary>
    public IReadOnlyList<GoodsEntry> GetGoods(long userNumber)
    {
        PlayerAccount account = GetAccount(userNumber);
        var entries = new List<GoodsEntry>
        {
            new(CurrencyItems.Gold, account.Gold),
            new(CurrencyItems.Gems, account.Gems),
        };
        (long stamina, long lastGain) = GetStamina(userNumber);
        entries.Add(new GoodsEntry(CurrencyItems.Stamina, stamina) { LastStaminaGain = lastGain });
        long spiritClock = goods.ArenaSpiritClock(userNumber, clock.GetUtcNow().ToUnixTimeSeconds());
        entries.AddRange(goods.GetAll(userNumber).Where(row => row.ItemId != CurrencyItems.Stamina)
            .Select(row => new GoodsEntry(row.ItemId, row.Count) { LastStaminaGain = row.ItemId == 1900017 ? spiritClock : 0 }));
        return entries;
    }

    private long MaxExperienceAt(long level) =>
        masterData.GetExperienceToNextLevel(level) ?? PlayerRules.FallbackMaxExperience;

    private long BaseMaxStaminaAt(long level) =>
        masterData.GetMaxStamina(level) ?? PlayerRules.BaseMaxStamina(level);
}
