namespace KnS.Data.Repositories;

public sealed record FellowRecord
{
    public long FellowNumber { get; init; }
    public long FellowId { get; init; }
    public long Level { get; init; }
    public long Experience { get; init; }
    public long Upgrade { get; init; }
    public long Locked { get; init; }

    /// <summary>The level cap, from master_fellow.</summary>
    public long MaxLevel { get; init; }

    public long FailCount { get; init; }
    public long AttackBonus { get; init; }

    /// <summary>Evolutions done (fellow_awaken_level).</summary>
    public long Awaken { get; init; }

    /// <summary>Stored talents (mission skills); 0 = the master default.</summary>
    public long Talent1 { get; init; }
    public long Talent2 { get; init; }
}

/// <summary>A fellow with what power-up and enhance (303 / 307) read: its grade and its upgrade state.</summary>
public sealed record FellowUpgradeRecord
{
    public long FellowNumber { get; init; }
    public long FellowId { get; init; }
    public long Grade { get; init; }
    public long Level { get; init; }
    public long MaxLevel { get; init; }
    public long Upgrade { get; init; }
    public long Locked { get; init; }
    public long FailCount { get; init; }
    public long AttackBonus { get; init; }
    public long Awaken { get; init; }
}

public sealed record FellowProgressRecord
{
    public long FellowNumber { get; init; }
    public long Level { get; init; }
    public long Experience { get; init; }
    public long Grade { get; init; }
    public long MaxLevel { get; init; }
}

public sealed record FellowBookRecord
{
    public long FellowId { get; init; }
    public long RewardReceived { get; init; }
    public long Experience { get; init; }
    public long Level { get; init; }
}

public sealed record FellowSlotRecord
{
    /// <summary>The setup (1-3) the slot belongs to; dungeons use setup 3.</summary>
    public long SetupType { get; init; }

    public long Position { get; init; }
    public long FellowNumber { get; init; }
}

public sealed record FellowSaleRecord
{
    public long SellPrice { get; init; }
    public long Locked { get; init; }
}

/// <summary>
/// Fellows ("allies") a player owns (fellow), their collection book (fellow_book) and the active
/// slots (fellow_active). Fellow numbers are the client's ids (<see cref="WireIdentifiers.FellowNumberOffset"/>).
/// </summary>
public sealed class FellowRepository(DataSession session)
{
    private const long Offset = WireIdentifiers.FellowNumberOffset;

    /// <summary>Every owned fellow, oldest first.</summary>
    public IReadOnlyList<FellowRecord> GetOwned(long userNumber) =>
        session.Query<FellowRecord>(
            "SELECT f.rowid_alias + @offset AS FellowNumber, f.fellow_id AS FellowId, f.level AS Level, f.exp AS Experience,"
            + " f.upgrade AS Upgrade, f.locked AS Locked, (m.max_level + f.max_level_bonus) AS MaxLevel, f.fail_count AS FailCount, f.attack_bonus AS AttackBonus,"
            + " f.awaken AS Awaken, f.talent1 AS Talent1, f.talent2 AS Talent2"
            + " FROM fellow f JOIN master_fellow m ON m.fellow_id=f.fellow_id WHERE f.user_no=@userNumber"
            + " ORDER BY f.rowid_alias",
            new { offset = Offset, userNumber });

    public HashSet<long> GetOwnedFellowNumbers(long userNumber) =>
        [.. session.Query<long>("SELECT rowid_alias + @offset FROM fellow WHERE user_no=@userNumber", new { offset = Offset, userNumber })];

    /// <summary>Adds a fellow and returns its fellow number.</summary>
    public long Add(long userNumber, long fellowId, long level) =>
        session.Insert(
            "INSERT INTO fellow(user_no, fellow_id, level) VALUES(@userNumber, @fellowId, @level)",
            new { userNumber, fellowId, level }) + Offset;

    public void Delete(long fellowNumber)
    {
        RememberBookProgress(fellowNumber);
        session.Execute("DELETE FROM fellow WHERE rowid_alias=@rowId", new { rowId = fellowNumber - Offset });
    }

    public FellowProgressRecord? FindProgress(long userNumber, long fellowNumber) =>
        session.QueryFirstOrDefault<FellowProgressRecord>(
            "SELECT f.rowid_alias + @offset AS FellowNumber, f.level AS Level, f.exp AS Experience, m.grade AS Grade, (m.max_level + f.max_level_bonus) AS MaxLevel"
            + " FROM fellow f JOIN master_fellow m ON m.fellow_id=f.fellow_id"
            + " WHERE f.rowid_alias=@rowId AND f.user_no=@userNumber",
            new { offset = Offset, rowId = fellowNumber - Offset, userNumber });

    public void SetProgress(long fellowNumber, long level, long experience)
    {
        session.Execute(
            "UPDATE fellow SET level=@level, exp=@experience WHERE rowid_alias=@rowId",
            new { level, experience, rowId = fellowNumber - Offset });
        RememberBookProgress(fellowNumber);
    }

    private sealed record BookProgress(long UserNumber, long FellowId, long Level, long Experience);

    private void RememberBookProgress(long fellowNumber)
    {
        BookProgress? progress = session.QueryFirstOrDefault<BookProgress>(
            "SELECT user_no AS UserNumber, fellow_id AS FellowId, level AS Level, exp AS Experience FROM fellow WHERE rowid_alias=@rowId",
            new { rowId = fellowNumber - Offset });
        if (progress is null) return;
        RecordInBook(progress.UserNumber, progress.FellowId);
        session.Execute(
            "UPDATE fellow_book SET level=@Level, exp=@Experience WHERE user_no=@UserNumber AND fellow_id=@FellowId"
            + " AND (level<@Level OR (level=@Level AND exp<@Experience))", progress);
    }

    /// <summary>Collection rewards use the species' base cap and survive sale or consumption.</summary>
    public bool HasReachedBookMaxLevel(long userNumber, long fellowId) =>
        session.QueryFirstOrDefault<long>(
            "SELECT COUNT(*) FROM master_fellow m WHERE m.fellow_id=@fellowId AND m.max_level>0 AND ("
            + " EXISTS (SELECT 1 FROM fellow_book b WHERE b.user_no=@userNumber AND b.fellow_id=m.fellow_id AND b.level>=m.max_level)"
            + " OR EXISTS (SELECT 1 FROM fellow f WHERE f.user_no=@userNumber AND f.fellow_id=m.fellow_id AND f.level>=m.max_level))",
            new { userNumber, fellowId }) > 0;

    /// <summary>The sale price (master_fellow_upgrade.sell_price for the fellow's grade and upgrade) and lock flag.</summary>
    public FellowSaleRecord? FindSaleInfo(long userNumber, long fellowNumber) =>
        session.QueryFirstOrDefault<FellowSaleRecord>(
            "SELECT price.sell_price AS SellPrice, f.locked AS Locked FROM fellow f"
            + " JOIN master_fellow m ON m.fellow_id=f.fellow_id"
            + " JOIN master_fellow_upgrade price ON price.grade=m.grade AND price.upgrade=f.upgrade"
            + " WHERE f.rowid_alias=@rowId AND f.user_no=@userNumber",
            new { rowId = fellowNumber - Offset, userNumber });

    private const string UpgradeColumns =
        "SELECT f.rowid_alias + @offset AS FellowNumber, f.fellow_id AS FellowId, m.grade AS Grade, f.level AS Level,"
        + " (m.max_level + f.max_level_bonus) AS MaxLevel, f.upgrade AS Upgrade, f.locked AS Locked, f.fail_count AS FailCount, f.attack_bonus AS AttackBonus, f.awaken AS Awaken"
        + " FROM fellow f JOIN master_fellow m ON m.fellow_id=f.fellow_id WHERE f.user_no=@userNumber";

    public FellowUpgradeRecord? FindForUpgrade(long userNumber, long fellowNumber) =>
        session.QueryFirstOrDefault<FellowUpgradeRecord>(UpgradeColumns + " AND f.rowid_alias=@rowId",
            new { offset = Offset, rowId = fellowNumber - Offset, userNumber });

    public IReadOnlyList<FellowUpgradeRecord> GetOwnedForUpgrade(long userNumber) =>
        session.Query<FellowUpgradeRecord>(UpgradeColumns + " ORDER BY f.rowid_alias",
            new { offset = Offset, userNumber });

    public void SetUpgradeState(long fellowNumber, long upgrade, long failCount, long attackBonus) =>
        session.Execute(
            "UPDATE fellow SET upgrade=@upgrade, fail_count=@failCount, attack_bonus=@attackBonus WHERE rowid_alias=@rowId",
            new { upgrade, failCount, attackBonus, rowId = fellowNumber - Offset });

    /// <summary>Records one more evolution and the MAX Level it added.</summary>
    public void AddEvolution(long fellowNumber, long maxLevelBonus) =>
        session.Execute(
            "UPDATE fellow SET awaken=awaken+1, max_level_bonus=max_level_bonus+@maxLevelBonus WHERE rowid_alias=@rowId",
            new { maxLevelBonus, rowId = fellowNumber - Offset });

    /// <summary>Stores talent <paramref name="slot"/> (1 or 2).</summary>
    public void SetTalent(long fellowNumber, long slot, long talent) =>
        session.Execute(
            slot == 1 ? "UPDATE fellow SET talent1=@talent WHERE rowid_alias=@rowId" : "UPDATE fellow SET talent2=@talent WHERE rowid_alias=@rowId",
            new { talent, rowId = fellowNumber - Offset });

    /// <summary>
    /// An ally's master evolution data (dt_fellow_pkg_140 table 0): int 96 the evolutions it allows
    /// (+0x180, checked against fellow_awaken_level), ints 97-100 the skill each evolution unlocks.
    /// </summary>
    public (long MaxEvolutions, IReadOnlyList<long> Skills) GetEvolutionData(long fellowId)
    {
        string hex = session.QueryFirstOrDefault<string>("SELECT raw_hex FROM master_fellow WHERE fellow_id=@fellowId", new { fellowId }) ?? "";
        long Int(int index) => hex.Length >= (index + 1) * 8
            ? System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Convert.FromHexString(hex.AsSpan(index * 8, 8)))
            : 0;
        return (Math.Max(0, Int(96)), [Int(97), Int(98), Int(99), Int(100)]);
    }

    public bool SetLocked(long userNumber, long fellowNumber, long locked) =>
        session.Execute("UPDATE fellow SET locked=@locked WHERE user_no=@userNumber AND rowid_alias=@rowId",
            new { locked, userNumber, rowId = fellowNumber - Offset }) > 0;

    public void RecordInBook(long userNumber, long fellowId) =>
        session.Execute(
            $"{session.Dialect.InsertOrIgnore} INTO fellow_book(user_no, fellow_id) VALUES(@userNumber, @fellowId)",
            new { userNumber, fellowId });

    /// <summary>Marks a book entry's max-level reward as taken. False when it was already taken.</summary>
    public bool TryClaimBookReward(long userNumber, long fellowId)
    {
        RecordInBook(userNumber, fellowId);
        return session.Execute(
            "UPDATE fellow_book SET recv_reward=1 WHERE user_no=@userNumber AND fellow_id=@fellowId AND recv_reward=0",
            new { userNumber, fellowId }) > 0;
    }

    public IReadOnlyList<FellowBookRecord> GetBook(long userNumber) =>
        session.Query<FellowBookRecord>(
            "SELECT fellow_id AS FellowId, recv_reward AS RewardReceived, exp AS Experience, level AS Level"
            + " FROM fellow_book WHERE user_no=@userNumber ORDER BY fellow_id",
            new { userNumber });

    /// <summary>Every filled active slot, by setup and position.</summary>
    public IReadOnlyList<FellowSlotRecord> GetFilledSlots(long userNumber) =>
        session.Query<FellowSlotRecord>(
            "SELECT type AS SetupType, position AS Position, fellow_no AS FellowNumber FROM fellow_active"
            + " WHERE user_no=@userNumber AND fellow_no<>0 ORDER BY type, position",
            new { userNumber });

    public void SetSlot(long userNumber, long setupType, long position, long fellowNumber) =>
        session.Execute(
            $"{session.Dialect.InsertOrReplace} INTO fellow_active(user_no, type, position, fellow_no)"
            + " VALUES(@userNumber, @setupType, @position, @fellowNumber)",
            new { userNumber, setupType, position, fellowNumber });
}
