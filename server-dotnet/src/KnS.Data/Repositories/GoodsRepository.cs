using KnS.Data.Dialects;

namespace KnS.Data.Repositories;

public sealed record GoodsRecord
{
    public long ItemId { get; init; }
    public long Count { get; init; }
}

/// <summary>Counted etc items a player holds (user_goods): tickets, collectibles, ...</summary>
public sealed class GoodsRepository(DataSession session)
{
    /// <summary>Recover arena spirits against a persisted clock; never reduce reward overflow.</summary>
    public long ArenaSpiritClock(long userNumber, long now)
    {
        const long itemId = 1900017, maximum = 10, interval = 1800;
        long? balance = session.QueryFirstOrDefault<long?>("SELECT count FROM user_goods WHERE user_no=@userNumber AND item_id=@itemId", new { userNumber, itemId });
        if (balance is null) return now;
        session.Execute($"{session.Dialect.InsertOrIgnore} INTO pvp_spirit_clock(user_no,last_gain) VALUES(@userNumber,@now)", new { userNumber, now });
        session.Execute("UPDATE pvp_spirit_clock SET last_gain=last_gain WHERE user_no=@userNumber", new { userNumber });
        long last = session.QueryFirstOrDefault<long>("SELECT last_gain FROM pvp_spirit_clock WHERE user_no=@userNumber", new { userNumber });
        long count = balance.Value;
        if (count >= maximum || last > now) last = now;
        else
        {
            long gained = Math.Min(maximum - count, Math.Max(0, now - last) / interval);
            if (gained > 0)
            {
                Add(userNumber, itemId, gained);
                last = count + gained >= maximum ? now : last + gained * interval;
            }
        }
        session.Execute("UPDATE pvp_spirit_clock SET last_gain=@last WHERE user_no=@userNumber", new { userNumber, last });
        return last;
    }

    public IReadOnlyList<GoodsRecord> GetAll(long userNumber) =>
        session.Query<GoodsRecord>(
            "SELECT item_id AS ItemId, count AS Count FROM user_goods WHERE user_no=@userNumber ORDER BY item_id",
            new { userNumber });

    /// <summary>Reserve the uncredited portion of a legacy goods balance inside the caller's transaction.</summary>
    public long ReserveLegacyStamina(long userNumber, long itemId)
    {
        if (!session.InTransaction) throw new InvalidOperationException("Stamina recovery requires a transaction.");
        long? observed = session.QueryFirstOrDefault<long?>(
            "SELECT count FROM user_goods WHERE user_no=@userNumber AND item_id=@itemId", new { userNumber, itemId });
        if (observed is not > 0) return 0;
        session.Execute($"{session.Dialect.InsertOrIgnore} INTO stamina_recovery(user_no,credited) VALUES(@userNumber,0)", new { userNumber });
        // An UPDATE obtains the row lock before reading the high-water mark. MySQL locking reads
        // use the current committed value instead of a prior repeatable-read snapshot.
        session.Execute("UPDATE stamina_recovery SET credited=credited WHERE user_no=@userNumber", new { userNumber });
        string locking = session.Dialect is MySqlDialect ? " FOR UPDATE" : "";
        long credited = session.QueryFirstOrDefault<long>("SELECT credited FROM stamina_recovery WHERE user_no=@userNumber" + locking, new { userNumber });
        long current = session.QueryFirstOrDefault<long?>(
            "SELECT count FROM user_goods WHERE user_no=@userNumber AND item_id=@itemId" + locking, new { userNumber, itemId }) ?? 0;
        if (current <= credited) return 0;
        session.Execute("UPDATE stamina_recovery SET credited=@current WHERE user_no=@userNumber", new { userNumber, current });
        return current - credited;
    }

    /// <summary>
    /// Adds a counted etc item (19xxxxx). Allies, equipment and materials are owned rows, and gold (1900008), gems (1900009)
    /// and stamina (1900014) have their own columns, so filing any of them here would vanish from the player's view:
    /// that is refused loudly instead (an achievement once paid ally 100322 this way).
    /// </summary>
    /// <summary>Whether <paramref name="itemId"/> belongs in user_goods (see <see cref="Add"/>).</summary>
    public static bool IsCountedItem(long itemId) =>
        itemId is >= 1900000 and <= 1999999 and not (1900008 or 1900009 or 1900014);

    public void Add(long userNumber, long itemId, long count)
    {
        if (!IsCountedItem(itemId))
        {
            throw new InvalidOperationException($"item {itemId} is not a counted etc item; grant it through its own owner");
        }

        session.Execute(
            $"{session.Dialect.InsertOrIgnore} INTO user_goods(user_no, item_id, count) VALUES(@userNumber, @itemId, 0)",
            new { userNumber, itemId });
        session.Execute(
            "UPDATE user_goods SET count=count+@count WHERE user_no=@userNumber AND item_id=@itemId",
            new { count, userNumber, itemId });
    }
}
