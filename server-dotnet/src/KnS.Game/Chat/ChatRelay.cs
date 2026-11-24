using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using KnS.Data;
using KnS.Data.Repositories;
using KnS.Protocol.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace KnS.Game.Chat;

/// <summary>A notice saved in chat history: one Studio entry for the rows one BroadcastNotice call stored.</summary>
public sealed class SavedNotice
{
    public List<long> Ids { get; } = [];
    public List<int> Groups { get; } = [];
    public long CreatedAt { get; init; }
    public string Text { get; init; } = "";
    public string Sender { get; init; } = "";
    public long Position { get; init; }
    public long EndTime { get; init; }
    public long PinId { get; init; }
}

/// <summary>A byte stream to one chat client. The frames are the same over a WebSocket (browser) and TCP (the APK, later).</summary>
public interface IChatTransport
{
    /// <summary>Reads into the buffer; 0 means the client left.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);
}

/// <summary>
/// The chat server: logs clients in with the key protocol 701 handed out, keeps them in channels, stores the latest 25 messages
/// per channel, and passes messages to everyone in the same channel. A "group" is the client's channel number: language * 1000 + channel.
/// </summary>
public sealed class ChatRelay(TimeSpan? idleTimeout = null, IServiceScopeFactory? scopeFactory = null, int maxMessagesPerWindow = 40)
{
    /// <summary>The client's own limit on a channel number: ChattingServer_GroupChangeReq refuses anything above 999.</summary>
    private const int ChannelsPerLanguage = 1000;
    private const int MaxLanguage = 999;
    private static readonly TimeSpan MessageRateWindow = TimeSpan.FromSeconds(10);

    /// <summary>Guests are placed in the first channel with room, so low-traffic servers keep everyone together. My own choice.</summary>
    private const int ChannelCapacity = 100;

    private const int HistoryLimit = 25;
    public const int NoticeMessageType = 10;
    private const int PinnedNoticePosition = 2;
    private const int OutboxCapacity = 256;

    /// <summary>The client pings 10 s after its last write, but a background browser tab stops ticking; a closed tab ends its socket anyway, so this only reaps half-open connections.</summary>
    private readonly TimeSpan _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5);
    private readonly int _maxMessagesPerWindow = Math.Max(1, maxMessagesPerWindow);

    private readonly ConcurrentDictionary<long, Grant> _grantsByUser = new();
    private readonly ConcurrentDictionary<int, Grant> _grantsByKey = new();
    private readonly Dictionary<int, HashSet<Session>> _groups = [];
    private readonly Dictionary<int, Queue<byte[]>> _memoryHistory = [];
    private readonly object _groupsLock = new();

    private sealed record Grant(long UserNumber, int LoginKey)
    {
        public string Nickname { get; set; } = "";
    }

    private sealed class Session
    {
        /// <summary>The account number the client logged in with: its hub id, not the game's user_no.</summary>
        public long WireUser { get; set; }
        public long UserNumber { get; set; }
        public long SessionKey { get; set; }
        public int Language { get; set; }
        public int Group { get; set; }
        public string Nickname { get; set; } = "";
        public bool LoggedIn => SessionKey != 0;
        public Queue<DateTimeOffset> RecentMessages { get; } = new();

        public Channel<byte[]> Outbox { get; } = System.Threading.Channels.Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(OutboxCapacity) { FullMode = BoundedChannelFullMode.DropWrite });
    }

    /// <summary>The login key of protocol 701. It stays valid across the client's reconnects, until the server restarts.</summary>
    public int Authorize(long userNumber, string? nickname)
    {
        Grant grant = _grantsByUser.GetOrAdd(userNumber, user => new Grant(user, RandomNumberGenerator.GetInt32(1, int.MaxValue)));
        grant.Nickname = nickname ?? "";
        _grantsByKey[grant.LoginKey] = grant;
        return grant.LoginKey;
    }

    /// <summary>Serves one client until it leaves, goes quiet or sends something malformed.</summary>
    public async Task RunAsync(IChatTransport transport, CancellationToken cancellationToken)
    {
        var session = new Session();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task writer = WriteOutboxAsync(session, transport, idle.Token);
        try
        {
            var buffer = new byte[ChatFrame.MaxSize];
            int have = 0;
            while (true)
            {
                idle.CancelAfter(_idleTimeout);
                int read = await transport.ReadAsync(buffer.AsMemory(have), idle.Token);
                if (read == 0)
                {
                    return;
                }

                have += read;
                int used = 0;
                while (have - used >= ChatFrame.HeaderSize)
                {
                    int size = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(used));
                    if (size is < ChatFrame.HeaderSize or > ChatFrame.MaxSize)
                    {
                        return;
                    }

                    if (have - used < size)
                    {
                        break;
                    }

                    ushort opcode = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(used + 2));
                    if (!Handle(session, opcode, buffer.AsSpan(used + ChatFrame.HeaderSize, size - ChatFrame.HeaderSize)))
                    {
                        return;
                    }

                    used += size;
                }

                Buffer.BlockCopy(buffer, used, buffer, 0, have - used);
                have -= used;
            }
        }
        catch (OperationCanceledException)
        {
            // Idle or shutting down: same as leaving.
        }
        finally
        {
            Leave(session);
            session.Outbox.Writer.TryComplete();
            await idle.CancelAsync();
            await writer;
        }
    }

    /// <summary>False ends the connection, which sends the client back through its own retry and protocol 701.</summary>
    private bool Handle(Session session, ushort opcode, ReadOnlySpan<byte> payload)
    {
        if (opcode == ChatOpcode.Login)
        {
            return HandleLogin(session, payload);
        }

        if (!session.LoggedIn || payload.Length < 16
            || BinaryPrimitives.ReadInt64BigEndian(payload) != session.WireUser
            || BinaryPrimitives.ReadInt64BigEndian(payload[8..]) != session.SessionKey)
        {
            return false;
        }

        ReadOnlySpan<byte> body = payload[16..];
        switch (opcode)
        {
            case ChatOpcode.GroupChange when body.Length >= 4:
                ChangeGroup(session, BinaryPrimitives.ReadInt32BigEndian(body));
                break;
            case ChatOpcode.LanguageChange when body.Length >= 4:
                ChangeLanguage(session, BinaryPrimitives.ReadInt32BigEndian(body));
                break;
            case ChatOpcode.UserChat when body.Length >= 2:
                Send(session, ChatFrame.OfInts(ChatOpcode.UserChatReply, 1));
                Broadcast(session, body[2..]);
                break;
            case ChatOpcode.WideChat:
                Send(session, ChatFrame.OfInts(ChatOpcode.WideChatReply, 1));
                break;
            default:
                // Ping: receiving it was the point, it refreshed the idle timer.
                break;
        }

        return true;
    }

    private bool HandleLogin(Session session, ReadOnlySpan<byte> payload)
    {
        if (session.LoggedIn || payload.Length < 32)
        {
            return false;
        }

        long wireUser = BinaryPrimitives.ReadInt64BigEndian(payload[4..]);
        int loginKey = BinaryPrimitives.ReadInt32BigEndian(payload[16..]);
        int wantedGroup = BinaryPrimitives.ReadInt32BigEndian(payload[20..]);
        int language = BinaryPrimitives.ReadInt32BigEndian(payload[24..]);
        if (language is < 0 or > MaxLanguage || !_grantsByKey.TryGetValue(loginKey, out Grant? grant))
        {
            return false;
        }

        session.WireUser = wireUser;
        session.UserNumber = grant.UserNumber;
        session.SessionKey = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        session.Language = language;
        session.Nickname = grant.Nickname;
        lock (_groupsLock)
        {
            Join(session, IsGroupOf(wantedGroup, language) && wantedGroup != 0 ? wantedGroup : FirstOpenGroup(language));
        }

        Send(session, ChatFrame.Login(session.SessionKey, session.Group));
        SendHistory(session, session.Group);
        return true;
    }

    private void ChangeGroup(Session session, int group)
    {
        if (!IsGroupOf(group, session.Language))
        {
            Send(session, ChatFrame.OfInts(ChatOpcode.GroupChangeReply, 0, session.Group));
            return;
        }

        lock (_groupsLock)
        {
            Leave(session);
            Join(session, group);
        }

        Send(session, ChatFrame.OfInts(ChatOpcode.GroupChangeReply, 1, group));
        SendHistory(session, group);
    }

    private void ChangeLanguage(Session session, int language)
    {
        if (language is < 0 or > MaxLanguage)
        {
            Send(session, ChatFrame.OfInts(ChatOpcode.LanguageChangeReply, 0, session.Language, session.Group));
            return;
        }

        int group = language * ChannelsPerLanguage + session.Group % ChannelsPerLanguage;
        lock (_groupsLock)
        {
            Leave(session);
            session.Language = language;
            Join(session, group);
        }

        Send(session, ChatFrame.OfInts(ChatOpcode.LanguageChangeReply, 1, language, group));
        SendHistory(session, group);
    }

    /// <summary>The name and number on a message come from the login, not from what the sender wrote. Only plain chat (msg_type 0) is relayed: the other types are server announcements.</summary>
    private void Broadcast(Session sender, ReadOnlySpan<byte> base64Body)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        while (sender.RecentMessages.Count > 0 && now - sender.RecentMessages.Peek() >= MessageRateWindow)
        {
            sender.RecentMessages.Dequeue();
        }

        if (sender.RecentMessages.Count >= _maxMessagesPerWindow)
        {
            return;
        }

        sender.RecentMessages.Enqueue(now);

        byte[] body;
        try
        {
            if (JsonNode.Parse(ChatCipher.Decrypt(base64Body)) is not JsonObject message
                || message["msg_type"]?.GetValue<int>() != 0)
            {
                return;
            }

            message["user_no"] = sender.UserNumber;
            message["user_id"] = sender.Nickname.Length > 0 ? sender.Nickname : $"Squire{sender.UserNumber}";

            body = ChatCipher.Encrypt(JsonSerializer.SerializeToUtf8Bytes(message));
        }
        catch (Exception e) when (e is FormatException or CryptographicException or JsonException or ArgumentException or InvalidOperationException)
        {
            return;
        }

        if (body.Length + ChatFrame.HeaderSize + 2 > ChatFrame.MaxSize)
        {
            return;
        }

        byte[] frame = ChatFrame.Message(body);
        RecordHistory(sender.Group, body, frame);
        lock (_groupsLock)
        {
            if (_groups.TryGetValue(sender.Group, out HashSet<Session>? members))
            {
                foreach (Session member in members)
                {
                    Send(member, frame);
                }
            }
        }
    }

    /// <summary>
    /// Broadcasts a server notice to one group or to every active channel (plus Channel 1). NetSocketReceiveProc switches on
    /// msg_type at 0x162556 and only handles 0, 10, 11, 20 and 30; any other type (1 included) is dropped. Type 10 is the
    /// server notice: it reads user_id and msg_data as plain strings and regist_position. Every position queues the chat
    /// line (0x1625c4: SplitLineChatString + InsertChattingQueue). Position 2 (0x162d32) first copies the text (strcpy) into
    /// the pinned slot (chattingServer+0x4468, 512 bytes up to the second slot at +0x4668; start/end at +0x4200/+0x4218
    /// through GetLocalTimeFromServerTime, which is localtime() read back, i.e. unchanged unix seconds) that
    /// DrawChattingPopup draws above the messages until ProcessChattingView sees time() pass end_time. Position 3 fills a
    /// second slot (+0x4668) that nothing in the client draws.
    /// pinSeconds &gt; 0 pins the notice for that long: it goes to every channel (the client has one slot, not one per
    /// channel), is stored as a chat_pin row Studio can edit or end, and is served by /api/pins.
    /// </summary>
    public bool BroadcastNotice(string text, int? group = null, long pinSeconds = 0)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || (pinSeconds > 0 && Encoding.UTF8.GetByteCount(trimmed) > MaxPinBytes))
        {
            return false;
        }

        long pinId = 0;
        byte[] body;
        if (pinSeconds > 0)
        {
            group = null;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            body = NoticeBody(trimmed, now, now + pinSeconds);
            if (body.Length + ChatFrame.HeaderSize + 2 > ChatFrame.MaxSize)
            {
                return false;
            }
            if (scopeFactory is not null)
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DataSession>();
                db.BeginTransaction();
                pinId = scope.ServiceProvider.GetRequiredService<ChatPinRepository>().Add(trimmed, now, now + pinSeconds);
                db.Commit();
            }
        }
        else
        {
            body = NoticeBody(trimmed, null, null);
        }

        if (body.Length + ChatFrame.HeaderSize + 2 > ChatFrame.MaxSize)
        {
            return false;
        }

        byte[] frame = ChatFrame.Message(body);
        if (group is int targetGroup)
        {
            RecordHistory(targetGroup, body, frame);
            lock (_groupsLock)
            {
                if (_groups.TryGetValue(targetGroup, out HashSet<Session>? members))
                {
                    foreach (Session member in members)
                    {
                        Send(member, frame);
                    }
                }
            }
            return true;
        }

        List<int> activeGroups;
        lock (_groupsLock)
        {
            activeGroups = [.. _groups.Keys];
            foreach (HashSet<Session> members in _groups.Values)
            {
                foreach (Session member in members)
                {
                    Send(member, frame);
                }
            }
        }

        // Always persist to English Channel 1 (1000) and Korean Channel 1 (0) so F5/reconnecting players see the notice.
        var historyGroups = new HashSet<int>(activeGroups) { ChannelsPerLanguage, 0 };
        foreach (int g in historyGroups)
        {
            RecordHistory(g, body, frame, pinId);
        }
        return true;
    }

    /// <summary>The pinned slot's size: strcpy at 0x162d42 into +0x4468, next slot at +0x4668; one byte for the terminator.</summary>
    public const int MaxPinBytes = 0x200 - 1;

    /// <summary>
    /// Changes a pin's text and end. Online clients get no message (any type-10 message also adds a chat line): the browser
    /// picks the change up from /api/pins; replayed history uses the new text. False when the pin does not exist.
    /// </summary>
    public bool UpdatePin(long id, string text, long endTime)
    {
        string trimmed = text.Trim();
        if (scopeFactory is null || trimmed.Length == 0 || Encoding.UTF8.GetByteCount(trimmed) > MaxPinBytes)
        {
            return false;
        }
        using IServiceScope scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataSession>();
        db.BeginTransaction();
        bool updated = scope.ServiceProvider.GetRequiredService<ChatPinRepository>().Update(id, trimmed, endTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        db.Commit();
        return updated;
    }

    /// <summary>Ends a pin now. Its chat line stays in history as an ordinary notice.</summary>
    public bool RemovePin(long id)
    {
        if (scopeFactory is null)
        {
            return false;
        }
        using IServiceScope scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataSession>();
        db.BeginTransaction();
        bool ended = scope.ServiceProvider.GetRequiredService<ChatPinRepository>().End(id, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        db.Commit();
        return ended;
    }

    /// <summary>Pins showing now, newest first; the client shows only the newest.</summary>
    public IReadOnlyList<ChatPin> ActivePins()
    {
        if (scopeFactory is null)
        {
            return [];
        }
        using IServiceScope scope = scopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ChatPinRepository>().Active(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>
    /// Notices (msg_type 10) saved in chat history, newest first. BroadcastNotice stores the same payload in several groups;
    /// rows with one payload saved within <see cref="NoticeSameSendSeconds"/> of each other are one notice. Player chat is
    /// never listed. Removing or changing a row reaches players when history replays (F5, reconnect, channel change):
    /// lines already in an open chat window stay.
    /// </summary>
    public IReadOnlyList<SavedNotice> SavedNotices()
    {
        if (scopeFactory is null)
        {
            return [];
        }
        IReadOnlyList<ChatHistoryRow> rows;
        using (IServiceScope scope = scopeFactory.CreateScope())
        {
            rows = scope.ServiceProvider.GetRequiredService<ChatHistoryRepository>().All();
        }

        var notices = new List<SavedNotice>();
        var open = new Dictionary<string, SavedNotice>(StringComparer.Ordinal);
        foreach (ChatHistoryRow row in rows)
        {
            if (ReadNotice(row.Payload) is not JsonObject message)
            {
                continue;
            }
            if (open.TryGetValue(row.Payload, out SavedNotice? same) && row.CreatedAt - same.CreatedAt <= NoticeSameSendSeconds)
            {
                same.Ids.Add(row.Id);
                same.Groups.Add(row.ChatGroup);
                continue;
            }
            var notice = new SavedNotice
            {
                CreatedAt = row.CreatedAt,
                Text = message["msg_data"]?.ToString() ?? "",
                Sender = message["user_id"]?.ToString() ?? "",
                Position = Int(message["regist_position"]),
                EndTime = Int(message["end_time"]),
                PinId = row.PinId,
            };
            notice.Ids.Add(row.Id);
            notice.Groups.Add(row.ChatGroup);
            open[row.Payload] = notice;
            notices.Add(notice);
        }
        notices.Reverse();
        return notices;
    }

    /// <summary>Rows of one BroadcastNotice call are written in one loop; a few seconds covers a slow database.</summary>
    public const int NoticeSameSendSeconds = 10;

    /// <summary>Rewrites the text of saved notice rows (only rows that are notices). A linked pin gets the same text.</summary>
    public int EditSavedNotice(IReadOnlyCollection<long> ids, string text)
    {
        string trimmed = text.Trim();
        if (scopeFactory is null || trimmed.Length == 0)
        {
            return 0;
        }
        using IServiceScope scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataSession>();
        var history = scope.ServiceProvider.GetRequiredService<ChatHistoryRepository>();
        var pins = scope.ServiceProvider.GetRequiredService<ChatPinRepository>();
        db.BeginTransaction();
        int changed = 0;
        foreach (ChatHistoryRow row in history.ByIds(ids))
        {
            if (ReadNotice(row.Payload) is not JsonObject message)
            {
                continue;
            }
            message["msg_data"] = trimmed;
            byte[] body = ChatCipher.Encrypt(JsonSerializer.SerializeToUtf8Bytes(message));
            if (body.Length + ChatFrame.HeaderSize + 2 > ChatFrame.MaxSize)
            {
                db.Rollback();
                return 0;
            }
            changed += history.SetPayload(row.Id, Encoding.ASCII.GetString(body));
            if (row.PinId > 0)
            {
                pins.SetMessage(row.PinId, trimmed);
            }
        }
        db.Commit();
        return changed;
    }

    /// <summary>Deletes saved notice rows (only rows that are notices) and ends a pin linked to them.</summary>
    public int DeleteSavedNotice(IReadOnlyCollection<long> ids)
    {
        if (scopeFactory is null)
        {
            return 0;
        }
        using IServiceScope scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DataSession>();
        var history = scope.ServiceProvider.GetRequiredService<ChatHistoryRepository>();
        var pins = scope.ServiceProvider.GetRequiredService<ChatPinRepository>();
        db.BeginTransaction();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var notices = history.ByIds(ids).Where(row => ReadNotice(row.Payload) is not null).ToList();
        foreach (long pinId in notices.Select(row => row.PinId).Where(id => id > 0).Distinct())
        {
            pins.End(pinId, now);
        }
        int deleted = history.Delete([.. notices.Select(row => row.Id)]);
        db.Commit();
        return deleted;
    }

    /// <summary>The decrypted message when the stored payload is a server notice (msg_type 10), otherwise null.</summary>
    private static JsonObject? ReadNotice(string payload)
    {
        try
        {
            return JsonNode.Parse(ChatCipher.Decrypt(Encoding.ASCII.GetBytes(payload))) is JsonObject message
                && Int(message["msg_type"]) == NoticeMessageType ? message : null;
        }
        catch (Exception e) when (e is FormatException or CryptographicException or JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static long Int(JsonNode? node) =>
        node is JsonValue value && (value.TryGetValue(out long n) || (value.TryGetValue(out string? s) && long.TryParse(s, out n))) ? n : 0;

    private static byte[] NoticeBody(string text, long? startTime, long? endTime)
    {
        var payload = new JsonObject
        {
            ["msg_type"] = NoticeMessageType,
            ["user_id"] = "Notice",
            ["msg_data"] = text,
            ["regist_position"] = endTime is null ? 0 : PinnedNoticePosition,
        };
        if (endTime is long end)
        {
            payload["start_time"] = startTime;
            payload["end_time"] = end;
        }
        return ChatCipher.Encrypt(JsonSerializer.SerializeToUtf8Bytes(payload));
    }

    private void RecordHistory(int group, byte[] encryptedBody, byte[] frame, long pinId = 0)
    {
        if (scopeFactory is not null)
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataSession>();
            db.BeginTransaction();
            scope.ServiceProvider.GetRequiredService<ChatHistoryRepository>()
                .Add(group, Encoding.ASCII.GetString(encryptedBody), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), HistoryLimit, pinId);
            db.Commit();
            return;
        }

        lock (_groupsLock)
        {
            if (!_memoryHistory.TryGetValue(group, out Queue<byte[]>? queue))
            {
                _memoryHistory[group] = queue = new Queue<byte[]>(HistoryLimit);
            }
            if (queue.Count >= HistoryLimit)
            {
                queue.Dequeue();
            }
            queue.Enqueue(frame);
        }
    }

    private void SendHistory(Session session, int group)
    {
        if (scopeFactory is not null)
        {
            // A pinned notice replays pinned only while its pin is active, with the pin's current text and end; an ended or
            // removed pin replays as an ordinary notice line, so F5 never brings back a pin Studio took down.
            var frames = new List<byte[]>();
            using (IServiceScope scope = scopeFactory.CreateScope())
            {
                var pins = scope.ServiceProvider.GetRequiredService<ChatPinRepository>();
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (ChatHistoryEntry entry in scope.ServiceProvider.GetRequiredService<ChatHistoryRepository>().GetRecent(group, HistoryLimit))
                {
                    byte[] body = Encoding.ASCII.GetBytes(entry.Payload);
                    if (entry.PinId > 0 && pins.Find(entry.PinId) is ChatPin pin)
                    {
                        bool active = pin.StartTime <= now && now < pin.EndTime;
                        body = active ? NoticeBody(pin.Message, pin.StartTime, pin.EndTime) : NoticeBody(pin.Message, null, null);
                    }
                    else if (ReadNotice(entry.Payload) is JsonObject notice && Int(notice["regist_position"]) != 0)
                    {
                        // A pinned notice saved before chat_pin existed has no pin row Studio could show or end: replay it as an
                        // ordinary line, so only pins listed in Studio (and /api/pins) ever pin.
                        body = NoticeBody(notice["msg_data"]?.ToString() ?? "", null, null);
                    }
                    frames.Add(ChatFrame.Message(body));
                }
            }
            foreach (byte[] frame in frames)
            {
                Send(session, frame);
            }
            return;
        }

        byte[][] snapshot;
        lock (_groupsLock)
        {
            if (!_memoryHistory.TryGetValue(group, out Queue<byte[]>? queue) || queue.Count == 0)
            {
                return;
            }
            snapshot = [.. queue];
        }
        foreach (byte[] frame in snapshot)
        {
            Send(session, frame);
        }
    }

    private static bool IsGroupOf(int group, int language) => group >= 0 && group / ChannelsPerLanguage == language;

    private int FirstOpenGroup(int language)
    {
        for (int channel = 0; channel < ChannelsPerLanguage; channel++)
        {
            int group = language * ChannelsPerLanguage + channel;
            if (!_groups.TryGetValue(group, out HashSet<Session>? members) || members.Count < ChannelCapacity)
            {
                return group;
            }
        }

        return language * ChannelsPerLanguage;
    }

    private void Join(Session session, int group)
    {
        session.Group = group;
        if (!_groups.TryGetValue(group, out HashSet<Session>? members))
        {
            _groups[group] = members = [];
        }

        members.Add(session);
    }

    private void Leave(Session session)
    {
        lock (_groupsLock)
        {
            if (session.LoggedIn && _groups.TryGetValue(session.Group, out HashSet<Session>? members))
            {
                members.Remove(session);
                if (members.Count == 0)
                {
                    _groups.Remove(session.Group);
                }
            }
        }
    }

    /// <summary>A client too slow to drain its queue loses messages rather than holding up the channel.</summary>
    private static void Send(Session session, byte[] frame) => session.Outbox.Writer.TryWrite(frame);

    private static async Task WriteOutboxAsync(Session session, IChatTransport transport, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (byte[] frame in session.Outbox.Reader.ReadAllAsync(cancellationToken))
            {
                await transport.WriteAsync(frame, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The read loop ended the connection.
        }
    }
}
