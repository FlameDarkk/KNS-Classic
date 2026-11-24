using KnS.Data;
using KnS.Game;
using KnS.Platform;
using KnS.Server.Configuration;
using KnS.Server.Diagnostics;
using KnS.Server.Endpoints;
using KnS.Server.Routing;
using Microsoft.Extensions.Logging.Console;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;

// KnS mock backend: the game gateway, the Com2uS platform surfaces and the addition-file CDN.
// Run from the repository root (work/ and original/ are relative to it):
//   dotnet run --project server-dotnet/src/KnS.Server -- 8080
ServerOptions serverOptions = EnvironmentSettings.ReadServerOptions(args);
DatabaseOptions databaseOptions = EnvironmentSettings.ReadDatabaseOptions();
GameOptions gameOptions = EnvironmentSettings.ReadGameOptions();

WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
builder.WebHost.UseUrls($"http://{serverOptions.BindAddress}:{serverOptions.Port}");
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
    console.ColorBehavior = LoggerColorBehavior.Disabled;
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services
    .AddSingleton(serverOptions)
    .AddKnSData(databaseOptions)
    .AddKnSGame(gameOptions)
    .AddKnSPlatform()
    .AddSingleton<RequestJournal>()
    .AddSingleton<TrafficLog>()
    .AddSingleton<HealthCheck>()
    .AddSingleton<AdditionFileEndpoint>()
    .AddSingleton<TesterAdminEndpoint>()
    .AddSingleton<FeatureFlagAdminEndpoint>()
    .AddSingleton<AccountEditorEndpoint>()
    .AddSingleton<RequestRouter>();

builder.Services.AddRateLimiter(limits =>
{
    limits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limits.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.Request.Path.StartsWithSegments("/admin")
            ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
            : RateLimitPartition.GetNoLimiter("other"));
});

WebApplication app = builder.Build();
// The pipeline follows the registered options, so test hosts can swap them.
serverOptions = app.Services.GetRequiredService<ServerOptions>();
// Only loopback (and any KNS_TRUSTED_PROXIES CIDRs/IPs, such as a Docker reverse proxy) may supply forwarded headers.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
foreach (string entry in serverOptions.TrustedProxies)
{
    string[] parts = entry.Split('/', 2);
    if (parts.Length == 2
        && System.Net.IPAddress.TryParse(parts[0], out System.Net.IPAddress? network)
        && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int prefixLength)
        && prefixLength <= (network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
    {
        forwardedHeaders.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(network, prefixLength));
    }
    else if (parts.Length == 1 && System.Net.IPAddress.TryParse(parts[0], out System.Net.IPAddress? proxy))
    {
        forwardedHeaders.KnownProxies.Add(proxy);
    }
}
app.UseForwardedHeaders(forwardedHeaders);

// Everything lives under KNS_PUBLIC_BASE (e.g. /games/kns/play/ behind a shared site); /health also answers at the root.
if (serverOptions.PublicBase != "/")
{
    string publicBase = serverOptions.PublicBase.TrimEnd('/');
    app.UsePathBase(publicBase);
    app.Use(async (context, next) =>
    {
        if (!context.Request.PathBase.HasValue && context.Request.Path != "/health")
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (context.Request.PathBase.HasValue && !context.Request.Path.HasValue)
        {
            // Relative URLs in the page need the trailing slash.
            context.Response.Redirect(serverOptions.PublicBase + context.Request.QueryString, permanent: true, preserveMethod: true);
            return;
        }

        await next();
    });
}

app.UseWebSockets();

// With a web root the base serves the browser build and the API moves under <base>api/; without one (local development) the API
// stays at the base.
if (serverOptions.WebRoot is { } webRoot)
{
    app.MapWhen(context => !context.Request.Path.StartsWithSegments("/api") && context.Request.Path != "/health",
        files => WebFiles.Use(files, webRoot));
    app.UsePathBase("/api");
}

// Routing after the path bases, so endpoints match the path below them. The host also adds implicit routing at the very start, which
// matched the full path; forget that match.
app.Use((context, next) =>
{
    context.SetEndpoint(null);
    context.Request.RouteValues.Clear();
    return next(context);
});
app.UseRouting();
app.UseRateLimiter();

// Create the schema and load the master tables before the first request.
GameDatabase database = app.Services.GetRequiredService<GameDatabase>();
database.OpenSession().Dispose();
app.Logger.LogInformation("KnS server listening on {Address}:{Port}{Base} (db={Database}, web={Web})", serverOptions.BindAddress, serverOptions.Port,
    serverOptions.PublicBase, database.Description, serverOptions.WebRoot ?? "none");

// The browser build talks to this server from file:// (Origin: null) or a local web server.
app.Use(async (context, next) =>
{
    string origin = context.Request.Headers.Origin.ToString();
    if (origin == "null" || origin.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)
        || origin.StartsWith("http://localhost:", StringComparison.Ordinal))
    {
        context.Response.Headers.AccessControlAllowOrigin = origin;
        context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
        context.Response.Headers.AccessControlAllowHeaders = "*";
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
    }
    await next();
});

app.Map("/chat", ChatEndpoint.HandleAsync);

app.MapMethods("/{**path}", [HttpMethods.Get, HttpMethods.Post],
    (HttpContext context, RequestRouter router) => router.RouteAsync(context));

app.Run();

/// <summary>The entry point; public so integration tests can host the app (WebApplicationFactory).</summary>
public partial class Program;
