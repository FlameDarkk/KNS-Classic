namespace KnS.Game.Rewards;

/// <summary>
/// Message Box titles (mail <c>text_code</c>). The client reads them from text table 14 (DrawMessageUI@0xc9600 row title,
/// DrawPopupReward@0xc8da0 heading); a code missing there draws a blank row title and the fallback heading "Event Reward"
/// (table 15, 9991006). Codes below are the native table 14 entries.
/// </summary>
public static class MailTexts
{
    public const long RaidReward = 9800011;
    public const long PvpReward = 9800021;
    public const long PvpMissionReward = 9800022;
    public const long TutorialReward = 9801001;
    private const long CalendarDayBase = 9800100;   // 9800101..9800128 "Day N Reward"
    private const long ReturningDayBase = 9800200;  // 9800201..9800207 "Returning User Event Day N Reward"

    public static long CalendarDay(long day) => day is >= 1 and <= 28 ? CalendarDayBase + day : 0;

    public static long ReturningDay(long day) => day is >= 1 and <= 7 ? ReturningDayBase + day : 0;

    /// <summary>Maps codes stored before 2026-11-11 (table 15 / raid text ids, not table 14) so existing mail gets its title.</summary>
    public static long ForClient(long stored) => stored switch
    {
        9991278 => PvpMissionReward,                    // was "Mission Complete" (table 15)
        9992912 => TutorialReward,                      // was a table 15 tutorial text
        >= 9993501 and <= 9993520 => RaidReward,        // raid rank tier text ids
        _ => stored,
    };
}
