namespace KnS.Game.Rules;

/// <summary>Friend limits from dt_etc_setting.json.</summary>
public static class FriendRules
{
    public const long SendCooldownSeconds = 24 * 3600;

    /// <summary>How long a sent request stays greyed in Add Friends (a guess).</summary>
    public const long RequestCooldownSeconds = 24 * 3600;

    /// <summary>Friendship points a player earns for each friend they send points to (dt_etc_setting Social_Reward.SendReward).</summary>
    public const long SendReward = 10;

    /// <summary>Friendship points mailed to a friend who receives a Send (restoration choice: the same amount the sender earns).</summary>
    public const long ReceiveReward = 10;

    /// <summary>Friendship points mailed to a friend whose hero helped in a dungeon (dt_etc_setting Social_Reward.PalyGameWithFriend).</summary>
    public const long HelperReward = 3;

    /// <summary>dt_etc_setting.json Init_Value "FriendWithPlayResetHour": 8 hours.</summary>
    public const long HelpCooldownSeconds = 8 * 3600;

    /// <summary>dt_etc_setting.json Init_Value daily social caps.</summary>
    public const int DailyRequestLimit = 30;
    public const int DailyDeleteLimit = 5;
    public const int DailySendPointsLimit = 100;
    public const int DailyReceivePointsLimit = 100;

    /// <summary>reward_mail type of the Message Box "FP" tab.</summary>
    public const long FriendPointMailType = 1;
}
