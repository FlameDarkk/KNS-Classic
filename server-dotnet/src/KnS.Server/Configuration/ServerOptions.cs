namespace KnS.Server.Configuration;

/// <summary>Host-level switches.</summary>
public sealed record ServerOptions
{
    public int Port { get; init; } = 8080;

    /// <summary>
    /// The public URL path everything is served under (KNS_PUBLIC_BASE), "/" or "/a/b/". Behind a shared site such as
    /// /games/kns/play/; the reverse proxy forwards the path unchanged.
    /// </summary>
    public string PublicBase { get; init; } = "/";

    /// <summary>
    /// The packaged browser build (KNS_WEB_ROOT): game page, Studio and art, served at the base, with the API under
    /// &lt;base&gt;api/. Unset (local development): no files, the API at the base.
    /// </summary>
    public string? WebRoot { get; init; }

    public static string NormalizeBase(string? value)
    {
        string trimmed = (value ?? "").Trim().Trim('/');
        return trimmed.Length == 0 ? "/" : "/" + trimmed + "/";
    }

    /// <summary>
    /// No work/server.log and no work/requests.jsonl (KNS_QUIET), for hosted servers: they record
    /// every request and would grow without limit.
    /// </summary>
    public bool Quiet { get; init; }

    /// <summary>The folder of server.log, requests.jsonl and traffic/ (KNS_LOG_DIR).</summary>
    public string LogDirectory { get; init; } = "work";

    /// <summary>The address to listen on (KNS_BIND).</summary>
    public string BindAddress { get; init; } = "0.0.0.0";

    /// <summary>
    /// Writes traffic/&lt;day&gt;.jsonl: every request with its reply, the game gateway's decrypted
    /// (see <see cref="Diagnostics.TrafficLog"/>). On unless quiet; KNS_TRAFFIC_LOG turns it on
    /// on a quiet (hosted) server too, where the retention bounds its size.
    /// </summary>
    public bool TrafficLog { get; init; } = true;

    /// <summary>Days of traffic files kept, today included (KNS_TRAFFIC_DAYS, default 7).</summary>
    public int TrafficRetentionDays { get; init; } = 7;

    /// <summary>
    /// Routes the encrypted ActiveUser gateway.php to the plain ActiveUser reply instead
    /// (KNS_BREAK_AU; set on Azure).
    /// </summary>
    public bool BreakActiveUser { get; init; }

    /// <summary>
    /// Extra latency on every game gateway reply (KNS_GW_DELAY, seconds). The server answers in
    /// microseconds where the original took ~100 ms, so the whole post-login burst lands at once:
    /// a timing the client never saw. This tells a protocol bug apart from a race.
    /// </summary>
    public TimeSpan GatewayDelay { get; init; }

    /// <summary>
    /// Stalls the billing check2 reply (KNS_BILLING_DELAY, seconds), to tell "the client exits
    /// because of the reply" from "the client exits regardless".
    /// </summary>
    public TimeSpan BillingDelay { get; init; }

    /// <summary>
    /// The secret of the /admin/testers endpoint (KNS_ADMIN_KEY, sent as the X-Admin-Key header).
    /// Unset = the endpoint does not exist. The client speaks plain http, so the key travels in
    /// the clear: use it from a trusted network or rotate it.
    /// </summary>
    public string? AdminKey { get; init; }

    /// <summary>
    /// Comma-separated reverse-proxy IPs or CIDRs trusted for X-Forwarded-For / X-Forwarded-Proto
    /// (KNS_TRUSTED_PROXIES, e.g. "172.16.0.0/12,10.0.0.0/8"). Empty keeps the loopback-only default.
    /// </summary>
    public IReadOnlyList<string> TrustedProxies { get; init; } = [];
}
