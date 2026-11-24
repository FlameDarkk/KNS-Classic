using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using KnS.Protocol.Crypto;
using KnS.Protocol.Json;

namespace KnS.Tests.Integration;

/// <summary>A gateway request must prove its user number with the session key of that user's login.</summary>
[Collection(SharedServer.Name)]
public class SessionTests(ServerFixture server)
{
    private static readonly long[] StarterItem = [1001];

    private async Task<JsonObject> RawAsync(int protocol, object parameters, string? userNumber, string? sessionKey)
    {
        var request = new JsonObject { ["params"] = WireJson.ToJsonObject(parameters), ["protocol"] = protocol };
        using var message = new HttpRequestMessage(HttpMethod.Post, "/1mhero/gateway.php")
        {
            Content = new ByteArrayContent(GameGatewayCipher.Encrypt(Encoding.UTF8.GetBytes(request.ToJsonString()))),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        if (userNumber is not null)
        {
            message.Headers.Add("USERNO", userNumber);
        }

        if (sessionKey is not null)
        {
            message.Headers.Add("SESSIONKEY", sessionKey);
        }

        using HttpResponseMessage response = await server.Client.SendAsync(message);
        return WireJson.ParseObject(GameGatewayCipher.Decrypt(await response.Content.ReadAsByteArrayAsync()))!;
    }

    private async Task<(long User, string Key)> LoginAsync(string device)
    {
        JsonObject reply = await RawAsync(51, new { hub_did = device }, null, null);
        return (reply["user_no"]!.GetValue<long>(), reply["session_key"]!.GetValue<string>());
    }

    private static long Errno(JsonObject reply) => reply["errno"]!.GetValue<long>();

    private static string Text(long number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_key_from_login_opens_the_players_data()
    {
        (long user, string key) = await LoginAsync("session-good");

        JsonObject load = await RawAsync(60, new { user_no = user }, Text(user), key);

        Assert.Equal(0, Errno(load));
        Assert.Equal(user, load["user_info"]!["user_no"]!.GetValue<long>());
    }

    [Fact]
    public async Task Another_players_number_does_not_work_with_your_key_or_the_old_guessable_one()
    {
        (long victim, _) = await LoginAsync("session-victim");
        (_, string attackerKey) = await LoginAsync("session-attacker");
        server.Execute($"UPDATE gameuser SET gold=4242 WHERE user_no={victim}");

        JsonObject withOwnKey = await RawAsync(60, new { user_no = victim }, Text(victim), attackerKey);
        JsonObject withOldKey = await RawAsync(83, new { sell_items = StarterItem }, Text(victim), "sk" + Text(victim));
        JsonObject noKey = await RawAsync(93, new { package_id = 9710106 }, Text(victim), null);

        Assert.Equal(1, Errno(withOwnKey));
        Assert.Null(withOwnKey["user_info"]);
        Assert.Equal(1, Errno(withOldKey));
        Assert.Equal(1, Errno(noKey));
        Assert.Equal(4242, server.Scalar($"SELECT gold FROM gameuser WHERE user_no={victim}"));
    }

    [Fact]
    public async Task A_user_no_in_the_request_cannot_differ_from_the_session()
    {
        (long victim, _) = await LoginAsync("session-param-victim");
        (long attacker, string key) = await LoginAsync("session-param-attacker");

        JsonObject reply = await RawAsync(60, new { user_no = victim }, Text(attacker), key);

        Assert.Equal(1, Errno(reply));
        Assert.Null(reply["user_info"]);
    }

    [Fact]
    public async Task A_request_with_no_identity_never_falls_back_to_another_player()
    {
        (long someone, string key) = await LoginAsync("session-someone");
        await RawAsync(60, new { user_no = someone }, Text(someone), key);

        JsonObject reply = await RawAsync(93, new { package_id = 9710106 }, null, null);

        Assert.Equal(1, Errno(reply));
        Assert.Null(reply["item_infos"]);
    }

    [Fact]
    public async Task Login_ignores_a_user_no_and_goes_by_the_device()
    {
        (long victim, _) = await LoginAsync("session-login-victim");

        JsonObject reply = await RawAsync(51, new { hub_did = "session-login-attacker", user_no = victim }, null, null);

        Assert.NotEqual(victim, reply["user_no"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_player_keeps_working_on_two_devices_but_only_the_newest_five_sessions()
    {
        (long user, string first) = await LoginAsync("session-many");
        (_, string second) = await LoginAsync("session-many");
        Assert.Equal(0, Errno(await RawAsync(60, new { user_no = user }, Text(user), first)));
        Assert.Equal(0, Errno(await RawAsync(60, new { user_no = user }, Text(user), second)));

        for (int i = 0; i < 5; i++)
        {
            await LoginAsync("session-many");
        }

        Assert.Equal(1, Errno(await RawAsync(60, new { user_no = user }, Text(user), first)));
    }

    [Fact]
    public async Task Session_keys_are_stored_hashed_and_stay_out_of_the_logs()
    {
        (long user, string key) = await LoginAsync("session-hidden");

        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM player_session WHERE session_hash='{key}' OR session_hash='{key.ToUpperInvariant()}'"));
        Assert.Equal(1, server.Scalar($"SELECT COUNT(*) FROM player_session WHERE user_no={user}"));
        Assert.DoesNotContain(server.ReadTraffic(), line => line.ToJsonString().Contains(key, StringComparison.Ordinal));
    }
}
