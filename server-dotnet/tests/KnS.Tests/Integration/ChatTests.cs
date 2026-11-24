using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using KnS.Game.Chat;
using KnS.Protocol.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace KnS.Tests.Integration;

/// <summary>The chat server, spoken to the way libgame.so speaks it: frames from NetSocketConnectCB and ChattingServer_ChatReq.</summary>
[Collection(SharedServer.Name)]
public class ChatTests(ServerFixture server)
{
    private const int English = 1;
    private const int FirstEnglishChannel = English * 1000;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private async Task<(long User, int LoginKey)> NewPlayerAsync(string device)
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        JsonObject reply = await server.CallGatewayAsync(701, new { }, user);
        return (user, reply["chat_server_info"]!["login_key"]!.GetValue<int>());
    }

    private async Task<ChatClient> LoggedInAsync(string device, int group = 0)
    {
        (long user, int key) = await NewPlayerAsync(device);
        var client = new ChatClient(await server.ConnectChatAsync(), user);
        await client.LoginAsync(key, group);
        return client;
    }

    [Fact]
    public async Task Protocol_701_gives_what_the_client_needs_to_connect()
    {
        long user = (await server.CallGatewayAsync(51, new { hub_did = "chat-701" }))["user_no"]!.GetValue<long>();

        JsonObject reply = await server.CallGatewayAsync(701, new { }, user);

        // The post-handler @0x153cda gives up on a 0 login key or a retcode of 200; is_block 1 mutes the player.
        Assert.NotEqual(0, reply["chat_server_info"]!["login_key"]!.GetValue<int>());
        Assert.NotEqual(200, reply["chat_server_retcode"]!.GetValue<int>());
        Assert.Equal(0, reply["chat_block_info"]!["is_block"]!.GetValue<int>());
        Assert.True(reply["chat_server_info"]!["port"]!.GetValue<int>() > 0);
        Assert.True(reply["chat_cool_time"]!.GetValue<int>() > 0);
    }

    [Fact]
    public void Decrypts_a_message_captured_from_the_game()
    {
        const string Captured =
            "k+IK3x3hcnbycUnwaQA6NerXqEVc5Vt1C9RFgZgDt5jBBsQdn5gk79cSe8Yap8xC2YGkYJfB936yhtkUIyNQ44bVPgI9wmNqqtjT3yxXgVL/GfDzVs9aB3nWqqclpf5UxAUZnY/mX2WfKZ2LTQxbskVy5XUVy+VvWZVMpF8R2WjqXs6NNATnYCHoW6zgqrwm";

        string plain = Encoding.UTF8.GetString(ChatCipher.Decrypt(Encoding.ASCII.GetBytes(Captured)));

        Assert.Equal(
            """{"msg_data": [{"lang": 1, "msg": "hello from guest one"}], "user_pvp_grade": 0, "user_no": 10001, "msg_type": 0, "user_id": "Squire10001"}""",
            plain);
    }

    [Fact]
    public async Task A_login_joins_the_first_channel_of_the_language()
    {
        using ChatClient client = await LoggedInAsync("chat-login");

        Assert.Equal(FirstEnglishChannel, client.Group);
    }

    [Fact]
    public async Task A_login_with_an_unknown_key_is_dropped()
    {
        using var client = new ChatClient(await server.ConnectChatAsync(), 1);

        await client.SendLoginAsync(loginKey: 123, group: 0);

        Assert.Null(await client.ReceiveAsync());
    }

    [Fact]
    public async Task Everyone_in_the_channel_sees_a_message_including_the_sender()
    {
        int ch = FirstEnglishChannel + 10;
        using ChatClient alice = await LoggedInAsync("chat-alice", ch);
        using ChatClient bob = await LoggedInAsync("chat-bob", ch);

        await alice.ChatAsync("hello bob");

        Assert.Equal("hello bob", await bob.NextMessageTextAsync());
        Assert.Equal("hello bob", await alice.NextMessageTextAsync());
    }

    [Fact]
    public async Task The_sender_cannot_claim_another_players_number()
    {
        int ch = FirstEnglishChannel + 11;
        using ChatClient alice = await LoggedInAsync("chat-spoof", ch);
        using ChatClient bob = await LoggedInAsync("chat-spoof-reader", ch);

        await alice.ChatAsync("not really", claimedUser: 999_999);

        JsonObject message = await bob.NextMessageAsync();
        Assert.Equal(alice.User, message["user_no"]!.GetValue<long>());
    }

    [Fact]
    public async Task Server_announcement_types_are_not_relayed_from_a_client()
    {
        int ch = FirstEnglishChannel + 12;
        using ChatClient alice = await LoggedInAsync("chat-announce", ch);
        using ChatClient bob = await LoggedInAsync("chat-announce-reader", ch);

        await alice.ChatAsync("fake notice", messageType: 10);
        await alice.ChatAsync("real");

        Assert.Equal("real", await bob.NextMessageTextAsync());
    }

    [Fact]
    public async Task A_channel_change_moves_the_player()
    {
        int ch = FirstEnglishChannel + 13;
        using ChatClient alice = await LoggedInAsync("chat-mover", ch);
        using ChatClient bob = await LoggedInAsync("chat-stayer", ch);
        using ChatClient carol = await LoggedInAsync("chat-other", FirstEnglishChannel + 7);

        Assert.True(await alice.ChangeGroupAsync(FirstEnglishChannel + 7));
        await alice.ChatAsync("over here");
        await bob.ChatAsync("still here");

        Assert.Equal("over here", await carol.NextMessageTextAsync());
        Assert.Equal("still here", await bob.NextMessageTextAsync());
        Assert.Equal("over here", await alice.NextMessageTextAsync());
    }

    [Fact]
    public async Task A_channel_of_another_language_is_refused()
    {
        using ChatClient alice = await LoggedInAsync("chat-refused");

        Assert.False(await alice.ChangeGroupAsync(5000));
        Assert.Equal(FirstEnglishChannel, alice.Group);
    }

    [Fact]
    public async Task A_language_change_keeps_the_channel_number()
    {
        using ChatClient alice = await LoggedInAsync("chat-language", FirstEnglishChannel + 3);

        int group = await alice.ChangeLanguageAsync(4);

        Assert.Equal(4003, group);
    }

    [Fact]
    public async Task A_frame_with_a_wrong_session_is_dropped()
    {
        using ChatClient alice = await LoggedInAsync("chat-session", FirstEnglishChannel + 14);

        await alice.PingAsync(session: alice.Session + 1);

        Assert.Null(await alice.ReceiveAsync());
    }

    [Fact]
    public async Task Pings_keep_a_quiet_connection_alive_and_silence_ends_it()
    {
        var relay = new ChatRelay(TimeSpan.FromMilliseconds(400));
        int key = relay.Authorize(5, "Quiet");
        var transport = new MemoryTransport();
        Task run = relay.RunAsync(transport, CancellationToken.None);

        transport.Send(ChatClient.LoginFrame(5, key, 0));
        byte[] login = await transport.ReceiveAsync();
        long session = BinaryPrimitives.ReadInt64BigEndian(login.AsSpan(4));

        // Three idle timeouts' worth of pings, each inside the timeout.
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(100);
            transport.Send(ChatClient.PingFrame(5, session, FirstEnglishChannel));
        }

        Assert.False(run.IsCompleted);

        await run.WaitAsync(Patience);
    }

    [Fact]
    public async Task Reconnecting_on_channel_1_receives_latest_25_messages()
    {
        int ch = FirstEnglishChannel + 50;
        using (ChatClient sender = await LoggedInAsync("chat-hist-sender", ch))
        {
            for (int i = 1; i <= 30; i++)
            {
                await sender.ChatAsync($"msg-{i}");
                Assert.Equal($"msg-{i}", await sender.NextMessageTextAsync());
            }
        }

        using ChatClient reloaded = await LoggedInAsync("chat-hist-reloaded", ch);
        var received = new List<string>(25);
        for (int i = 0; i < 25; i++)
        {
            received.Add(await reloaded.NextMessageTextAsync());
        }

        Assert.Equal(Enumerable.Range(6, 25).Select(i => $"msg-{i}").ToList(), received);

        // Default wantedGroup=0 still joins Channel 1 (FirstEnglishChannel).
        using ChatClient defaultClient = await LoggedInAsync("chat-default-ch1", 0);
        Assert.Equal(FirstEnglishChannel, defaultClient.Group);
    }

    [Fact]
    public async Task Admin_chat_notice_broadcasts_msg_type_10_and_persists_to_history()
    {
        int ch = FirstEnglishChannel + 51;
        using ChatClient live = await LoggedInAsync("chat-notice-live", ch);
        using var scope = server.CreateScope();
        ChatRelay relay = scope.ServiceProvider.GetRequiredService<ChatRelay>();
        Assert.True(relay.BroadcastNotice("Maintenance in 10m", ch));

        JsonObject liveMsg = await live.NextMessageAsync();
        Assert.Equal(10, liveMsg["msg_type"]!.GetValue<int>());
        Assert.Equal("Notice", liveMsg["user_id"]!.GetValue<string>());
        Assert.Equal("Maintenance in 10m", liveMsg["msg_data"]!.GetValue<string>());

        using ChatClient later = await LoggedInAsync("chat-notice-later", ch);
        JsonObject histMsg = await later.NextMessageAsync();
        Assert.Equal(10, histMsg["msg_type"]!.GetValue<int>());
        Assert.Equal("Maintenance in 10m", histMsg["msg_data"]!.GetValue<string>());
        Assert.Equal(0, liveMsg["regist_position"]!.GetValue<int>());
        Assert.Null(liveMsg["end_time"]);
    }

    [Fact]
    public async Task Admin_pinned_chat_notice_uses_position_2_with_an_end_time()
    {
        int ch = FirstEnglishChannel + 52;
        using ChatClient live = await LoggedInAsync("chat-notice-pin", ch);
        using var scope = server.CreateScope();
        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(scope.ServiceProvider.GetRequiredService<ChatRelay>().BroadcastNotice("Event starts now", ch, 600));

        JsonObject message = await live.NextMessageAsync();
        Assert.Equal(10, message["msg_type"]!.GetValue<int>());
        Assert.Equal(2, message["regist_position"]!.GetValue<int>());
        long start = message["start_time"]!.GetValue<long>(), end = message["end_time"]!.GetValue<long>();
        Assert.InRange(start, before, before + 5);
        Assert.Equal(start + 600, end);
    }

    [Fact]
    public async Task Pinned_notice_can_be_listed_edited_and_removed_and_history_follows_the_pin()
    {
        int ch = FirstEnglishChannel + 53;
        using var scope = server.CreateScope();
        ChatRelay relay = scope.ServiceProvider.GetRequiredService<ChatRelay>();
        using (ChatClient live = await LoggedInAsync("chat-pin-edit-live", ch))
        {
            Assert.True(relay.BroadcastNotice("Pin v1", null, 600));
            Assert.Equal("Pin v1", (await live.NextMessageAsync())["msg_data"]!.GetValue<string>());
        }
        KnS.Data.Repositories.ChatPin pin = Assert.Single(relay.ActivePins(), p => p.Message == "Pin v1");

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(relay.UpdatePin(pin.Id, "Pin v2", now + 1200));
        KnS.Data.Repositories.ChatPin edited = Assert.Single(relay.ActivePins(), p => p.Id == pin.Id);
        Assert.Equal("Pin v2", edited.Message);
        Assert.Equal(now + 1200, edited.EndTime);

        // F5 while pinned: the replay carries the edited text, still pinned (position 2) with the new end.
        using (ChatClient reloaded = await LoggedInAsync("chat-pin-edit-f5", ch))
        {
            JsonObject replay = await reloaded.NextMessageAsync();
            Assert.Equal("Pin v2", replay["msg_data"]!.GetValue<string>());
            Assert.Equal(2, replay["regist_position"]!.GetValue<int>());
            Assert.Equal(now + 1200, replay["end_time"]!.GetValue<long>());
        }

        Assert.True(relay.RemovePin(pin.Id));
        Assert.DoesNotContain(relay.ActivePins(), p => p.Id == pin.Id);
        Assert.False(relay.UpdatePin(pin.Id, "Pin v3", now + 600));
        Assert.False(relay.RemovePin(pin.Id));

        // F5 after removal: the line stays, as an ordinary notice that does not pin.
        using ChatClient after = await LoggedInAsync("chat-pin-edit-after", ch);
        JsonObject line = await after.NextMessageAsync();
        Assert.Equal("Pin v2", line["msg_data"]!.GetValue<string>());
        Assert.Equal(0, line["regist_position"]!.GetValue<int>());
        Assert.Null(line["end_time"]);
    }

    [Fact]
    public async Task Saved_notices_are_listed_edited_and_deleted_and_old_pins_replay_unpinned()
    {
        int ch = FirstEnglishChannel + 54;
        using var scope = server.CreateScope();
        ChatRelay relay = scope.ServiceProvider.GetRequiredService<ChatRelay>();
        var history = scope.ServiceProvider.GetRequiredService<KnS.Data.Repositories.ChatHistoryRepository>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // A pinned notice stored the way the server stored it before chat_pin existed (same payload in two groups), plus a
        // player line in the same channel that must never be listed.
        string legacy = Encoding.ASCII.GetString(ChatCipher.Encrypt(Encoding.UTF8.GetBytes(
            $$"""{"msg_type":10,"user_id":"Notice","msg_data":"Legacy pinned {{now}}","regist_position":2,"start_time":{{now}},"end_time":{{now + 86400}}}""")));
        string player = Encoding.ASCII.GetString(ChatCipher.Encrypt(Encoding.UTF8.GetBytes(
            $$"""{"msg_type":0,"user_no":1,"user_id":"someone","msg_data":"player line {{now}}"}""")));
        history.Add(ch, legacy, now);
        history.Add(FirstEnglishChannel, legacy, now);
        history.Add(ch, player, now);

        SavedNotice notice = Assert.Single(relay.SavedNotices(), n => n.Text == $"Legacy pinned {now}");
        Assert.Equal(2, notice.Ids.Count);
        Assert.Equal([FirstEnglishChannel, ch], notice.Groups.Order());
        Assert.Equal(2, notice.Position);
        Assert.DoesNotContain(relay.SavedNotices(), n => n.Text == $"player line {now}");

        // F5: the old pin replays as an ordinary line, so it can no longer pin.
        using (ChatClient f5 = await LoggedInAsync("chat-saved-notice-f5", ch))
        {
            JsonObject line = await f5.NextMessageAsync();
            Assert.Equal($"Legacy pinned {now}", line["msg_data"]!.GetValue<string>());
            Assert.Equal(0, line["regist_position"]!.GetValue<int>());
            Assert.Null(line["end_time"]);
        }

        Assert.Equal(2, relay.EditSavedNotice(notice.Ids, $"Edited {now}"));
        long playerRow = history.All().Single(r => r.Payload == player).Id;
        Assert.Equal(0, relay.EditSavedNotice([playerRow], "player rows are not notices"));
        Assert.Equal(0, relay.DeleteSavedNotice([playerRow]));
        SavedNotice edited = Assert.Single(relay.SavedNotices(), n => n.Text == $"Edited {now}");
        Assert.Equal(notice.Ids, edited.Ids);
        using (ChatClient f5 = await LoggedInAsync("chat-saved-notice-edit", ch))
        {
            Assert.Equal($"Edited {now}", (await f5.NextMessageAsync())["msg_data"]!.GetValue<string>());
        }

        Assert.Equal(2, relay.DeleteSavedNotice(edited.Ids));
        Assert.DoesNotContain(relay.SavedNotices(), n => n.Text == $"Edited {now}");
        using ChatClient after = await LoggedInAsync("chat-saved-notice-gone", ch);
        JsonObject first = await after.NextMessageAsync();
        Assert.Equal($"player line {now}", first["msg_data"]!.GetValue<string>());
    }

    [Fact]
    public async Task Deleting_a_pinned_notice_from_history_ends_its_pin()
    {
        int ch = FirstEnglishChannel + 55;
        using var scope = server.CreateScope();
        ChatRelay relay = scope.ServiceProvider.GetRequiredService<ChatRelay>();
        string text = $"Pin to delete {Guid.NewGuid():N}";
        using (ChatClient live = await LoggedInAsync("chat-saved-pin-live", ch))
        {
            Assert.True(relay.BroadcastNotice(text, null, 600));
            await live.NextMessageAsync();
        }
        SavedNotice notice = Assert.Single(relay.SavedNotices(), n => n.Text == text);
        Assert.True(notice.PinId > 0);
        Assert.Contains(FirstEnglishChannel, notice.Groups);

        Assert.Equal(1, relay.EditSavedNotice(notice.Ids, text + " v2") / notice.Ids.Count);
        Assert.Equal(text + " v2", Assert.Single(relay.ActivePins(), p => p.Id == notice.PinId).Message);

        Assert.Equal(notice.Ids.Count, relay.DeleteSavedNotice(notice.Ids));
        Assert.DoesNotContain(relay.ActivePins(), p => p.Id == notice.PinId);
    }

    [Fact]
    public async Task Invalid_language_change_is_refused_and_empty_nickname_cannot_spoof_user_id()
    {
        int ch = FirstEnglishChannel + 60;
        using ChatClient alice = await LoggedInAsync("chat-hardening-a", ch);
        using ChatClient bob = await LoggedInAsync("chat-hardening-b", ch);

        Assert.Equal(ch, await alice.ChangeLanguageAsync(-5));
        Assert.Equal(ch, await alice.ChangeLanguageAsync(5000));

        await alice.ChatAsync("hello");
        JsonObject msg = await bob.NextMessageAsync();
        Assert.Equal($"Squire{alice.User}", msg["user_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_burst_beyond_rate_limit_is_dropped()
    {
        var relay = new ChatRelay(TimeSpan.FromSeconds(5), maxMessagesPerWindow: 2);
        int key = relay.Authorize(77, "Flooder");
        var transport = new MemoryTransport();
        using var cts = new CancellationTokenSource();
        Task run = relay.RunAsync(transport, cts.Token);

        transport.Send(ChatClient.LoginFrame(77, key, 0));
        byte[] login = await transport.ReceiveAsync();
        long session = BinaryPrimitives.ReadInt64BigEndian(login.AsSpan(4));

        static byte[] BuildChat(long session, string text)
        {
            var json = new JsonObject
            {
                ["msg_type"] = 0,
                ["user_id"] = "Flooder",
                ["user_no"] = 77,
                ["msg_data"] = new JsonArray(new JsonObject { ["lang"] = English, ["msg"] = text }),
            };
            byte[] body = ChatCipher.Encrypt(Encoding.UTF8.GetBytes(json.ToJsonString()));
            var payload = new byte[18 + body.Length];
            BinaryPrimitives.WriteInt64BigEndian(payload, 77);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(8), session);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(16), (ushort)body.Length);
            body.CopyTo(payload, 18);
            return ChatFrame.Build(ChatOpcode.UserChat, payload);
        }

        // Send 3 messages under a cap of 2: each gets a UserChatReply, but only the first 2 are broadcast.
        transport.Send(BuildChat(session, "one"));
        transport.Send(BuildChat(session, "two"));
        transport.Send(BuildChat(session, "three"));

        int broadcasts = 0;
        int replies = 0;
        while (replies < 3)
        {
            byte[] frame = await transport.ReceiveAsync();
            ushort opcode = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
            if (opcode == ChatOpcode.UserChatReply) replies++;
            else if (opcode == ChatOpcode.Message) broadcasts++;
        }

        // Send a ping to flush any queued messages before counting.
        transport.Send(ChatClient.PingFrame(77, session, FirstEnglishChannel));
        await Task.Delay(50);
        await cts.CancelAsync();
        await run.WaitAsync(Patience);

        Assert.Equal(2, broadcasts);
    }

    private sealed class MemoryTransport : IChatTransport
    {
        private readonly Channel<byte[]> _in = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>();

        public void Send(byte[] frame) => _in.Writer.TryWrite(frame);

        public async Task<byte[]> ReceiveAsync() => await _out.Reader.ReadAsync().AsTask().WaitAsync(Patience);

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            byte[] frame = await _in.Reader.ReadAsync(cancellationToken);
            frame.CopyTo(buffer);
            return frame.Length;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        {
            _out.Writer.TryWrite(frame.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ChatClient(WebSocket socket, long user) : IDisposable
    {
        private const long HubUser = 0x141a83ba1;

        public long User { get; } = user;
        public long Session { get; private set; }
        public int Group { get; private set; }

        public void Dispose() => socket.Dispose();

        public static byte[] LoginFrame(long wireUser, int key, int group)
        {
            var payload = new byte[32];
            BinaryPrimitives.WriteInt32BigEndian(payload, 1);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(4), wireUser);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(12), 1);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(16), key);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(20), group);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(24), English);
            return ChatFrame.Build(ChatOpcode.Login, payload);
        }

        public static byte[] PingFrame(long wireUser, long session, int group) => SessionFrame(ChatOpcode.Ping, wireUser, session, group);

        private static byte[] SessionFrame(ushort opcode, long wireUser, long session, int value)
        {
            var payload = new byte[20];
            BinaryPrimitives.WriteInt64BigEndian(payload, wireUser);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(8), session);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(16), value);
            return ChatFrame.Build(opcode, payload);
        }

        public Task SendLoginAsync(int loginKey, int group) => SendAsync(LoginFrame(HubUser, loginKey, group));

        public async Task LoginAsync(int loginKey, int group)
        {
            await SendLoginAsync(loginKey, group);
            byte[] reply = (await ReceiveAsync())!;
            Assert.Equal(ChatOpcode.LoginReply, Opcode(reply));
            Session = BinaryPrimitives.ReadInt64BigEndian(reply.AsSpan(4));
            Group = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(12));
        }

        public Task PingAsync(long session) => SendAsync(PingFrame(HubUser, session, Group));

        public async Task<bool> ChangeGroupAsync(int group)
        {
            await SendAsync(SessionFrame(ChatOpcode.GroupChange, HubUser, Session, group));
            byte[] reply = await NextOfAsync(ChatOpcode.GroupChangeReply);
            Group = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(8));
            return BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(4)) == 1;
        }

        public async Task<int> ChangeLanguageAsync(int language)
        {
            await SendAsync(SessionFrame(ChatOpcode.LanguageChange, HubUser, Session, language));
            byte[] reply = await NextOfAsync(ChatOpcode.LanguageChangeReply);
            return BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(12));
        }

        public async Task ChatAsync(string text, long claimedUser = 0, int messageType = 0)
        {
            var json = new JsonObject
            {
                ["msg_type"] = messageType,
                ["user_id"] = "Claimed",
                ["user_no"] = claimedUser == 0 ? User : claimedUser,
                ["user_pvp_grade"] = 0,
                ["msg_data"] = new JsonArray(new JsonObject { ["lang"] = English, ["msg"] = text }),
            };
            byte[] body = ChatCipher.Encrypt(Encoding.UTF8.GetBytes(json.ToJsonString()));
            var payload = new byte[18 + body.Length];
            BinaryPrimitives.WriteInt64BigEndian(payload, HubUser);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(8), Session);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(16), (ushort)body.Length);
            body.CopyTo(payload, 18);
            await SendAsync(ChatFrame.Build(ChatOpcode.UserChat, payload));
        }

        public async Task<JsonObject> NextMessageAsync()
        {
            byte[] frame = await NextOfAsync(ChatOpcode.Message);
            int length = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4));
            return JsonNode.Parse(ChatCipher.Decrypt(frame.AsSpan(6, length)))!.AsObject();
        }

        public async Task<string> NextMessageTextAsync() =>
            (await NextMessageAsync())["msg_data"]![0]!["msg"]!.GetValue<string>();

        private async Task<byte[]> NextOfAsync(ushort opcode)
        {
            while (true)
            {
                byte[]? frame = await ReceiveAsync();
                Assert.NotNull(frame);

                // A frame of size 4 makes the real client drop the connection (ChatFrame.OfInts).
                Assert.True(frame.Length > ChatFrame.HeaderSize, $"empty frame {Opcode(frame):x}");
                if (Opcode(frame) == opcode)
                {
                    return frame;
                }
            }
        }

        private static ushort Opcode(byte[] frame) => BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));

        private Task SendAsync(byte[] frame) => socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);

        /// <summary>The next frame, or null once the server has dropped the connection.</summary>
        public async Task<byte[]?> ReceiveAsync()
        {
            using var timeout = new CancellationTokenSource(Patience);
            var buffer = new byte[ChatFrame.MaxSize];
            try
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, timeout.Token);
                return result.MessageType == WebSocketMessageType.Close ? null : buffer[..result.Count];
            }
            catch (Exception e) when (e is WebSocketException or IOException)
            {
                return null;
            }
        }
    }
}
