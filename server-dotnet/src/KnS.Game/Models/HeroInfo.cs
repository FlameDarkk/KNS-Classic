using System.Text.Json.Serialization;

namespace KnS.Game.Models;

/// <summary>A hero_info element, parsed by ParsingDataToCharacterInfo@0x14cdd0.</summary>
public sealed record HeroInfo
{
    /// <summary>Profile-card protocol 64 reads the owner from hero_info, not the top-level user_no.</summary>
    [JsonPropertyName("user_no")] public long UserNumber { get; init; }
    [JsonPropertyName("hero_no")] public long HeroNumber { get; init; }
    [JsonPropertyName("hero_pcid")] public long HeroPcid { get; init; }
    [JsonPropertyName("hero_perfect_guard_bonus")] public long PerfectGuardBonus { get; init; }
    [JsonPropertyName("hero_booster_bonus")] public long BoosterBonus { get; init; }
    [JsonPropertyName("hero_item_bonus")] public long ItemBonus { get; init; }
    [JsonPropertyName("hero_gold_bonus")] public long GoldBonus { get; init; }
    [JsonPropertyName("hero_potion_bonus")] public long PotionBonus { get; init; }

    /// <summary>The equipped items (<see cref="StarterItemInfo"/> or <see cref="ItemInfo"/>).</summary>
    [JsonPropertyName("hero_item_infos")] public IReadOnlyList<object> EquippedItems { get; init; } = [];
}
