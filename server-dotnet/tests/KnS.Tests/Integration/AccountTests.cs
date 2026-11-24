using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace KnS.Tests.Integration;

/// <summary>KnS id + password accounts: /account/register, /login, /session, /logout, /nickname and /password.</summary>
[Collection(SharedServer.Name)]
public class AccountTests(ServerFixture server)
{
    private const string Password = "test-password-1";

    private async Task<(HttpStatusCode Status, JsonObject Body)> PostAsync(string path, object body)
    {
        using HttpResponseMessage response = await server.Client.PostAsJsonAsync(path, body);
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonObject>())!);
    }

    [Fact]
    public async Task A_registered_id_logs_in_to_the_same_device_ignoring_case()
    {
        var (status, registered) = await PostAsync("/account/register", new { id = "Case_User", password = Password, nickname = "Case Hero" });
        var (loginStatus, login) = await PostAsync("/account/login", new { id = "case_user", password = Password });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(HttpStatusCode.OK, loginStatus);
        Assert.Equal("case_user", login["login_id"]!.GetValue<string>());
        Assert.InRange(registered["did"]!.GetValue<string>().Length, 1, 28); // Longer ids overflow the game's buffer.
        Assert.Equal(registered["did"]!.GetValue<string>(), login["did"]!.GetValue<string>());
        Assert.Equal(registered["guest_uid"]!.GetValue<string>(), login["guest_uid"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_guest_who_registers_keeps_their_player_on_another_browser()
    {
        const string GuestDevice = "web-guest-keeps";
        long user = (await server.CallGatewayAsync(51, new { hub_did = GuestDevice }))["user_no"]!.GetValue<long>();

        await PostAsync("/account/register", new { id = "keeper", password = Password, nickname = "Keeper Hero", did = GuestDevice, guest_uid = "905123" });
        var (_, login) = await PostAsync("/account/login", new { id = "keeper", password = Password });
        JsonObject again = await server.CallGatewayAsync(51, new { hub_did = login["did"]!.GetValue<string>() });

        Assert.Equal(GuestDevice, login["did"]!.GetValue<string>());
        Assert.Equal("905123", login["guest_uid"]!.GetValue<string>());
        Assert.Equal(user, again["user_no"]!.GetValue<long>());
    }

    [Fact]
    public async Task Nickname_change_authenticates_and_persists_for_the_correct_player()
    {
        var (_, registered) = await PostAsync("/account/register", new { id = "nickname-user", password = Password, nickname = "Login Namer" });
        string device = registered["did"]!.GetValue<string>();
        long user = (await server.CallGatewayAsync(51, new { hub_did = device }))["user_no"]!.GetValue<long>();
        string token = registered["token"]!.GetValue<string>();
        var denied = await PostAsync("/account/nickname", new { token = "not-a-token", nickname = "Intruder" });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.Status);
        var invalid = await PostAsync("/account/nickname", new { token, nickname = new string('x', 25) });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);
        var changed = await PostAsync("/account/nickname", new { token, nickname = "Knight Alice" });
        Assert.Equal(HttpStatusCode.OK, changed.Status);
        Assert.Equal("Knight Alice", changed.Body["nickname"]!.GetValue<string>());
        JsonObject profile = await server.CallGatewayAsync(60, new { user_no = user }, user);
        Assert.Equal("Knight Alice", profile["user_info"]!["user_nickname"]!.GetValue<string>());
        Assert.DoesNotContain(server.ReadTraffic(), line => line.ToJsonString().Contains(Password, StringComparison.Ordinal)
            || line.ToJsonString().Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bad_registrations_are_refused()
    {
        await PostAsync("/account/register", new { id = "taken", password = Password, nickname = "Taken Hero", did = "web-taken-device" });

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync("/account/register", new { id = "TAKEN", password = Password, nickname = "Second Hero" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync("/account/register", new { id = "other1", password = Password, nickname = "Third Hero", did = "web-taken-device" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/account/register", new { id = "ab", password = Password, nickname = "Short Id" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/account/register", new { id = "bad id!", password = Password, nickname = "Bad Id" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/account/register", new { id = "shortpass", password = "1234567", nickname = "Short Pass" })).Status);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_id_get_the_same_answer()
    {
        await PostAsync("/account/register", new { id = "same-answer", password = Password, nickname = "Same Answer" });

        var wrong = await PostAsync("/account/login", new { id = "same-answer", password = "wrong-password" });
        var unknown = await PostAsync("/account/login", new { id = "nobody-here", password = "wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.Status);
        Assert.Equal(wrong.Body["error"]!.GetValue<string>(), unknown.Body["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_id_even_for_the_right_password()
    {
        await PostAsync("/account/register", new { id = "locked-out", password = Password, nickname = "Locked Out" });
        for (int i = 0; i < 5; i++)
        {
            await PostAsync("/account/login", new { id = "locked-out", password = "wrong-password" });
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync("/account/login", new { id = "locked-out", password = Password })).Status);
    }

    [Fact]
    public async Task The_password_is_hashed_and_never_reaches_the_logs()
    {
        const string Secret = "secret-marker-password";
        await server.CallGatewayAsync(141, new { }); // Creates the traffic log, so the check below reads a real file.
        await PostAsync("/account/register", new { id = "no-logs", password = Secret, nickname = "No Logs" });
        await PostAsync("/account/login", new { id = "no-logs", password = Secret });

        Assert.DoesNotContain(server.ReadTraffic(), line => line.ToJsonString().Contains(Secret, StringComparison.Ordinal));
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM login_account WHERE password_hash LIKE '%{Secret}%'"));
        Assert.Equal(1, server.Scalar("SELECT COUNT(*) FROM login_account WHERE login_id='no-logs' AND password_hash LIKE 'pbkdf2-sha256$%'"));
    }

    [Fact]
    public async Task Register_username_becomes_the_new_players_in_game_name()
    {
        var (_, registered) = await PostAsync("/account/register", new { id = "username-user", password = Password, nickname = "Knight Bob" });
        string device = registered["did"]!.GetValue<string>();

        // A fresh device adopts the stored username when the game creates its player.
        JsonObject login = await server.CallGatewayAsync(51, new { hub_did = device });
        long user = login["user_no"]!.GetValue<long>();
        JsonObject profile = await server.CallGatewayAsync(60, new { user_no = user }, user);
        Assert.Equal("Knight Bob", profile["user_info"]!["user_nickname"]!.GetValue<string>());
    }

    [Fact]
    public async Task Guest_register_applies_username_immediately_and_overwrites()
    {
        const string GuestDevice = "web-guest-names";
        long user = (await server.CallGatewayAsync(51, new { hub_did = GuestDevice }))["user_no"]!.GetValue<long>();

        await PostAsync("/account/register", new { id = "stealer", password = Password, nickname = "Taken Name", did = GuestDevice, guest_uid = "905124" });
        JsonObject profile = await server.CallGatewayAsync(60, new { user_no = user }, user);
        Assert.Equal("Taken Name", profile["user_info"]!["user_nickname"]!.GetValue<string>());
    }

    [Fact]
    public async Task Register_refuses_missing_invalid_or_taken_usernames()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/account/register", new { id = "noname", password = Password })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/account/register", new { id = "emptyname", password = Password, nickname = "   " })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/account/register", new { id = "longname", password = Password, nickname = new string('x', 25) })).Status);

        await PostAsync("/account/register", new { id = "first-name", password = Password, nickname = "Unique Hero" });
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync("/account/register", new { id = "second-name", password = Password, nickname = "unique hero" })).Status);
    }

    [Fact]
    public async Task A_session_token_restores_the_account_until_logout()
    {
        var (_, registered) = await PostAsync("/account/register", new { id = "session-user", password = Password, nickname = "Session Hero" });
        string token = registered["token"]!.GetValue<string>();
        Assert.Equal(64, token.Length);
        Assert.Equal(0, server.Scalar($"SELECT COUNT(*) FROM account_session WHERE token_hash='{token}'"));

        var (status, restored) = await PostAsync("/account/session", new { token });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("session-user", restored["login_id"]!.GetValue<string>());
        Assert.Equal(registered["did"]!.GetValue<string>(), restored["did"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/account/logout", new { token })).Status);
        var expired = await PostAsync("/account/session", new { token });
        Assert.Equal(HttpStatusCode.Unauthorized, expired.Status);
        Assert.Equal("session_expired", expired.Body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_expired_token_is_refused_and_use_renews_a_live_one()
    {
        var (_, login) = await PostAsync("/account/register", new { id = "expiry-user", password = Password, nickname = "Expiry Hero" });
        string token = login["token"]!.GetValue<string>();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        server.Execute($"UPDATE account_session SET expires=expires-86400*29 WHERE token_hash='{hash}'");
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/account/session", new { token })).Status);
        Assert.True(server.Scalar($"SELECT CAST(expires-created AS INTEGER) FROM account_session WHERE token_hash='{hash}'") > 86400 * 29);

        server.Execute($"UPDATE account_session SET expires=1 WHERE token_hash='{hash}'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("/account/session", new { token })).Status);
    }

    [Fact]
    public async Task Changing_the_password_needs_the_old_one_and_signs_out_other_browsers()
    {
        var (_, first) = await PostAsync("/account/register", new { id = "pw-user", password = Password, nickname = "Password Hero" });
        var (_, second) = await PostAsync("/account/login", new { id = "pw-user", password = Password });
        string token = first["token"]!.GetValue<string>(), other = second["token"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync("/account/password", new { token, password = "wrong-password", new_password = "new-password-1" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/account/password", new { token, password = Password, new_password = "short" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/account/password", new { token, password = Password, new_password = "new-password-1" })).Status);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/account/session", new { token })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("/account/session", new { token = other })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("/account/login", new { id = "pw-user", password = Password })).Status);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/account/login", new { id = "pw-user", password = "new-password-1" })).Status);
    }

    [Fact]
    public async Task A_urlsafe_pbkdf2_password_hash_still_logs_in()
    {
        // Alternate format: pbkdf2_sha256$iterations$urlsafe-salt$urlsafe-digest, unpadded.
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] digest = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Password), salt, 1000, HashAlgorithmName.SHA256, 32);
        static string UrlSafe(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        server.Execute("INSERT INTO login_account(login_id,password_hash,did,guest_uid,nickname,created) VALUES('moved-back',"
            + $"'pbkdf2_sha256$1000${UrlSafe(salt)}${UrlSafe(digest)}','web-moved-back','906000001','Moved Back',0)");

        var (status, login) = await PostAsync("/account/login", new { id = "Moved-Back", password = Password });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("web-moved-back", login["did"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("/account/login", new { id = "moved-back", password = "wrong-password" })).Status);
    }

    [Fact]
    public async Task An_android_guest_who_registers_keeps_their_player()
    {
        const string AndroidGuestDevice = "and-0123456789abcdef";
        long user = (await server.CallGatewayAsync(51, new { hub_did = AndroidGuestDevice }))["user_no"]!.GetValue<long>();

        var (status, registered) = await PostAsync("/account/register",
            new { id = "android-keeper", password = Password, nickname = "Android Hero", did = AndroidGuestDevice, guest_uid = "905999" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(AndroidGuestDevice, registered["did"]!.GetValue<string>());
        JsonObject again = await server.CallGatewayAsync(51, new { hub_did = registered["did"]!.GetValue<string>() });
        Assert.Equal(user, again["user_no"]!.GetValue<long>());
    }

    [Fact]
    public async Task Nickname_change_enforces_uniqueness_allows_recasing_and_syncs_login_account()
    {
        var (_, first) = await PostAsync("/account/register", new { id = "nick-one", password = Password, nickname = "Knight One" });
        var (_, second) = await PostAsync("/account/register", new { id = "nick-two", password = Password, nickname = "Knight Two" });
        await server.CallGatewayAsync(51, new { hub_did = first["did"]!.GetValue<string>() });
        await server.CallGatewayAsync(51, new { hub_did = second["did"]!.GetValue<string>() });
        string token1 = first["token"]!.GetValue<string>();

        // Colliding with another player (case-insensitive) is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync("/account/nickname", new { token = token1, nickname = "knight two" })).Status);

        // Changing casing of one's own name succeeds and syncs login_account.nickname.
        var recased = await PostAsync("/account/nickname", new { token = token1, nickname = "KNIGHT ONE" });
        Assert.Equal(HttpStatusCode.OK, recased.Status);
        Assert.Equal("KNIGHT ONE", recased.Body["nickname"]!.GetValue<string>());

        // Renaming to a brand-new name frees the old name and updates login_account.nickname.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/account/nickname", new { token = token1, nickname = "Knight Prime" })).Status);
        Assert.Equal(1, server.Scalar("SELECT COUNT(*) FROM login_account WHERE login_id='nick-one' AND nickname='Knight Prime'"));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/account/register", new { id = "nick-three", password = Password, nickname = "Knight One" })).Status);
    }

    [Fact]
    public async Task Oversized_chunked_account_request_is_rejected_with_413()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"id\":\"" + new string('a', 5000) + "\"}");
        using var stream = new MemoryStream(payload);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/account/login") { Content = content };
        request.Headers.TransferEncodingChunked = true;

        using HttpResponseMessage response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}
