using System.Text;
using System.Text.Json.Nodes;
using KnS.Data;
using KnS.Data.Repositories;
using KnS.Protocol.Json;
using KnS.Server.Configuration;

namespace KnS.Server.Endpoints;

/// <summary>
/// The owner's view of the tester allowlist, behind the X-Admin-Key header (KNS_ADMIN_KEY):
/// <c>GET /admin/testers</c> lists testers and the refused devices; <c>POST /admin/testers</c>
/// adds {email, note}; <c>.../attach</c> {email, device_id}, <c>.../revoke</c> and
/// <c>.../restore</c> {email} change one. Not journaled: nothing here belongs in a log.
/// </summary>
public sealed class TesterAdminEndpoint(ServerOptions options, TimeProvider clock)
{
    public const string PathPrefix = "/admin/testers";

    public static bool Matches(string target) =>
        target.Split('?', 2)[0].TrimEnd('/').StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase);

    public async Task ServeAsync(HttpContext context, string target)
    {
        if (!await AdminAccess.AuthorizeAsync(context, options))
        {
            return;
        }

        string action = target.Split('?', 2)[0].TrimEnd('/')[PathPrefix.Length..].Trim('/').ToLowerInvariant();
        DataSession session = context.RequestServices.GetRequiredService<DataSession>();
        var testers = context.RequestServices.GetRequiredService<TesterRepository>();

        if (HttpMethods.IsGet(context.Request.Method) && action.Length == 0)
        {
            await AdminAccess.WriteAsync(context, StatusCodes.Status200OK, Overview(testers));
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await AdminAccess.WriteAsync(context, StatusCodes.Status405MethodNotAllowed, new JsonObject { ["error"] = "use GET or POST" });
            return;
        }

        if (await AdminAccess.ReadBodyAsync(context) is not { } body)
        {
            return;
        }

        string email = (body["email"]?.ToString() ?? "").Trim().ToLowerInvariant();
        if (!email.Contains('@', StringComparison.Ordinal) || email.Length > 200)
        {
            await AdminAccess.WriteAsync(context, StatusCodes.Status400BadRequest, new JsonObject { ["error"] = "email is required" });
            return;
        }

        session.BeginTransaction();
        (int status, string message) = Apply(testers, action, email, body);
        if (status == StatusCodes.Status200OK)
        {
            session.Commit();
            await AdminAccess.WriteAsync(context, status, Overview(testers));
        }
        else
        {
            session.Rollback();
            await AdminAccess.WriteAsync(context, status, new JsonObject { ["error"] = message });
        }
    }

    private (int Status, string Message) Apply(TesterRepository testers, string action, string email, JsonObject body)
    {
        TesterRecord? tester = testers.Find(email);
        switch (action)
        {
            case "":
                if (tester is not null)
                {
                    return (StatusCodes.Status409Conflict, "already a tester");
                }

                testers.Add(email, body["note"]?.ToString() ?? "", clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0);
                return (StatusCodes.Status200OK, "");
            case "attach":
                string deviceId = (body["device_id"]?.ToString() ?? "").Trim();
                if (tester is null || deviceId.Length == 0)
                {
                    return (StatusCodes.Status400BadRequest, "needs a known email and a device_id");
                }

                if (testers.FindByDevice(deviceId) is { } owner && owner.Email != email)
                {
                    return (StatusCodes.Status409Conflict, "that device belongs to " + owner.Email);
                }

                testers.SetDevice(email, deviceId);
                return (StatusCodes.Status200OK, "");
            case "revoke" or "restore":
                if (tester is null)
                {
                    return (StatusCodes.Status404NotFound, "no such tester");
                }

                testers.SetRevoked(email, action == "revoke");
                return (StatusCodes.Status200OK, "");
            default:
                return (StatusCodes.Status404NotFound, "unknown action");
        }
    }

    private static JsonObject Overview(TesterRepository testers)
    {
        var list = new JsonArray();
        foreach (TesterRecord tester in testers.List())
        {
            list.Add(new JsonObject
            {
                ["email"] = tester.Email,
                ["device_id"] = tester.DeviceId,
                ["note"] = tester.Note,
                ["revoked"] = tester.Revoked,
            });
        }

        var refused = new JsonArray();
        foreach (RefusedDeviceRecord device in testers.ListRefused())
        {
            refused.Add(new JsonObject
            {
                ["device_id"] = device.DeviceId,
                ["user_no"] = device.UserNumber,
                ["attempts"] = device.Attempts,
                ["last_seen"] = DateTimeOffset.FromUnixTimeSeconds((long)device.LastSeen).ToString("u", System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        return new JsonObject { ["testers"] = list, ["refused"] = refused };
    }
}
