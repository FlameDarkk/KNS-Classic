using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KnS.Data;
using KnS.Data.Repositories;
using KnS.Server.Configuration;

namespace KnS.Server.Endpoints;

/// <summary>
/// The feature switches behind the X-Admin-Key header: <c>GET /admin/flags</c> lists them,
/// <c>POST /admin/flags</c> with {feature_key, enabled} sets one (creating its row if missing).
/// </summary>
public sealed partial class FeatureFlagAdminEndpoint(ServerOptions options, TimeProvider clock)
{
    public const string PathPrefix = "/admin/flags";

    public static bool Matches(string target) =>
        target.Split('?', 2)[0].TrimEnd('/').Equals(PathPrefix, StringComparison.OrdinalIgnoreCase);

    public async Task ServeAsync(HttpContext context)
    {
        if (!await AdminAccess.AuthorizeAsync(context, options))
        {
            return;
        }

        var flags = context.RequestServices.GetRequiredService<FeatureFlagRepository>();
        if (HttpMethods.IsPost(context.Request.Method))
        {
            if (await AdminAccess.ReadBodyAsync(context) is not { } body)
            {
                return;
            }

            string key = (body["feature_key"]?.ToString() ?? "").Trim().ToLowerInvariant();
            if (!KeyPattern().IsMatch(key) || body["enabled"] is not JsonValue enabled || !enabled.TryGetValue(out bool on))
            {
                await AdminAccess.WriteAsync(context, StatusCodes.Status400BadRequest,
                    new JsonObject { ["error"] = "needs feature_key (a-z, 0-9, _) and enabled (true/false)" });
                return;
            }

            DataSession session = context.RequestServices.GetRequiredService<DataSession>();
            session.BeginTransaction();
            flags.Set(key, on, clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0);
            session.Commit();
        }
        else if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminAccess.WriteAsync(context, StatusCodes.Status405MethodNotAllowed, new JsonObject { ["error"] = "use GET or POST" });
            return;
        }

        var list = new JsonArray();
        foreach (FeatureFlagRecord flag in flags.List())
        {
            list.Add(new JsonObject
            {
                ["feature_key"] = flag.FeatureKey,
                ["enabled"] = flag.Enabled,
                ["created_date"] = Format(flag.CreatedDate),
                ["update_date"] = Format(flag.UpdateDate),
            });
        }

        await AdminAccess.WriteAsync(context, StatusCodes.Status200OK, new JsonObject { ["flags"] = list });
    }

    private static string Format(double unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds).ToString("u", CultureInfo.InvariantCulture);

    [GeneratedRegex("^[a-z0-9_]{1,64}$")]
    private static partial Regex KeyPattern();
}
