namespace KnS.Data.Repositories;

/// <summary>
/// Notices pinned at the top of chat (the client's single pinned slot, chattingServer+0x4468). A pin stays a row after it
/// ends or is removed so replayed chat history can still show its line; "active" means start_time &lt;= now &lt; end_time.
/// </summary>
public sealed class ChatPinRepository(DataSession session)
{
    public long Add(string message, long startTime, long endTime) =>
        session.Insert(
            "INSERT INTO chat_pin(message,start_time,end_time,created_at) VALUES(@message,@startTime,@endTime,@startTime)",
            new { message, startTime, endTime });

    public ChatPin? Find(long id) =>
        session.QueryFirstOrDefault<ChatPin>(
            "SELECT id AS Id,message AS Message,start_time AS StartTime,end_time AS EndTime FROM chat_pin WHERE id=@id", new { id });

    /// <summary>Pins showing at <paramref name="now"/>, newest first (the client shows only the newest).</summary>
    public IReadOnlyList<ChatPin> Active(long now) =>
        session.Query<ChatPin>(
            "SELECT id AS Id,message AS Message,start_time AS StartTime,end_time AS EndTime FROM chat_pin "
            + "WHERE start_time<=@now AND end_time>@now ORDER BY id DESC", new { now });

    /// <summary>Changes an active pin; an ended or removed pin stays ended.</summary>
    public bool Update(long id, string message, long endTime, long now) =>
        session.Execute("UPDATE chat_pin SET message=@message,end_time=@endTime WHERE id=@id AND end_time>@now",
            new { id, message, endTime, now }) > 0;

    /// <summary>Changes the text of a pin in any state (its replayed chat line follows it).</summary>
    public bool SetMessage(long id, string message) =>
        session.Execute("UPDATE chat_pin SET message=@message WHERE id=@id", new { id, message }) > 0;

    /// <summary>Ends the pin now; its row (and chat line) stays.</summary>
    public bool End(long id, long now) =>
        session.Execute("UPDATE chat_pin SET end_time=@now WHERE id=@id AND end_time>@now", new { id, now }) > 0;
}

public sealed record ChatPin
{
    public long Id { get; init; }
    public string Message { get; init; } = "";
    public long StartTime { get; init; }
    public long EndTime { get; init; }
}
