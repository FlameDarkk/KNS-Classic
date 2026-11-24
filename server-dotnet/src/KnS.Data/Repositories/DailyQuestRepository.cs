namespace KnS.Data.Repositories;

public sealed record DailyQuestProgress
{
    public long QuestId { get; init; }
    public long Count { get; init; }
    public long Complete { get; init; }
}

public sealed record DailyQuestRule
{
    public long QuestId { get; init; }
    public long Goal { get; init; }
    public long RewardItemId { get; init; }
    public long RewardCount { get; init; }
}

public sealed class DailyQuestRepository(DataSession session)
{
    public IReadOnlyList<DailyQuestRule> Rules() => session.Query<DailyQuestRule>(
        "SELECT quest_id AS QuestId, goal AS Goal, reward_item_id AS RewardItemId, reward_count AS RewardCount FROM master_daily_quest ORDER BY quest_id");

    public IReadOnlyList<DailyQuestProgress> Get(long userNumber, long day) => session.Query<DailyQuestProgress>(
        "SELECT quest_id AS QuestId, quest_count AS Count, complete AS Complete FROM daily_quest_progress WHERE user_no=@userNumber AND day_no=@day",
        new { userNumber, day });

    public void Add(long userNumber, long day, long questId, long amount)
    {
        session.Execute($"{session.Dialect.InsertOrIgnore} INTO daily_quest_progress(user_no, day_no, quest_id) VALUES(@userNumber, @day, @questId)",
            new { userNumber, day, questId });
        session.Execute("UPDATE daily_quest_progress SET quest_count=quest_count+@amount WHERE user_no=@userNumber AND day_no=@day AND quest_id=@questId AND complete=0",
            new { userNumber, day, questId, amount });
    }

    public bool Claim(long userNumber, long day, long questId, long goal) => session.Execute(
        "UPDATE daily_quest_progress SET complete=1 WHERE user_no=@userNumber AND day_no=@day AND quest_id=@questId AND complete=0 AND quest_count>=@goal",
        new { userNumber, day, questId, goal }) == 1;
}
