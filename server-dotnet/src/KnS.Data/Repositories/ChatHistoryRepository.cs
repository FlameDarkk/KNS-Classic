namespace KnS.Data.Repositories;

/// <summary>Persists recent chat messages per channel group so F5 reloads and server restarts keep recent history.</summary>
public sealed class ChatHistoryRepository(DataSession session)
{
    public void Add(int group, string payload, long createdAt, int keepPerGroup = 25, long pinId = 0)
    {
        session.Insert(
            "INSERT INTO chat_history(chat_group,payload,created_at,pin_id) VALUES(@group,@payload,@createdAt,@pinId)",
            new { group, payload, createdAt, pinId });

        if (keepPerGroup > 0)
        {
            long? cutoff = session.QueryFirstOrDefault<long?>(
                "SELECT id FROM chat_history WHERE chat_group=@group ORDER BY id DESC LIMIT 1 OFFSET @offset",
                new { group, offset = keepPerGroup });
            if (cutoff is long id)
            {
                session.Execute(
                    "DELETE FROM chat_history WHERE chat_group=@group AND id<=@id",
                    new { group, id });
            }
        }
    }

    /// <summary>Oldest first. PinId is the chat_pin row of a pinned notice, 0 otherwise.</summary>
    public IReadOnlyList<ChatHistoryEntry> GetRecent(int group, int limit = 25)
    {
        var rows = session.Query<ChatHistoryEntry>(
            "SELECT payload AS Payload,pin_id AS PinId FROM chat_history WHERE chat_group=@group ORDER BY id DESC LIMIT @limit",
            new { group, limit });
        var result = new List<ChatHistoryEntry>(rows.Count);
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            result.Add(rows[i]);
        }
        return result;
    }

    /// <summary>Every stored row, oldest first (at most 25 per group, so small).</summary>
    public IReadOnlyList<ChatHistoryRow> All() =>
        session.Query<ChatHistoryRow>(
            "SELECT id AS Id,chat_group AS ChatGroup,payload AS Payload,created_at AS CreatedAt,pin_id AS PinId FROM chat_history ORDER BY id");

    public IReadOnlyList<ChatHistoryRow> ByIds(IReadOnlyCollection<long> ids) =>
        ids.Count == 0 ? [] : session.Query<ChatHistoryRow>(
            "SELECT id AS Id,chat_group AS ChatGroup,payload AS Payload,created_at AS CreatedAt,pin_id AS PinId FROM chat_history WHERE id IN @ids ORDER BY id",
            new { ids });

    public int SetPayload(long id, string payload) =>
        session.Execute("UPDATE chat_history SET payload=@payload WHERE id=@id", new { id, payload });

    public int Delete(IReadOnlyCollection<long> ids) =>
        ids.Count == 0 ? 0 : session.Execute("DELETE FROM chat_history WHERE id IN @ids", new { ids });
}

public sealed record ChatHistoryRow
{
    public long Id { get; init; }
    public int ChatGroup { get; init; }
    public string Payload { get; init; } = "";
    public long CreatedAt { get; init; }
    public long PinId { get; init; }
}

public sealed record ChatHistoryEntry
{
    public string Payload { get; init; } = "";
    public long PinId { get; init; }
}
