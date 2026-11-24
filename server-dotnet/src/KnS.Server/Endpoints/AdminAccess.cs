using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KnS.Protocol.Json;
using KnS.Server.Configuration;

namespace KnS.Server.Endpoints;

/// <summary>The X-Admin-Key check and JSON replies shared by the /admin endpoints.</summary>
internal static class AdminAccess
{
    /// <summary>
    /// False (with the reply already written) unless the request carries the admin key. No key
    /// configured = no endpoint at all, so a public server does not advertise one.
    /// </summary>
    public static async Task<bool> AuthorizeAsync(HttpContext context, ServerOptions options)
    {
        if (string.IsNullOrEmpty(options.AdminKey))
        {
            await WriteAsync(context, StatusCodes.Status404NotFound, new JsonObject { ["error"] = "not found" });
            return false;
        }

        string? offered = context.Request.Headers["X-Admin-Key"].FirstOrDefault();
        // Compares hashes, so the check takes the same time whatever prefix matches.
        if (offered is null || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(offered)), SHA256.HashData(Encoding.UTF8.GetBytes(options.AdminKey))))
        {
            await WriteAsync(context, StatusCodes.Status401Unauthorized, new JsonObject { ["error"] = "bad admin key" });
            return false;
        }

        return true;
    }

    public static async Task<JsonObject?> ReadBodyAsync(HttpContext context)
    {
        byte[]? bytes = await RequestBodyLimit.ReadAsync(context.Request, RequestBodyLimit.MaxAdminBytes);
        if (bytes is null)
        {
            await WriteAsync(context, StatusCodes.Status413PayloadTooLarge, new JsonObject { ["error"] = "request body too large" });
            return null;
        }

        try
        {
            return WireJson.ParseObject(bytes) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    public static async Task WriteAsync(HttpContext context, int status, JsonObject reply)
    {
        byte[] body = Encoding.UTF8.GetBytes(reply.ToJsonString());
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }
}
