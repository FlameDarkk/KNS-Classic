using System.Text.Json.Serialization;
using KnS.Data;
using KnS.Data.Repositories;

namespace KnS.Server.Endpoints;

/// <summary>
/// GET or POST /api/events: {"events": [[stage_id, start_time, end_time, duration_seconds, period_seconds], ...]}, the
/// persisted event windows (master_event_schedule) that EventStageService admits by. The original client lists event
/// dungeons from the dates in its own stage package (RefreshEventStagelist reads start/end/duration/period at row +356..+392),
/// so the browser writes these rows over that table (browser/port/native_hooks.js) and Studio changes reach the game.
/// Public: it reveals only the event calendar. Served under the API base like every other route.
/// </summary>
public static class EventScheduleEndpoint
{
    public const string Path = "/api/events";

    public static bool Matches(string target) =>
        string.Equals(target.Split('?', 2)[0].TrimEnd('/'), Path, StringComparison.OrdinalIgnoreCase);

    public static EventScheduleReply Read(DataSession session)
    {
        bool allOpen = EventStageRepository.IsAllOpen(session);
        return new([.. session.Query<EventStageSchedule>(
            "SELECT stage_id AS StageId,start_time AS StartTime,end_time AS EndTime,duration_seconds AS DurationSeconds,"
            + "period_seconds AS PeriodSeconds FROM master_event_schedule ORDER BY stage_id")
            .Select(r => EventStageRepository.ApplyOpenAllPolicy(r, allOpen))
            .Select(r => new[] { r.StageId, r.StartTime, r.EndTime, r.DurationSeconds, r.PeriodSeconds })]);
    }
}

public sealed record EventScheduleReply([property: JsonPropertyName("events")] IReadOnlyList<long[]> Events);
