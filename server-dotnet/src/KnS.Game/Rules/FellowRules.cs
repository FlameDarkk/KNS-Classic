using KnS.Protocol;

namespace KnS.Game.Rules;

public static class FellowRules
{
    public const long StartLevel = 1;

    /// <summary>equip_index of protocol 333: the three gear slots on an ally.</summary>
    public const long WeaponEquipIndex = 1, Accessory1EquipIndex = 2, Accessory2EquipIndex = 3;

    /// <summary>Ally gear ids are category * 100000: 12xxxxx weapons, 13xxxxx accessories.</summary>
    public const long WeaponCategory = 12, AccessoryCategory = 13;

    /// <summary>Whether this gear goes in this slot: weapons in the first, accessories in the other two.</summary>
    public static bool FitsEquipIndex(long fellowItemId, long equipIndex) => (fellowItemId / 100_000, equipIndex) switch
    {
        (WeaponCategory, WeaponEquipIndex) => true,
        (AccessoryCategory, Accessory1EquipIndex or Accessory2EquipIndex) => true,
        _ => false,
    };

    /// <summary>Positions 0..2 per setup.</summary>
    public const int SlotsPerSetup = 3;

    /// <summary>The fellow setup dungeons use (the Ally screen edits this one).</summary>
    public const long DungeonSetupType = 3;

    /// <summary>
    /// The dungeon setup's slots for a client that asks for them (the rebuild). The original client keeps
    /// <see cref="SlotsPerSetup"/>: its three setups sit back to back in memory, so it must never be sent or
    /// handed a fourth position (docs/rebuild-fellows.md "Fourth slot").
    /// </summary>
    public const int DungeonPartySlots = 4;

    /// <summary>The request parameter only the rebuild sends: <c>party_slots</c> = <see cref="DungeonPartySlots"/>.</summary>
    public const string PartySlotsParameter = "party_slots";

    /// <summary>True when the fourth-slot feature flag is on and the request opts in to the fourth dungeon slot.</summary>
    public static bool WantsFourthSlot(RequestParameters parameters, bool featureEnabled) =>
        featureEnabled && parameters.GetInt64OrFallback(PartySlotsParameter) >= DungeonPartySlots;

    /// <summary>The slots of a setup for a client that did (or did not) opt in to the fourth one.</summary>
    public static int SlotsFor(long setupType, bool fourthSlot) =>
        fourthSlot && setupType == DungeonSetupType ? DungeonPartySlots : SlotsPerSetup;

    /// <summary>The highest fellow_upgrade ("+5").</summary>
    public const long MaxUpgrade = 5;

    /// <summary>The highest grade (<c>FellowMaxGrade</c>): nothing enhances past it.</summary>
    public const long MaxGrade = 6;

    /// <summary>Cap of the attack bonus the power-ups and enhances accumulate (<c>FellowAtkMax</c>).</summary>
    public const long AttackBonusCap = 99;

    /// <summary>The angel allies (110001-110006, master row +0x198, the evolution materials): they cannot take a slot and are neither powered up nor enhanced.</summary>
    public static bool IsAngel(long fellowId) => fellowId is >= 110001 and <= 110006;

    /// <summary>
    /// The success chance (percent) of a power-up (DrawFellowUpgrade @0xa8158): 100 for a material of a higher grade or a target already at
    /// the top upgrade, else the gap's row of master_fellow_power_up_rate plus 5 per 10 Power-up Points, at most 100.
    /// </summary>
    public static long PowerUpPercent(long targetGrade, long materialGrade, long targetUpgrade, long targetFailCount, long baseSuccessPercent) =>
        materialGrade > targetGrade || targetUpgrade >= MaxUpgrade
            ? 100
            : Math.Min(100, baseSuccessPercent + targetFailCount / 10 * 5);

    /// <summary>Evolution skill 2550101 (option family 25501, "Increases MAX Level by ^o1", dt_itemoption_pkg_140 value 5).</summary>
    /// <summary>Gems for collecting an ally's max-level reward (protocol 308). The popup shows 10 for an angel; other grades are unconfirmed.</summary>
    public const long MaxLevelRewardGems = 10;

    public const long MaxLevelEvolutionSkill = 2550101;

    public const long MaxLevelPerEvolutionSkill = 5;

    /// <summary>dt_etc_setting.json AttributeInit: changing a talent costs 10 gems (AttributeResetValue).</summary>
    public const long TalentResetGems = 10;

    /// <summary>
    /// A talent is a mission skill family id (id / 10, the name in text table 23) plus its level
    /// (id % 10, GetFellowAttributeValue@0x56108); the mission value table scores levels 1-8.
    /// The level odds of a new talent are not in the client; these weights are a restoration choice.
    /// </summary>
    public static readonly IReadOnlyList<(long Level, long Weight)> TalentLevelWeights =
        [(1, 30), (2, 25), (3, 18), (4, 12), (5, 7), (6, 4), (7, 3), (8, 1)];

    /// <summary>A stored talent, or the ally's master skill at level 1 when none was stored (0).</summary>
    public static long Talent(long stored, long masterSkill) =>
        stored != 0 ? stored : masterSkill > 0 ? masterSkill / 10 * 10 + 1 : masterSkill;

    /// <summary>
    /// Draw odds: the relative weight of each grade (1-6 stars), then a uniform pick inside it.
    /// Tuning, not original data; the real odds lived in the original server.
    /// </summary>
    public static readonly IReadOnlyList<(long Grade, long Weight)> DrawGradeWeights =
        [(1, 30), (2, 25), (3, 20), (4, 13), (5, 8), (6, 4)];
}
