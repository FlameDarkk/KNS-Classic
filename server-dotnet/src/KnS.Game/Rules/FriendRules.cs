namespace KnS.Game.Rules;

/// <summary>Friend limits. The cool time is a guess: the game data has no value for it.</summary>
public static class FriendRules
{
    public const long SendCooldownSeconds = 24 * 3600;

    /// <summary>How long a sent request stays greyed in Add Friends (a guess).</summary>
    public const long RequestCooldownSeconds = 24 * 3600;

    /// <summary>Friendship points a player earns for each friend they send points to (dt_etc_setting Social_Reward.SendReward).</summary>
    public const long SendReward = 10;

    /// <summary>Friendship points mailed to a friend who receives a Send (restoration choice: the same amount the sender earns).</summary>
    public const long ReceiveReward = 10;

    /// <summary>Friendship points mailed to a friend whose hero helped in a dungeon, at most once per day per helper and player (restoration choice).</summary>
    public const long HelperReward = 3;

    public const long HelpCooldownSeconds = 24 * 3600;

    /// <summary>reward_mail type of the Message Box "FP" tab.</summary>
    public const long FriendPointMailType = 1;
}
