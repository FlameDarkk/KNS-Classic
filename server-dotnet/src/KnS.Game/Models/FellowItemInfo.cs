using System.Text.Json.Serialization;

namespace KnS.Game.Models;

/// <summary>An owned ally weapon or accessory (ParsingDataToFellowItem@0x14bd80 + ParsingDataToFellowEquipItem@0x14bc24).</summary>
public sealed record FellowItemInfo
{
    [JsonPropertyName("fellow_item_no")] public long FellowItemNumber { get; init; }
    [JsonPropertyName("fellow_item_id")] public long FellowItemId { get; init; }
    [JsonPropertyName("fellow_item_upgrade")] public long Upgrade { get; init; }
    [JsonPropertyName("fellow_item_attack")] public long Attack { get; init; }
    [JsonPropertyName("fellow_item_skill_attack")] public long SkillAttack { get; init; }
    [JsonPropertyName("fellow_item_critical")] public long Critical { get; init; }
    [JsonPropertyName("fellow_item_critical_damage")] public long CriticalDamage { get; init; }
    [JsonPropertyName("fellow_item_attack_speed")] public long AttackSpeed { get; init; }
    [JsonPropertyName("fellow_item_lock")] public long Locked { get; init; }

    /// <summary>The ally wearing it; 0 = in the bag.</summary>
    [JsonPropertyName("fellow_no")] public long FellowNumber { get; init; }
    [JsonPropertyName("fellow_item_upgrade_exp")] public long UpgradeExperience { get; init; }
    [JsonPropertyName("fellow_item_upgrade_need_exp")] public long UpgradeNeededExperience { get; init; }
    [JsonPropertyName("fellow_item_attribute")] public long Attribute { get; init; }
}
