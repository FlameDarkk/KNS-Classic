using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Chat;
using KnS.Game.Gateway;

namespace KnS.Game.Protocols.Session;

/// <summary>
/// Protocol 701: where the chat server is and the key to log in with (parser case @0x15b77a, post-handler @0x153cda). A <c>login_key</c> of 0 or a
/// <c>chat_server_retcode</c> of 200 makes the client give up, and <c>is_block</c> 1 mutes it. The cool times are my own tuning: the data has no original value.
/// </summary>
public sealed class ChatServerInfoHandler(PlayerRepository players, ChatRelay relay) : IProtocolHandler
{
    private const int DefaultPort = 8080;

    public ProtocolResult Handle(GatewayRequest request)
    {
        string nickname = players.FindByUserNumber(request.HeaderUserNumber)?.Nickname ?? "";
        (string ip, int port) = SplitHost(request.Host);
        return ProtocolResult.Success(new ChatServerInfoResponse(
            RetryTimeSeconds: 10,
            ChatCoolTimeSeconds: 3,
            ChannelChangeCoolTimeSeconds: 10,
            new ChatServerInfo(ip, port, GameServerId: 1, TimeOutSeconds: 60, relay.Authorize(request.HeaderUserNumber, nickname)),
            new ChatBlockInfo(IsBlocked: 0)));
    }

    /// <summary>The browser ignores these and uses its WebSocket; a TCP client needs them to be a dotted address.</summary>
    private static (string Ip, int Port) SplitHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return ("127.0.0.1", DefaultPort);
        }

        string[] parts = host.Split(':');
        return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out int port) ? port : DefaultPort);
    }
}

public sealed record ChatServerInfoResponse(
    [property: JsonPropertyName("retry_time")] int RetryTimeSeconds,
    [property: JsonPropertyName("chat_cool_time")] int ChatCoolTimeSeconds,
    [property: JsonPropertyName("chat_channel_change_cool_time")] int ChannelChangeCoolTimeSeconds,
    [property: JsonPropertyName("chat_server_info")] ChatServerInfo Server,
    [property: JsonPropertyName("chat_block_info")] ChatBlockInfo Block)
{
    [JsonPropertyName("chat_server_retcode")] public int ReturnCode { get; init; }
}

public sealed record ChatServerInfo(
    [property: JsonPropertyName("ip")] string Ip,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("game_server_id")] int GameServerId,
    [property: JsonPropertyName("time_out")] int TimeOutSeconds,
    [property: JsonPropertyName("login_key")] int LoginKey);

public sealed record ChatBlockInfo(
    [property: JsonPropertyName("is_block")] int IsBlocked);
