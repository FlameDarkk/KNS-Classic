using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnS.Data;
using KnS.Game.Access;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Protocol;
using KnS.Protocol.Crypto;
using KnS.Protocol.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KnS.Game.Gateway;

/// <summary>
/// The native game gateway (/1mhero/gateway.php): decrypts a request, runs its protocol's
/// handler in one transaction and encrypts the reply.
/// </summary>
/// <remarks>
/// Request JSON: <c>{"protocol": N, "params": {...}}</c>. The reply is FLAT:
/// <c>{"protocol": N, "errno": 0, "error": "", &lt;fields&gt;}</c>. JanssonParser reads protocol,
/// errno and every payload field straight off the root, so nesting the payload makes every field
/// read as zero. errno 0 is the success path; anything else opens the error popup. Every field
/// is optional to the client: a missing one reads as 0 / "" / empty.
/// </remarks>
public sealed class GameGateway(
    IServiceProvider services,
    DataSession session,
    PlayerAllowlist allowlist,
    SessionService sessions,
    GatewayTrace trace,
    ILogger<GameGateway> logger)
{
    private const int LoggedParameterLength = 2000;
    private const int LoggedReplyLength = 240;
    private const int MaxTransactionAttempts = 3;
    private const string SessionKeyField = "session_key";

    /// <param name="body">The raw request body (base64 ciphertext).</param>
    /// <param name="userNumberHeader">The USERNO header, if any.</param>
    /// <param name="sessionKey">The SESSIONKEY header, if any.</param>
    /// <param name="host">The Host header, if any.</param>
    /// <returns>The encrypted reply body.</returns>
    public byte[] Handle(ReadOnlySpan<byte> body, string? userNumberHeader, string? sessionKey, string? host)
    {
        JsonObject? request = TryDecrypt(body);
        if (request is null)
        {
            trace.Reply = new JsonObject { ["protocol"] = 0, ["errno"] = -1, ["error"] = "decrypt failed" };
            return EncryptReply(trace.Reply);
        }

        JsonNode? protocolNode = request["protocol"];
        trace.Protocol = protocolNode?.DeepClone();
        trace.Request = (JsonObject)request.DeepClone();
        var parameters = new RequestParameters(request["params"] as JsonObject);
        var gatewayRequest = new GatewayRequest(parameters, ParseUserNumber(userNumberHeader), host);

        string protocolText = protocolNode?.ToJsonString() ?? "null";
        logger.LogInformation("protocol={Protocol} params={Params}", protocolText, Truncate(parameters.ToJsonString(), LoggedParameterLength));

        IProtocolHandler? handler = LenientJson.IsInteger(protocolNode, out long protocol) && protocol is >= int.MinValue and <= int.MaxValue
            ? services.GetKeyedService<IProtocolHandler>((int)protocol)
            : null;
        if (handler is null)
        {
            logger.LogInformation("No handler for protocol {Protocol}: replying bare errno=0", protocolText);
        }

        JsonObject reply = RunInTransaction(() =>
        {
            // A retried transaction runs the handler again: keep only the last run's findings.
            trace.Notes.Clear();
            trace.Warnings.Clear();
            if (allowlist.IsEnabled && !allowlist.Permits(LenientJson.IsInteger(protocolNode, out long listed) ? (int)listed : 0, gatewayRequest))
            {
                logger.LogWarning("Allowlist refused protocol {Protocol}: {Identity}. Attach the device to a tester to let them in.", protocolText, PlayerAllowlist.Describe(gatewayRequest));
                return BuildReply(protocolNode, ProtocolResult.Refused("not on the tester list"));
            }

            if (handler is not null && NeedsSession(protocol) && !IsAuthorized(gatewayRequest, sessionKey))
            {
                logger.LogInformation("Refused protocol {Protocol} for user {User}: no valid session", protocolText, gatewayRequest.HeaderUserNumber);
                return BuildReply(protocolNode, ProtocolResult.Refused("session expired"));
            }

            if (handler is not null && gatewayRequest.HeaderUserNumber > 0 && NeedsSession(protocol))
            {
                services.GetRequiredService<KnS.Data.Repositories.PlayerRepository>()
                    .TouchSeen(gatewayRequest.HeaderUserNumber, services.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeSeconds());
            }

            if (handler is not null && gatewayRequest.HeaderUserNumber > 0)
                services.GetRequiredService<KnS.Data.Repositories.AchievementRepository>().Reconcile(gatewayRequest.HeaderUserNumber);
            ProtocolResult result = handler?.Handle(gatewayRequest) ?? ProtocolResult.Success(EmptyObject.Instance);
            if (handler is not null && gatewayRequest.HeaderUserNumber > 0 && !result.IsRefused)
            {
                RecordAchievementProgress(gatewayRequest);
            }

            JsonObject built = BuildReply(protocolNode, result);
            if (handler is not null) AddAchievementProgress(built, gatewayRequest.HeaderUserNumber);
            return built;
        }, null);

        JsonObject logged = ForLog(reply);
        trace.Reply = logged;
        trace.UnreadParameters = parameters.UnreadNames;
        string replyText = reply.ToJsonString(WireJson.Options);
        logger.LogInformation("-> {Reply}", Truncate(logged.ToJsonString(WireJson.Options), LoggedReplyLength));
        return GameGatewayCipher.Encrypt(Encoding.UTF8.GetBytes(replyText));
    }

    /// <summary>Only what the title screen sends before there is a login goes without a session.</summary>
    private static bool NeedsSession(long protocol) =>
        protocol is not ((long)GameProtocol.VersionCheck or (long)GameProtocol.Login);

    /// <summary>The session key must belong to USERNO, and a user_no in the request can only repeat it.</summary>
    private bool IsAuthorized(GatewayRequest request, string? sessionKey) =>
        sessions.IsValid(request.HeaderUserNumber, sessionKey)
        && (!request.Parameters.IsSet("user_no") || request.Parameters.GetInt64OrFallback("user_no") == request.HeaderUserNumber);

    /// <summary>The reply as logged: a session key is a credential.</summary>
    private static JsonObject ForLog(JsonObject reply)
    {
        var copy = (JsonObject)reply.DeepClone();
        if (copy.ContainsKey(SessionKeyField))
        {
            copy[SessionKeyField] = "(hidden)";
        }

        return copy;
    }

    /// <summary>
    /// Runs the request as one transaction. One that collides with another player's (deadlock,
    /// lock timeout) is rolled back and run again; the client's post-login burst sends several
    /// requests at once.
    /// </summary>
    private T RunInTransaction<T>(Func<T> work, IsolationLevel? isolation)
    {
        for (int attempt = 1; ; attempt++)
        {
            session.BeginTransaction(isolation);
            try
            {
                T result = work();
                session.Commit();
                return result;
            }
            catch (Exception e) when (attempt < MaxTransactionAttempts && session.Dialect.IsTransientConflict(e))
            {
                session.Rollback();
                logger.LogWarning("Transaction conflict ({Error}); retrying, attempt {Attempt}", e.Message, attempt + 1);
            }
        }
    }

    /// <summary>Achievement type of "Reach Lv. N" (dt_achieve_pkg id / 100). The client keeps no local counter for it.</summary>
    private const long LevelAchievementType = 40000;

    /// <summary>
    /// Persists client-only event increments. Native InsertJsonDataToAchieveDoing clears its pending buffer after
    /// serialization; stats and highest combo are maxima, while kills and action counts accumulate.
    /// Server-owned counters ignore client assertions to avoid counting an accepted action twice.
    /// </summary>
    private void RecordAchievementProgress(GatewayRequest request)
    {
        IReadOnlyList<JsonNode?> list = request.Parameters.GetList("achievement_list");
        if (list.Count == 0)
        {
            return;
        }

        var achievements = services.GetRequiredService<KnS.Data.Repositories.AchievementRepository>();
        foreach (JsonObject entry in list.OfType<JsonObject>())
        {
            if (LenientJson.TryToInt64(entry["type"]) is not { } type || type == LevelAchievementType
                || LenientJson.TryToInt64(entry["count"]) is not { } count || count <= 0)
            {
                continue;
            }

            // These counters come from accepted server operations or persisted ownership, never client assertions.
            if (type is 40006 or 40007 or 40009 or 40031 or 40057 or 40067 or 40082
                || type is >= 41001 and <= 42025) continue;
            if (type is >= 40001 and <= 40005 or 40051)
                achievements.RecordProgress(request.HeaderUserNumber, type, 0, count);
            else achievements.AddProgress(request.HeaderUserNumber, type, count);
        }
    }

    private void AddAchievementProgress(JsonObject reply, long userNumber)
    {
        if (userNumber <= 0 || reply["errno"]?.GetValue<int>() != 0
            || !LenientJson.IsInteger(reply["protocol"], out _))
        {
            return;
        }

        if (reply["update_achievement_info"] is not JsonArray list)
        {
            list = [];
            reply["update_achievement_info"] = list;
        }

        var account = services.GetRequiredService<KnS.Data.Repositories.PlayerRepository>().FindAccount(userNumber);
        if (account is null)
        {
            return;
        }

        var achievements = services.GetRequiredService<KnS.Data.Repositories.AchievementRepository>();
        achievements.Reconcile(userNumber);
        list.Clear();
        foreach (var row in achievements.Progress(userNumber))
            list.Add(new JsonObject { ["idx"] = 0, ["type"] = row.Type, ["sub_type"] = row.SubType, ["count"] = row.Count });
    }

    /// <summary>The flat envelope with the payload's fields (or the refusal) on the root.</summary>
    private static JsonObject BuildReply(JsonNode? protocolNode, ProtocolResult result)
    {
        var reply = new JsonObject
        {
            ["protocol"] = protocolNode?.DeepClone(),
            ["errno"] = result.IsRefused ? result.ErrorNumber : 0,
            ["error"] = result.Refusal ?? "",
        };
        if (result.Payload is { } payload)
        {
            JsonObject fields = WireJson.ToJsonObject(payload);
            foreach (string name in fields.Select(field => field.Key).ToList())
            {
                JsonNode? value = fields[name];
                fields.Remove(name);
                reply[name] = value;
            }
        }

        return reply;
    }

    private JsonObject? TryDecrypt(ReadOnlySpan<byte> body)
    {
        try
        {
            return WireJson.ParseObject(GameGatewayCipher.Decrypt(body))
                ?? throw new JsonException("The request is not a JSON object.");
        }
        catch (Exception e) when (e is FormatException or CryptographicException or JsonException or ArgumentException)
        {
            logger.LogWarning("Decrypt failed: {Error}", e.Message);
            return null;
        }
    }

    private static byte[] EncryptReply(JsonObject reply) =>
        GameGatewayCipher.Encrypt(Encoding.UTF8.GetBytes(reply.ToJsonString(WireJson.Options)));

    private static long ParseUserNumber(string? header) =>
        long.TryParse(header?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number) ? number : 0;

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}
