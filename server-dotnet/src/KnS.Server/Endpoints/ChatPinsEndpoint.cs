using System.Text.Json.Serialization;
using KnS.Data.Repositories;

namespace KnS.Server.Endpoints;

/// <summary>
/// GET or POST /api/pins: {"now": unix, "pins": [[id, start_time, end_time, "text"], ...]}, the notices pinned at the top of
/// chat right now, newest first. The client holds one pinned slot and fills it only from a type-10 position-2 chat message,
/// which also adds a chat line, so the browser polls this and writes the newest pin into the slot itself
/// (browser/port/native_hooks.js), letting Studio edits and removals show without a new chat line.
/// Public: it reveals only text every player already sees. Served under the API base like every other route.
/// </summary>
public static class ChatPinsEndpoint
{
    public const string Path = "/api/pins";

    public static bool Matches(string target) =>
        string.Equals(target.Split('?', 2)[0].TrimEnd('/'), Path, StringComparison.OrdinalIgnoreCase);

    public static ChatPinsReply Read(IReadOnlyList<ChatPin> pins, long now) =>
        new(now, [.. pins.Select(p => new object[] { p.Id, p.StartTime, p.EndTime, p.Message })]);
}

public sealed record ChatPinsReply(
    [property: JsonPropertyName("now")] long Now,
    [property: JsonPropertyName("pins")] IReadOnlyList<object[]> Pins);
