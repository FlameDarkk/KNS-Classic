namespace KnS.Data.Repositories;

/// <summary>A player's account buffs (user_buff), sent as buff_etc_info rows {type, end_date_timestamp}.</summary>
public sealed record BuffRecord
{
    public long BuffType { get; init; }

    /// <summary>Unix seconds; 0 = no expiry.</summary>
    public long EndTime { get; init; }
}

public sealed class BuffRepository(DataSession session)
{
    public IReadOnlyList<BuffRecord> GetAll(long userNumber) =>
        session.Query<BuffRecord>(
            "SELECT buff_type AS BuffType, end_time AS EndTime FROM user_buff WHERE user_no=@userNumber ORDER BY buff_type",
            new { userNumber });

    public bool Has(long userNumber, long buffType) =>
        session.QueryFirstOrDefault<long?>(
            "SELECT buff_type FROM user_buff WHERE user_no=@userNumber AND buff_type=@buffType", new { userNumber, buffType }) is not null;

    public long GetEndTime(long userNumber, long buffType) =>
        session.QueryFirstOrDefault<long?>(
            "SELECT end_time FROM user_buff WHERE user_no=@userNumber AND buff_type=@buffType",
            new { userNumber, buffType }) ?? 0;

    public bool IsActive(long userNumber, long buffType, long now) =>
        GetEndTime(userNumber, buffType) > now;

    /// <summary>Adds the buff; false when the player already has it.</summary>
    public bool TryAdd(long userNumber, long buffType, long endTime) =>
        session.Execute(
            $"{session.Dialect.InsertOrIgnore} INTO user_buff(user_no, buff_type, end_time) VALUES(@userNumber, @buffType, @endTime)",
            new { userNumber, buffType, endTime }) > 0;

    /// <summary>Sets or replaces a buff's expiry time (e.g. renewable timed EXP boosts).</summary>
    public void Set(long userNumber, long buffType, long endTime) =>
        session.Execute(
            $"{session.Dialect.InsertOrReplace} INTO user_buff(user_no, buff_type, end_time) VALUES(@userNumber, @buffType, @endTime)",
            new { userNumber, buffType, endTime });

    /// <summary>Hero Point stamina upgrades bought (stamina_upgrade): MAX Stamina (901) and Recovery Time (902) purchases.</summary>
    public (long MaxCount, long TimeCount) GetStaminaUpgrades(long userNumber)
    {
        var rows = session.QueryRows("SELECT max_count, time_count FROM stamina_upgrade WHERE user_no=@userNumber", new { userNumber });
        return rows.Count == 0 ? (0, 0) : (Convert.ToInt64(rows[0]["max_count"]), Convert.ToInt64(rows[0]["time_count"]));
    }

    public void AddStaminaUpgrade(long userNumber, bool maxStamina)
    {
        session.Execute($"{session.Dialect.InsertOrIgnore} INTO stamina_upgrade(user_no, max_count, time_count) VALUES(@userNumber, 0, 0)", new { userNumber });
        string column = maxStamina ? "max_count" : "time_count";
        session.Execute($"UPDATE stamina_upgrade SET {column}={column}+1 WHERE user_no=@userNumber", new { userNumber });
    }
}
