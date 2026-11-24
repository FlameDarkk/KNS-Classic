namespace KnS.Data.Repositories;

/// <summary>A player's login reward progress (login_reward). Days are UTC day numbers (unix seconds / 86400).</summary>
public sealed record LoginRewardState
{
    /// <summary>The last day protocol 67 paid this player; 0 = never.</summary>
    public long LastDay { get; init; }

    /// <summary>The month (yyyymm) the calendar count belongs to.</summary>
    public long CalendarMonth { get; init; }

    /// <summary>Calendar days paid this month (0..28).</summary>
    public long CalendarDays { get; init; }

    /// <summary>Days of the current 7-day gift paid (1..7); meaningful while <see cref="GiftActive"/>.</summary>
    public long GiftDay { get; init; }

    public bool GiftActive { get; init; }

    /// <summary>The one-time new-account gift has started.</summary>
    public bool GiftUsed { get; init; }
}

public sealed record LoginReward(long ItemId, long Value);

/// <summary>Login reward progress and the one-time tutorial reward (login_reward).</summary>
public sealed class LoginRewardRepository(DataSession session)
{
    public LoginRewardState Get(long userNumber) =>
        session.QueryFirstOrDefault<LoginRewardState>(
            "SELECT last_day AS LastDay, calendar_month AS CalendarMonth, calendar_days AS CalendarDays, gift_day AS GiftDay,"
            + " gift_active AS GiftActive, gift_used AS GiftUsed FROM login_reward WHERE user_no=@userNumber",
            new { userNumber }) ?? new LoginRewardState();

    public void Save(long userNumber, LoginRewardState state)
    {
        Ensure(userNumber);
        session.Execute(
            "UPDATE login_reward SET last_day=@LastDay, calendar_month=@CalendarMonth, calendar_days=@CalendarDays, gift_day=@GiftDay,"
            + " gift_active=@Active, gift_used=@Used WHERE user_no=@userNumber",
            new
            {
                state.LastDay, state.CalendarMonth, state.CalendarDays, state.GiftDay,
                Active = state.GiftActive ? 1 : 0, Used = state.GiftUsed ? 1 : 0, userNumber,
            });
    }

    /// <summary>Marks the tutorial reward as given; false when it already was.</summary>
    public bool ClaimTutorial(long userNumber)
    {
        Ensure(userNumber);
        return session.Execute(
            "UPDATE login_reward SET tutorial_claimed=1 WHERE user_no=@userNumber AND tutorial_claimed=0", new { userNumber }) == 1;
    }

    /// <summary>The player's current tutorial quest (tutorial_progress); null = no row yet (a new player).</summary>
    public long? TutorialQuest(long userNumber) =>
        session.QueryFirstOrDefault<long?>("SELECT quest_id FROM tutorial_progress WHERE user_no=@userNumber", new { userNumber });

    public void SetTutorialQuest(long userNumber, long questId)
    {
        session.Execute($"{session.Dialect.InsertOrIgnore} INTO tutorial_progress(user_no, quest_id) VALUES(@userNumber, @questId)",
            new { userNumber, questId });
        session.Execute("UPDATE tutorial_progress SET quest_id=@questId WHERE user_no=@userNumber", new { userNumber, questId });
    }

    /// <summary>Records a one-time tutorial quest reward claim; false if this quest was already rewarded.</summary>
    public bool ClaimTutorialQuestReward(long userNumber, long questId) =>
        session.Execute(
            $"{session.Dialect.InsertOrIgnore} INTO tutorial_quest_claim(user_no, quest_id) VALUES(@userNumber, @questId)",
            new { userNumber, questId }) == 1;

    /// <summary>One row of master_login_reward: kind 1 = 7-day gift, 2 = monthly calendar.</summary>
    public LoginReward? Reward(long kind, long day) =>
        session.QueryFirstOrDefault<LoginReward>(
            "SELECT item_id AS ItemId, value AS Value FROM master_login_reward WHERE kind=@kind AND day=@day", new { kind, day });

    private void Ensure(long userNumber) => session.Execute(
        $"{session.Dialect.InsertOrIgnore} INTO login_reward(user_no, tutorial_claimed, last_day, calendar_month, calendar_days,"
        + " gift_day, gift_active, gift_used) VALUES(@userNumber, 0, 0, 0, 0, 0, 0, 0)",
        new { userNumber });
}
