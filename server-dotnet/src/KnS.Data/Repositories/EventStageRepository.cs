namespace KnS.Data.Repositories;

/// <summary>Original browser event schedule anchors, exported as UTC timestamps from Korean dates.</summary>
public sealed record EventStageSchedule
{
    public long StageId { get; init; }
    public long StartTime { get; init; }
    public long EndTime { get; init; }
    public long DurationSeconds { get; init; }
    public long PeriodSeconds { get; init; }

    public bool IsOpen(long now) => now >= StartTime && now < EndTime && PeriodSeconds > 0
        && DurationSeconds > 0 && (now - StartTime) % PeriodSeconds < DurationSeconds;
}

public sealed class EventStageRepository(DataSession session)
{
    // These entry/boss maps are absent from the indexed payload and purchased 1.4.0 APK.
    // Keep the source rows and calendar so exact recovered assets can restore them later.
    public static bool HasMissingMaps(long stageId) => stageId is
        3200810 or 3200820 or 3200830 or 3200840 or
        3200910 or 3200920 or 3200930 or 3200940 or 3201000 or 3201010;

    // The old standalone Center Hell row advertises nonexistent armor 301091.
    // Its two complete five-difficulty calendars remain available.
    public static bool IsUnavailable(long stageId) => HasMissingMaps(stageId) || stageId == 3200110;

    public static string UnavailableReason(long stageId) => HasMissingMaps(stageId)
        ? "Entry and boss monster maps are missing from the original assets."
        : stageId == 3200110 ? "Original rewards reference nonexistent armor 301091." : "";

    // Restoration policy for Open all only. The indexed stage table contains paired calendars
    // with the same table-6 localized name and difficulty (row +424 bits 16..23). Keep the first
    // paired set and the five-difficulty versions instead of the older standalone Hell rows.
    // Keep their distinct normal-calendar windows and rewards intact for Restore regular schedule.
    public static bool IsOpenAllDuplicate(long stageId) => stageId % 10 == 0 && (stageId is
        >= 3200340 and <= 3200380 or >= 3200500 and <= 3200540 or
        >= 3200700 and <= 3200740 or >= 3200910 and <= 3200950 or
        3200110 or 3200210 or 3200800 or 3201010 or >= 3250400 and <= 3250420);

    public static bool IsAllOpen(DataSession db) => db.Exists("SELECT stage_id FROM event_schedule_backup LIMIT 1");

    // Also repairs the public calendar/admission of databases opened with the older Open all
    // implementation, without requiring another click or changing the saved regular calendar.
    public static EventStageSchedule ApplyOpenAllPolicy(EventStageSchedule row, bool allOpen) =>
        IsUnavailable(row.StageId) || (allOpen && IsOpenAllDuplicate(row.StageId))
            ? row with { EndTime = row.StartTime } : row;

    public void BeginPlay(long userNumber, long stageId) => session.Execute(
        $"{session.Dialect.InsertOrReplace} INTO event_stage_play(user_no,stage_id,complete) VALUES(@userNumber,@stageId,0)", new { userNumber, stageId });

    public long? CompletePlay(long userNumber, long reportedStage)
    {
        if (session.Execute("UPDATE event_stage_play SET complete=1 WHERE user_no=@userNumber AND complete=0"
            + " AND (stage_id=@reportedStage OR stage_id+1=@reportedStage)", new { userNumber, reportedStage }) != 1) return null;
        return session.QueryFirstOrDefault<long?>("SELECT stage_id FROM event_stage_play WHERE user_no=@userNumber", new { userNumber });
    }

    public EventStageSchedule? Get(long stageId)
    {
        EventStageSchedule? row = session.QueryFirstOrDefault<EventStageSchedule>(
        "SELECT stage_id AS StageId, start_time AS StartTime, end_time AS EndTime,"
        + " duration_seconds AS DurationSeconds, period_seconds AS PeriodSeconds"
        + " FROM master_event_schedule WHERE stage_id=@stageId", new { stageId });
        return row is null ? null : ApplyOpenAllPolicy(row, IsOpenAllDuplicate(stageId) && IsAllOpen(session));
    }
}
