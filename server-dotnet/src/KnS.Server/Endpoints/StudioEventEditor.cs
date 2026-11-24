using System.Text.Json;
using System.Text.Json.Nodes;
using KnS.Data;
using KnS.Data.Repositories;

namespace KnS.Server.Endpoints;

/// <summary>Edits the persisted schedule already used by EventStageService.</summary>
internal static class StudioEventEditor
{
    private const long Hour = 3600;

    public static JsonObject Snapshot(DataSession db, TimeProvider clock)
    {
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        bool allOpen = AllOpen(db);
        var rows = db.Query<StudioEventRow>("SELECT s.stage_id AS StageId,s.map AS Name,"
            + " COALESCE(e.start_time,0) AS StartTime,COALESCE(e.end_time,0) AS EndTime,"
            + " COALESCE(e.duration_seconds,0) AS DurationSeconds,COALESCE(e.period_seconds,0) AS PeriodSeconds"
            + " FROM master_stage s LEFT JOIN master_event_schedule e ON e.stage_id=s.stage_id"
            + " WHERE s.stage_id>=3200000 AND s.stage_id<3300000 AND s.stage_id%10=0 ORDER BY s.stage_id");
        return new JsonObject { ["events"] = JsonSerializer.SerializeToNode(rows
            .Where(e => !allOpen || !EventStageRepository.IsOpenAllDuplicate(e.StageId)).Select(e => new
        {
            e.StageId, e.Name, e.StartTime, e.EndTime, e.DurationSeconds, e.PeriodSeconds,
            Playable = !EventStageRepository.IsUnavailable(e.StageId),
            UnavailableReason = EventStageRepository.UnavailableReason(e.StageId),
            Open = !EventStageRepository.IsUnavailable(e.StageId) && new EventStageSchedule { StartTime=e.StartTime, EndTime=e.EndTime,
                DurationSeconds=e.DurationSeconds, PeriodSeconds=e.PeriodSeconds }.IsOpen(now),
        })), ["now"] = now, ["all_open"] = allOpen };
    }

    public static bool AllOpen(DataSession db) => EventStageRepository.IsAllOpen(db);

    /// <summary>Saves the regular calendar once, then opens every event stage for a year (whole hours). Pressing it again keeps the first save.</summary>
    private static void OpenAll(DataSession db, long now)
    {
        const string EventStages = "SELECT stage_id FROM master_stage WHERE stage_id>=3200000 AND stage_id<3300000 AND stage_id%10=0";
        if (!AllOpen(db))
            db.Execute("INSERT INTO event_schedule_backup(stage_id,start_time,end_time,duration_seconds,period_seconds)"
                + " SELECT s.stage_id,COALESCE(e.start_time,0),COALESCE(e.end_time,0),COALESCE(e.duration_seconds,0),COALESCE(e.period_seconds,0)"
                + " FROM master_stage s LEFT JOIN master_event_schedule e ON e.stage_id=s.stage_id"
                + " WHERE s.stage_id>=3200000 AND s.stage_id<3300000 AND s.stage_id%10=0");
        long start = now / Hour * Hour, span = 365 * 24 * Hour;
        db.Execute($"{db.Dialect.InsertOrIgnore} INTO master_event_schedule(stage_id,start_time,end_time,duration_seconds,period_seconds) SELECT stage_id,0,0,0,0 FROM ({EventStages}) x");
        db.Execute($"UPDATE master_event_schedule SET start_time=@start,end_time=@end,duration_seconds=@span,period_seconds=@span WHERE stage_id IN ({EventStages})",
            new { start, end = start + span, span });
        foreach (long stage in db.Query<long>(EventStages).Where(s => EventStageRepository.IsOpenAllDuplicate(s) || EventStageRepository.IsUnavailable(s)))
            db.Execute("UPDATE master_event_schedule SET end_time=start_time WHERE stage_id=@stage", new { stage });
    }

    private static void RestoreAll(DataSession db)
    {
        if (!AllOpen(db)) return;
        db.Execute("UPDATE master_event_schedule SET"
            + " start_time=(SELECT b.start_time FROM event_schedule_backup b WHERE b.stage_id=master_event_schedule.stage_id),"
            + " end_time=(SELECT b.end_time FROM event_schedule_backup b WHERE b.stage_id=master_event_schedule.stage_id),"
            + " duration_seconds=(SELECT b.duration_seconds FROM event_schedule_backup b WHERE b.stage_id=master_event_schedule.stage_id),"
            + " period_seconds=(SELECT b.period_seconds FROM event_schedule_backup b WHERE b.stage_id=master_event_schedule.stage_id)"
            + " WHERE stage_id IN (SELECT stage_id FROM event_schedule_backup)");
        db.Execute("DELETE FROM event_schedule_backup");
    }

    public static string? Apply(DataSession db, TimeProvider clock, string action, JsonObject body)
    {
        if (action == "event_open_all") { OpenAll(db, clock.GetUtcNow().ToUnixTimeSeconds()); return null; }
        if (action == "event_restore_all") { RestoreAll(db); return null; }
        if (!StudioInventoryEditor.Number(body, "stage_id", 3200000, 3299999, out long stage)
            || stage % 10 != 0 || !db.Exists("SELECT stage_id FROM master_stage WHERE stage_id=@stage", new { stage })) return "unknown event stage";
        if (action != "event_close" && EventStageRepository.IsUnavailable(stage))
            return "event dungeon unavailable: " + EventStageRepository.UnavailableReason(stage);
        long start, end, duration, period;
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        // The client's calendar (stage package row +356..+392) holds whole hours, so every window is kept on hour
        // boundaries: the game then lists exactly the windows the server admits (browser/port/native_hooks.js).
        long hourStart = now / Hour * Hour;
        if (action == "event_open")
        {
            if (!StudioInventoryEditor.Number(body, "seconds", 1, 604800, out long seconds)) return "event duration must be between 1 second and 7 days";
            start = hourStart; end = start + (now + seconds - start + Hour - 1) / Hour * Hour; duration = period = end - start;
        }
        else if (action == "event_close")
        {
            start = 0; end = hourStart; duration = Hour; period = Hour;
        }
        else if (action == "event_schedule")
        {
            if (!StudioInventoryEditor.Number(body, "start_time", 0, int.MaxValue-1, out start)
                || !StudioInventoryEditor.Number(body, "end_time", 1, int.MaxValue, out end) || end <= start
                || !StudioInventoryEditor.Number(body, "duration_seconds", 1, 604800, out duration)
                || !StudioInventoryEditor.Number(body, "period_seconds", duration, 31622400, out period)) return "invalid event dates, duration or repeat period";
            if (start % Hour != 0 || end % Hour != 0 || duration % Hour != 0 || period % Hour != 0)
                return "event start, end, duration and repeat period must be whole hours (the game calendar counts hours)";
        }
        else return "unknown event action";
        // Validate the complete request before creating or changing any schedule row.
        db.Execute($"{db.Dialect.InsertOrIgnore} INTO master_event_schedule(stage_id,start_time,end_time,duration_seconds,period_seconds) VALUES(@stage,0,0,0,0)", new { stage });
        db.Execute("UPDATE master_event_schedule SET start_time=@start,end_time=@end,duration_seconds=@duration,period_seconds=@period WHERE stage_id=@stage", new { start, end, duration, period, stage });
        return null;
    }

    private sealed record StudioEventRow
    {
        public long StageId { get; init; }
        public string Name { get; init; } = "";
        public long StartTime { get; init; }
        public long EndTime { get; init; }
        public long DurationSeconds { get; init; }
        public long PeriodSeconds { get; init; }
    }
}
