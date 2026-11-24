using KnS.Data;
using KnS.Data.Repositories;
using KnS.Game.Gateway;
using KnS.Game.Rules;
using KnS.Protocol;

namespace KnS.Game.Players;

/// <summary>Finds (or registers) the player a request belongs to.</summary>
public sealed class PlayerDirectory(
    PlayerRepository players, LoginAccountRepository accounts, GoodsRepository goods, TimeProvider clock, GameOptions options)
{
    /// <summary>
    /// The player of a login request: its device (hub_did / did), registering a new player for an unknown device. A user_no in the
    /// request means nothing here: it would let anyone log in as anyone.
    /// </summary>
    public Player ResolveDevice(RequestParameters parameters)
    {
        string deviceId = FirstSetText(parameters, "guest", "hub_did", "did");
        string hubUid = FirstSetText(parameters, deviceId, "hub_uid", "hub_sessionkey");

        PlayerRecord? record = players.FindByLinkedDevice(deviceId) ?? players.FindByDevice(deviceId);
        if (record is not null)
        {
            return Player.From(record);
        }

        long createdAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
        // A registered account's username becomes the new player's in-game nickname.
        string nickname = accounts.FindByDevice(deviceId)?.Nickname ?? "";
        long userNumber = players.Create(deviceId, hubUid, createdAt / 1000.0, nickname);
        StartingWallet start = options.Start;
        players.SetStart(userNumber, start.Gold, start.Gems, start.Level);
        if (start.HeroPoints > 0)
        {
            goods.Add(userNumber, CurrencyItems.HeroPoints, start.HeroPoints);
        }

        return Player.From(new PlayerRecord { UserNumber = userNumber, HubUid = hubUid, Nickname = nickname });
    }

    /// <summary>
    /// The player a request belongs to: the USERNO header, which the gateway has already checked against the session key. There is
    /// deliberately no fallback: guessing a player here would act on someone else's account.
    /// </summary>
    public Player ResolveCurrent(GatewayRequest request) =>
        players.FindByUserNumber(request.HeaderUserNumber) is { } player
            ? Player.From(player)
            : throw new InvalidOperationException($"No player {request.HeaderUserNumber} for a request that passed the session check.");

    private static string FirstSetText(RequestParameters parameters, string fallback, params string[] names)
    {
        foreach (string name in names)
        {
            if (parameters.IsSet(name))
            {
                return parameters.GetText(name, fallback);
            }
        }

        return fallback;
    }
}
