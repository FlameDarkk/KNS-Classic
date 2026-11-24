using System.Text.Json;
using System.Text.Json.Nodes;
using KnS.Data;
using KnS.Data.Repositories;
using KnS.Platform.Accounts;
using KnS.Protocol.Json;

namespace KnS.Server.Endpoints;

/// <summary>
/// KnS id + password accounts (login_account) and their sign-in tokens (account_session).
/// <list type="bullet">
/// <item><c>POST /account/register</c> {id, password, nickname, did?, guest_uid?}: a guest who registers keeps their player.</item>
/// <item><c>POST /account/login</c> {id, password}.</item>
/// <item><c>POST /account/session</c> {token}: the account of a stored sign-in (renews it).</item>
/// <item><c>POST /account/logout</c> {token}.</item>
/// <item><c>POST /account/nickname</c> {token, nickname}: changes the signed-in player's name.</item>
/// <item><c>POST /account/password</c> {token, password, new_password}: signs every other browser out.</item>
/// </list>
/// Register, login and session answer {login_id, did, guest_uid, token}. The router handles these before journaling, so passwords and
/// tokens never reach the request or traffic logs.
/// </summary>
public static class AccountEndpoint
{
    private const string RegisterPath = "/account/register";
    private const string LoginPath = "/account/login";
    private const string SessionPath = "/account/session";
    private const string LogoutPath = "/account/logout";
    private const string NicknamePath = "/account/nickname";
    private const string PasswordPath = "/account/password";

    public static bool Matches(string target) =>
        Route(target) is RegisterPath or LoginPath or SessionPath or LogoutPath or NicknamePath or PasswordPath;

    private static string Route(string target) => target.Split('?', 2)[0].TrimEnd('/').ToLowerInvariant();

    public static async Task ServeAsync(HttpContext context, string target)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await WriteErrorAsync(context, StatusCodes.Status405MethodNotAllowed, "use POST");
            return;
        }

        (JsonObject? body, bool tooLarge) = await ReadBodyAsync(context.Request);
        if (tooLarge)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request body too large");
            return;
        }

        if (body is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "expected a JSON object");
            return;
        }

        var accounts = context.RequestServices.GetRequiredService<PasswordAccountService>();
        var tokens = context.RequestServices.GetRequiredService<AccountTokenService>();
        DataSession session = context.RequestServices.GetRequiredService<DataSession>();
        string route = Route(target);
        if (route is RegisterPath or LoginPath)
        {
            session.BeginTransaction();
            AccountResult result = route == RegisterPath
                ? accounts.Register(Text(body, "id"), Text(body, "password"), Text(body, "did"), Text(body, "guest_uid"), Text(body, "nickname"))
                : accounts.Login(Text(body, "id"), Text(body, "password"));
            if (result.Session is not { } signedIn)
            {
                session.Rollback();
                (int status, string message) = Describe(result.Failure);
                await WriteErrorAsync(context, status, message);
                return;
            }

            string token = tokens.Issue(signedIn.LoginId);
            session.Commit();
            await WriteAsync(context, StatusCodes.Status200OK, Reply(signedIn, token));
            return;
        }

        string? bodyToken = Text(body, "token");
        if (route == LogoutPath)
        {
            tokens.Revoke(bodyToken);
            await WriteAsync(context, StatusCodes.Status200OK, new JsonObject { ["signed_out"] = true });
            return;
        }

        session.BeginTransaction();
        if ((tokens.Verify(bodyToken) is { } loginId ? accounts.Find(loginId) : null) is not { } account)
        {
            session.Rollback();
            await WriteAsync(context, StatusCodes.Status401Unauthorized, new JsonObject
            {
                ["error"] = "Your sign-in has expired. Log in again.",
                ["code"] = "session_expired",
            });
            return;
        }

        switch (route)
        {
            case SessionPath:
                session.Commit();
                await WriteAsync(context, StatusCodes.Status200OK, Reply(account, bodyToken!));
                return;
            case PasswordPath:
                AccountFailure failure = accounts.ChangePassword(account.LoginId, Text(body, "password"), Text(body, "new_password"));
                if (failure != AccountFailure.None)
                {
                    session.Rollback();
                    (int status, string message) = Describe(failure);
                    await WriteErrorAsync(context, failure == AccountFailure.WrongCredentials ? StatusCodes.Status403Forbidden : status,
                        failure == AccountFailure.WrongCredentials ? "The current password is wrong." : message);
                    return;
                }

                tokens.RevokeOthers(account.LoginId, bodyToken!);
                session.Commit();
                await WriteAsync(context, StatusCodes.Status200OK, new JsonObject { ["changed"] = true });
                return;
        }

        string nickname = (Text(body, "nickname") ?? "").Trim();
        if (!PasswordAccountService.IsValidNickname(nickname))
        {
            session.Rollback();
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "Nickname must be 1 to 24 UTF-8 bytes without control characters.");
            return;
        }

        if (accounts.FindPlayer(account) is not { } player)
        {
            session.Rollback();
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "Start the game with this account before changing its nickname.");
            return;
        }

        var players = context.RequestServices.GetRequiredService<PlayerRepository>();
        var loginAccounts = context.RequestServices.GetRequiredService<LoginAccountRepository>();
        if (players.IsNicknameTakenByOther(nickname, player.UserNumber)
            || loginAccounts.IsNicknameTakenByOther(nickname, account.LoginId))
        {
            session.Rollback();
            await WriteErrorAsync(context, StatusCodes.Status409Conflict, "That username is already taken.");
            return;
        }

        players.SetNickname(player.UserNumber, nickname);
        loginAccounts.SetNickname(account.LoginId, nickname);
        session.Commit();
        await WriteAsync(context, StatusCodes.Status200OK, new JsonObject { ["nickname"] = nickname });
    }

    private static JsonObject Reply(AccountSession account, string token) => new()
    {
        ["login_id"] = account.LoginId,
        ["did"] = account.DeviceId,
        ["guest_uid"] = account.GuestUid,
        ["token"] = token,
    };

    private static (int Status, string Message) Describe(AccountFailure failure) => failure switch
    {
        AccountFailure.InvalidId => (StatusCodes.Status400BadRequest, "The ID needs 3 to 24 letters, digits, _ or -."),
        AccountFailure.InvalidPassword => (StatusCodes.Status400BadRequest, "The password needs 8 to 128 characters."),
        AccountFailure.InvalidNickname => (StatusCodes.Status400BadRequest, "The username must be 1 to 24 UTF-8 bytes without control characters."),
        AccountFailure.NicknameTaken => (StatusCodes.Status409Conflict, "That username is already taken."),
        AccountFailure.IdTaken => (StatusCodes.Status409Conflict, "That ID is already taken."),
        AccountFailure.DeviceTaken => (StatusCodes.Status409Conflict, "This player already has an account. Log in instead."),
        AccountFailure.TooManyAttempts => (StatusCodes.Status429TooManyRequests, "Too many wrong passwords. Wait a minute and try again."),
        _ => (StatusCodes.Status401Unauthorized, "Wrong ID or password."),
    };

    private static string? Text(JsonObject body, string key) =>
        body[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static async Task<(JsonObject? Body, bool TooLarge)> ReadBodyAsync(HttpRequest request)
    {
        byte[]? bytes = await RequestBodyLimit.ReadAsync(request, RequestBodyLimit.MaxAccountBytes);
        if (bytes is null)
        {
            return (null, true);
        }

        try
        {
            return (JsonNode.Parse(bytes) as JsonObject, false);
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string message) =>
        WriteAsync(context, status, new JsonObject { ["error"] = message });

    private static async Task WriteAsync(HttpContext context, int status, JsonObject reply)
    {
        byte[] body = WireJson.Serialize(reply);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }
}
