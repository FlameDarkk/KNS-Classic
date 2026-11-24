using System.Text;

namespace KnS.Protocol.Crypto;

/// <summary>
/// The chat server's message bodies: AES-128-CBC with PKCS#7 padding, then base64
/// (ChattingServer_ChatReq@0x1636e8 and NetSocketReceiveProc@0x1620e0 in libgame.so).
/// </summary>
public static class ChatCipher
{
    /// <summary>The embedded key string at 0x1ce358 in libgame.so.</summary>
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("hero!HUB/chat@1m");

    /// <summary>The constant IV at 0x1D2594 in libgame.so, shared with the game gateway.</summary>
    private static readonly byte[] InitializationVector =
        Convert.FromHexString("de295106d3872f949783942fdc9f01e2");

    public static byte[] Decrypt(ReadOnlySpan<byte> base64Body) =>
        AesCbc.Decrypt(LenientBase64.Decode(base64Body), Key, InitializationVector);

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        byte[] ciphertext = AesCbc.Encrypt(plaintext, Key, InitializationVector);
        return Encoding.ASCII.GetBytes(Convert.ToBase64String(ciphertext));
    }
}
