using System.Text.Json.Serialization;
using KnS.Game.Gateway;

namespace KnS.Game.Protocols.Friends;

/// <summary>
/// Protocol 118 (parser case @0x1606f0), request <c>{type}</c>. The client sends type 35 right after the friend list (protocol 62)
/// arrives, and waits for this reply before it shows the friend screens; it reads only the type back.
/// </summary>
public sealed class SocialTypeHandler : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request) =>
        ProtocolResult.Success(new SocialTypeResponse(request.Parameters.GetInt64("type")));
}

public sealed record SocialTypeResponse([property: JsonPropertyName("type")] long Type);
