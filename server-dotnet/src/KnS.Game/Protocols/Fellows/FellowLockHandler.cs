using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Players;

namespace KnS.Game.Protocols.Fellows;

/// <summary>Protocol 305: explicit fellow_no/fellow_lock; the reply parser reads one full fellow_info object.</summary>
public sealed class FellowLockHandler(PlayerDirectory directory, FellowRepository fellows, FellowService service) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long userNumber = directory.ResolveCurrent(request).UserNumber;
        long number = request.Parameters.GetInt64("fellow_no");
        long wanted = request.Parameters.GetInt64("fellow_lock");
        if (wanted is not (0 or 1) || !fellows.SetLocked(userNumber, number, wanted))
        {
            return ProtocolResult.Refused("invalid ally lock");
        }
        return ProtocolResult.Success(new FellowLockResponse(service.GetFellowInfos(userNumber, new HashSet<long> { number }).Single()));
    }
}

public sealed record FellowLockResponse([property: JsonPropertyName("fellow_info")] FellowInfo Fellow);
