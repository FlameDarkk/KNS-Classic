using System.Net.WebSockets;
using KnS.Game.Chat;

namespace KnS.Server.Endpoints;

/// <summary>
/// /chat: the chat server for the browser build, whose socket layer sends its TCP bytes as binary WebSocket messages
/// (runtime.js). The relay treats them as one stream, so message boundaries do not matter.
/// </summary>
public static class ChatEndpoint
{
    public static async Task HandleAsync(HttpContext context, ChatRelay relay)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        try
        {
            await relay.RunAsync(new WebSocketChatTransport(socket), context.RequestAborted);
        }
        catch (WebSocketException)
        {
            // The client vanished without a close handshake (a closed tab).
        }
    }

    private sealed class WebSocketChatTransport(WebSocket socket) : IChatTransport
    {
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);
            return result.MessageType == WebSocketMessageType.Close ? 0 : result.Count;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken) =>
            socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
    }
}
