using System.Buffers.Binary;

namespace KnS.Game.Chat;

/// <summary>
/// The chat server's frame opcodes. A frame is big-endian: u16 size (header included), u16 opcode, payload
/// (NetSocketWriteData@0x1630e8, NetSocketReadCB@0x162ea8).
/// </summary>
public static class ChatOpcode
{
    /// <summary>i64 user, i64 session, i32 group. ChattingServer_PingCB@0x163300; the client expects no reply.</summary>
    public const ushort Ping = 0x3eb;

    /// <summary>i64 user, i64 session, i32 group. ChattingServer_GroupChangeReq@0x1631fc.</summary>
    public const ushort GroupChange = 0x3ed;

    /// <summary>i32 ok (1), i32 group.</summary>
    public const ushort GroupChangeReply = 0x3ee;

    /// <summary>i64 user, i64 session, u16 length, base64 body. ChattingServer_UserChatReq@0x163a48.</summary>
    public const ushort UserChat = 0x3ef;

    /// <summary>i32 1 (unread); lets the client send its next message. Must not be empty, see <see cref="ChatFrame.OfInts"/>.</summary>
    public const ushort UserChatReply = 0x3f0;

    /// <summary>u16 length, base64 body. Case @0x1621a6 of NetSocketReceiveProc.</summary>
    public const ushort Message = 0x3f2;

    /// <summary>The all-channels form of <see cref="UserChat"/>. No client code sends it.</summary>
    public const ushort WideChat = 0x3f5;

    /// <summary>i32 1 (unread); the reply to <see cref="WideChat"/>.</summary>
    public const ushort WideChatReply = 0x3f6;

    /// <summary>i32 1, i64 hub user, i32 game server, i32 login key, i32 group, i32 language, i32 0. NetSocketConnectCB@0x163394.</summary>
    public const ushort Login = 0x3f9;

    /// <summary>i64 session, i32 group, i32 0, i32 0. Case @0x162250.</summary>
    public const ushort LoginReply = 0x3fa;

    /// <summary>i64 user, i64 session, i32 language. ChattingServer_LangCodeChangeReq@0x163174.</summary>
    public const ushort LanguageChange = 0x3fb;

    /// <summary>i32 ok (1), i32 language, i32 group. Case @0x162330.</summary>
    public const ushort LanguageChangeReply = 0x3fc;
}

/// <summary>Builds and reads the big-endian frames of <see cref="ChatOpcode"/>.</summary>
public static class ChatFrame
{
    public const int HeaderSize = 4;

    /// <summary>The client's receive and send buffers are 0x4000 bytes (the clear at NetSocketReadCB).</summary>
    public const int MaxSize = 0x4000;

    public static byte[] Build(ushort opcode, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)frame.Length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), opcode);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>
    /// Every reply needs a payload: NetSocketReadCB treats a header with size 4 as "read 0 more bytes", CS_netSocketRead refuses that, and the
    /// client drops the connection.
    /// </summary>
    public static byte[] OfInts(ushort opcode, params int[] values)
    {
        var payload = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(i * 4), values[i]);
        }

        return Build(opcode, payload);
    }

    public static byte[] Login(long session, int group)
    {
        var payload = new byte[20];
        BinaryPrimitives.WriteInt64BigEndian(payload, session);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), group);
        return Build(ChatOpcode.LoginReply, payload);
    }

    public static byte[] Message(ReadOnlySpan<byte> base64Body)
    {
        var payload = new byte[2 + base64Body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)base64Body.Length);
        base64Body.CopyTo(payload.AsSpan(2));
        return Build(ChatOpcode.Message, payload);
    }
}
