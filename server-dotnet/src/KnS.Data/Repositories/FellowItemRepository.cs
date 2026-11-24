namespace KnS.Data.Repositories;

public sealed record FellowItemRecord
{
    public long FellowItemNumber { get; init; }
    public long FellowItemId { get; init; }
    public long Upgrade { get; init; }
    public long Locked { get; init; }
    public long FellowNumber { get; init; }

    /// <summary>The slot on that ally: 1 weapon, 2 and 3 accessories; 0 = in the bag.</summary>
    public long EquipIndex { get; init; }

    /// <summary>Steel Anvil power-up exp inside the current level.</summary>
    public long UpgradeExperience { get; init; }
}

/// <summary>Ally gear a player owns (fellow_item). Numbers are rowid + <see cref="Offset"/>, apart from the item and fellow numbers.</summary>
public sealed class FellowItemRepository(DataSession session)
{
    private const long Offset = 5_000_000;

    private const string Columns =
        "rowid_alias + @offset AS FellowItemNumber, fellow_item_id AS FellowItemId, upgrade AS Upgrade,"
        + " locked AS Locked, fellow_no AS FellowNumber, equip_index AS EquipIndex, upgrade_exp AS UpgradeExperience";

    public IReadOnlyList<FellowItemRecord> GetOwned(long userNumber) =>
        session.Query<FellowItemRecord>(
            $"SELECT {Columns} FROM fellow_item WHERE user_no=@userNumber ORDER BY rowid_alias",
            new { offset = Offset, userNumber });

    public FellowItemRecord? Find(long userNumber, long fellowItemNumber) =>
        session.QueryFirstOrDefault<FellowItemRecord>(
            $"SELECT {Columns} FROM fellow_item WHERE rowid_alias=@rowId AND user_no=@userNumber",
            new { offset = Offset, rowId = fellowItemNumber - Offset, userNumber });

    public long Add(long userNumber, long fellowItemId) =>
        session.Insert(
            "INSERT INTO fellow_item(user_no, fellow_item_id) VALUES(@userNumber, @fellowItemId)",
            new { userNumber, fellowItemId }) + Offset;

    public void SetUpgrade(long fellowItemNumber, long upgrade, long experience) =>
        session.Execute(
            "UPDATE fellow_item SET upgrade=@upgrade, upgrade_exp=@experience WHERE rowid_alias=@rowId",
            new { upgrade, experience, rowId = fellowItemNumber - Offset });

    public void SetLocked(long fellowItemNumber, bool locked) =>
        session.Execute(
            "UPDATE fellow_item SET locked=@locked WHERE rowid_alias=@rowId",
            new { locked = locked ? 1 : 0, rowId = fellowItemNumber - Offset });

    public void SetEquipped(long fellowItemNumber, long fellowNumber, long equipIndex) =>
        session.Execute(
            "UPDATE fellow_item SET fellow_no=@fellowNumber, equip_index=@equipIndex WHERE rowid_alias=@rowId",
            new { fellowNumber, equipIndex, rowId = fellowItemNumber - Offset });

    /// <summary>Puts everything an ally wears back in the bag (the ally is going away). Returns those item numbers.</summary>
    public IReadOnlyList<long> ReleaseFromFellow(long userNumber, long fellowNumber)
    {
        List<long> worn = GetOwned(userNumber)
            .Where(item => item.FellowNumber == fellowNumber)
            .Select(item => item.FellowItemNumber)
            .ToList();
        foreach (long number in worn)
        {
            SetEquipped(number, 0, 0);
        }

        return worn;
    }

    public void Delete(long fellowItemNumber) =>
        session.Execute("DELETE FROM fellow_item WHERE rowid_alias=@rowId", new { rowId = fellowItemNumber - Offset });
}
