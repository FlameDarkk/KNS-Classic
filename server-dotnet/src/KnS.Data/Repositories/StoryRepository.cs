namespace KnS.Data.Repositories;

public sealed class StoryRepository(DataSession session)
{
    private const string Columns =
        "story_id AS StoryId, previous_id AS PreviousId, experience AS Experience,"
        + " quest1_type AS Quest1Type, quest1_target AS Quest1Target, quest1_stage AS Quest1Stage, quest1_goal AS Quest1Goal,"
        + " quest2_type AS Quest2Type, quest2_target AS Quest2Target, quest2_stage AS Quest2Stage, quest2_goal AS Quest2Goal,"
        + " quest3_type AS Quest3Type, quest3_target AS Quest3Target, quest3_stage AS Quest3Stage, quest3_goal AS Quest3Goal,"
        + " quest4_type AS Quest4Type, quest4_target AS Quest4Target, quest4_stage AS Quest4Stage, quest4_goal AS Quest4Goal,"
        + " reward1_item AS Reward1Item, reward1_count AS Reward1Count, reward2_item AS Reward2Item, reward2_count AS Reward2Count,"
        + " reward3_item AS Reward3Item, reward3_count AS Reward3Count, reward4_item AS Reward4Item, reward4_count AS Reward4Count";

    /// <summary>Every story of master_story_quest (dt_story_pkg_131), by id.</summary>
    public IReadOnlyList<StoryQuestRule> All() =>
        session.Query<StoryQuestRule>($"SELECT {Columns} FROM master_story_quest ORDER BY story_id");

    public StoryQuestRule Rule(long storyId) =>
        session.QueryFirstOrDefault<StoryQuestRule>($"SELECT {Columns} FROM master_story_quest WHERE story_id=@storyId", new { storyId })
        ?? throw new InvalidOperationException($"Missing story {storyId}");

    public bool IsCompleted(long userNumber, long storyId) => session.Exists(
        "SELECT 1 FROM story_completion WHERE user_no=@userNumber AND story_id=@storyId", new { userNumber, storyId });

    public HashSet<long> Completed(long userNumber) =>
        [.. session.Query<long>("SELECT story_id FROM story_completion WHERE user_no=@userNumber", new { userNumber })];

    public bool Complete(long userNumber, long storyId) => session.Execute(
        $"{session.Dialect.InsertOrIgnore} INTO story_completion(user_no, story_id) VALUES(@userNumber, @storyId)",
        new { userNumber, storyId }) == 1;

    /// <summary>Progress of objective <paramref name="quest"/> (1-4) of a story.</summary>
    public long Count(long userNumber, long storyId, int quest = 1) => session.QueryFirstOrDefault<long?>(
        $"SELECT {QuestColumn(quest)} FROM story_progress WHERE user_no=@userNumber AND story_id=@storyId", new { userNumber, storyId }) ?? 0;

    public void Add(long userNumber, long storyId, long amount, int quest = 1)
    {
        session.Execute($"{session.Dialect.InsertOrIgnore} INTO story_progress(user_no, story_id) VALUES(@userNumber, @storyId)",
            new { userNumber, storyId });
        string column = QuestColumn(quest);
        session.Execute($"UPDATE story_progress SET {column}={column}+@amount WHERE user_no=@userNumber AND story_id=@storyId",
            new { userNumber, storyId, amount });
    }

    private static string QuestColumn(int quest) => quest switch
    {
        1 => "quest_count",
        2 => "quest2_count",
        3 => "quest3_count",
        4 => "quest4_count",
        _ => throw new ArgumentOutOfRangeException(nameof(quest)),
    };
}

/// <summary>A story row: up to four objectives (type, target, stage, goal) and four rewards (item, count).</summary>
public sealed record StoryQuestRule
{
    public long StoryId { get; init; }
    public long PreviousId { get; init; }
    public long Experience { get; init; }
    public long Quest1Type { get; init; }
    public long Quest1Target { get; init; }
    public long Quest1Stage { get; init; }
    public long Quest1Goal { get; init; }
    public long Quest2Type { get; init; }
    public long Quest2Target { get; init; }
    public long Quest2Stage { get; init; }
    public long Quest2Goal { get; init; }
    public long Quest3Type { get; init; }
    public long Quest3Target { get; init; }
    public long Quest3Stage { get; init; }
    public long Quest3Goal { get; init; }
    public long Quest4Type { get; init; }
    public long Quest4Target { get; init; }
    public long Quest4Stage { get; init; }
    public long Quest4Goal { get; init; }
    public long Reward1Item { get; init; }
    public long Reward1Count { get; init; }
    public long Reward2Item { get; init; }
    public long Reward2Count { get; init; }
    public long Reward3Item { get; init; }
    public long Reward3Count { get; init; }
    public long Reward4Item { get; init; }
    public long Reward4Count { get; init; }

    /// <summary>The objectives in order (index 1-4); type 0 = none.</summary>
    public IReadOnlyList<StoryObjective> Objectives =>
    [
        new(1, Quest1Type, Quest1Target, Quest1Stage, Quest1Goal),
        new(2, Quest2Type, Quest2Target, Quest2Stage, Quest2Goal),
        new(3, Quest3Type, Quest3Target, Quest3Stage, Quest3Goal),
        new(4, Quest4Type, Quest4Target, Quest4Stage, Quest4Goal),
    ];

    public IReadOnlyList<(long Item, long Count)> Rewards =>
        new[] { (Reward1Item, Reward1Count), (Reward2Item, Reward2Count), (Reward3Item, Reward3Count), (Reward4Item, Reward4Count) }
            .Where(reward => reward.Item1 > 0 && reward.Item2 > 0).ToList();
}

/// <summary>Types: 1 defeat monster family Target, 3 clear Stage, 4 collect item Target, 5 own ally Target.</summary>
public sealed record StoryObjective(int Index, long Type, long Target, long Stage, long Goal);
