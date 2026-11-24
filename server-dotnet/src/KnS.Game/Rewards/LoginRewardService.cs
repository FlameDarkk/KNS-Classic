using KnS.Data.Repositories;

namespace KnS.Game.Rewards;

/// <summary>What protocol 67 shows: reward_type 1 = 7-day gift, 2 = monthly calendar, 0 = nothing new; day = the row shown.</summary>
public sealed record LoginRewardResult(long RewardType, long Day);

/// <summary>
/// The login rewards of protocol 67, paid into the Message Box (the native reply carries no items).
/// Native data: master_login_reward from dt_reward_pkg tables 4 (7-day gift) and 2 (28-day calendar).
/// Restoration choices (no native rule recovered): a day starts at 00:00 UTC; both schedules count login days,
/// so a missed day pauses rather than restarts; the calendar restarts on the 1st of each month and stops after 28;
/// every account gets the 7-day gift once from its first login, afterwards only after <see cref="ReturningAfterDays"/>
/// days without a login; while the gift runs both schedules pay and the popup shows the gift.
/// </summary>
public sealed class LoginRewardService(LoginRewardRepository rewards, MailRepository mail, TimeProvider clock)
{
    public const long GiftKind = 1;
    public const long CalendarKind = 2;
    public const long GiftDays = 7;
    public const long CalendarDays = 28;
    public const long ReturningAfterDays = 30;

    public const long TutorialRewardItem = 1900012;
    public const long TutorialRewardText = MailTexts.TutorialReward;

    public LoginRewardResult Claim(long userNumber)
    {
        mail.LockUser(userNumber);
        DateTimeOffset now = clock.GetUtcNow();
        long today = now.ToUnixTimeSeconds() / 86400;
        long month = now.Year * 100 + now.Month;
        LoginRewardState state = rewards.Get(userNumber);
        if (state.LastDay >= today)
        {
            return new LoginRewardResult(0, 0);
        }

        bool startGift = !state.GiftUsed || (!state.GiftActive && state.LastDay > 0 && today - state.LastDay >= ReturningAfterDays);
        long giftDay = startGift ? 0 : state.GiftDay;
        bool giftActive = startGift || state.GiftActive;
        long calendarDays = state.CalendarMonth == month ? state.CalendarDays : 0;
        long regDate = now.ToUnixTimeSeconds();

        LoginRewardResult shown = new(0, 0);
        if (giftActive && giftDay < GiftDays)
        {
            giftDay++;
            Pay(userNumber, GiftKind, giftDay, $"Returning gift: day {giftDay}", regDate);
            shown = new LoginRewardResult(GiftKind, giftDay);
            giftActive = giftDay < GiftDays;
        }

        if (calendarDays < CalendarDays)
        {
            calendarDays++;
            Pay(userNumber, CalendarKind, calendarDays, $"Monthly login reward: day {calendarDays}", regDate);
            if (shown.RewardType == 0) shown = new LoginRewardResult(CalendarKind, calendarDays);
        }

        rewards.Save(userNumber, new LoginRewardState
        {
            LastDay = today, CalendarMonth = month, CalendarDays = calendarDays,
            GiftDay = giftDay, GiftActive = giftActive, GiftUsed = true,
        });
        return shown;
    }

    /// <summary>
    /// The main tutorial chain, in the order of the native tutorial table (GetTutorialInfoByID; all send 602/603 when
    /// finished). 601 reports the current one; NetworkEvent@0x153940 starts it for 5000001..5000009 and, for an empty
    /// quest list, marks 5000001-5000005 and 5000010-5000013 finished (CheckTutorial then never starts them).
    /// </summary>
    public static readonly long[] TutorialChain = [5000001, 5000002, 5000003, 5000004, 5000005];

    /// <summary>The tutorial quest 601 reports; 0 once the chain is finished (the client then skips every tutorial).</summary>
    public long CurrentTutorialQuest(long userNumber) => rewards.TutorialQuest(userNumber) ?? TutorialChain[0];

    /// <summary>
    /// 602/603 report the tutorial just finished (quest_id = gameInfo+0x338c). Moves the account to the next quest, never
    /// back; the last one completes the tutorial for good and mails the reward. True when the tutorial was just completed.
    /// </summary>
    public bool FinishTutorialQuest(long userNumber, long questId)
    {
        long current = CurrentTutorialQuest(userNumber);
        int done = Array.IndexOf(TutorialChain, questId), at = Array.IndexOf(TutorialChain, current);
        if (current == 0 || done < 0 || done < at) return false;
        long next = done + 1 < TutorialChain.Length ? TutorialChain[done + 1] : 0;
        rewards.SetTutorialQuest(userNumber, next);
        return next == 0 && ClaimTutorial(userNumber);
    }

    /// <summary>Mails the one-time tutorial reward (an Ally Ticket); false when it was already given.</summary>
    public bool ClaimTutorial(long userNumber)
    {
        mail.LockUser(userNumber);
        if (!rewards.ClaimTutorial(userNumber)) return false;
        mail.Add(userNumber, TutorialRewardItem, 1, message: "Tutorial Reward", textCode: TutorialRewardText,
            regDate: clock.GetUtcNow().ToUnixTimeSeconds());
        return true;
    }

    private void Pay(long userNumber, long kind, long day, string message, long regDate)
    {
        LoginReward reward = rewards.Reward(kind, day)
            ?? throw new InvalidOperationException($"Missing master_login_reward {kind}/{day}");
        long text = kind == GiftKind ? MailTexts.ReturningDay(day) : MailTexts.CalendarDay(day);
        mail.Add(userNumber, reward.ItemId, reward.Value, message: message, textCode: text, regDate: regDate);
    }
}
