using KnS.Data;

namespace KnS.Server.Endpoints;

/// <summary>
/// GET or POST /api/progress: {"progress_epoch": n}, the time (unix seconds) of the last full progress reset (0 = never).
/// The browser (account.js) and Android (KnsHub) drop their local game save once when it differs from the value they saw last,
/// so the native tutorial and local flags start fresh with the server progress. Public: it reveals only that timestamp.
/// Served under the API base like every other route.
/// </summary>
public static class ProgressEpochEndpoint
{
    public const string Path = "/api/progress";

    public static bool Matches(string target) =>
        string.Equals(target.Split('?', 2)[0].TrimEnd('/'), Path, StringComparison.OrdinalIgnoreCase);

    public static long Read(DataSession session) =>
        session.QueryFirstOrDefault<long?>("SELECT value FROM server_state WHERE state_key='progress_epoch'") ?? 0;
}

public sealed record ProgressEpochReply([property: System.Text.Json.Serialization.JsonPropertyName("progress_epoch")] long ProgressEpoch);
