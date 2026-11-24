using KnS.Data.Repositories;
using KnS.Game.Models;
using KnS.Game.Randomness;
using KnS.Game.Rules;

namespace KnS.Game.Fellows;

/// <summary>Fellows ("allies"): their views, draws, and exp from dungeons.</summary>
public sealed class FellowService(
    FellowRepository fellows, FellowItemRepository fellowItems, MasterDataRepository masterData, FellowMissionRepository missionData, IRandomSource random)
{
    /// <summary>The player's fellows, oldest first; only <paramref name="fellowNumbers"/> when given.</summary>
    public IReadOnlyList<FellowInfo> GetFellowInfos(long userNumber, IReadOnlySet<long>? fellowNumbers = null)
    {
        ILookup<long, FellowItemRecord> worn = fellowItems.GetOwned(userNumber).ToLookup(item => item.FellowNumber);
        return fellows.GetOwned(userNumber)
            .Where(fellow => fellowNumbers is null || fellowNumbers.Contains(fellow.FellowNumber))
            .Select(fellow => ToFellowInfo(fellow, worn[fellow.FellowNumber]))
            .ToList();
    }

    /// <summary>The collection book, by fellow id; only <paramref name="fellowIds"/> when given.</summary>
    public IReadOnlyList<FellowBookEntry> GetBook(long userNumber, IReadOnlySet<long>? fellowIds = null) =>
        fellows.GetBook(userNumber)
            .Where(entry => fellowIds is null || fellowIds.Contains(entry.FellowId))
            .Select(entry => new FellowBookEntry(entry.FellowId, entry.RewardReceived, entry.Experience, entry.Level))
            .ToList();

    /// <summary>The filled slots; position 3 only for a client that opted in to the fourth one.</summary>
    public IReadOnlyList<ActiveFellowSlot> GetActiveSlots(long userNumber, bool fourthSlot = false) =>
        fellows.GetFilledSlots(userNumber)
            .Where(slot => fourthSlot || slot.Position < FellowRules.SlotsPerSetup)
            .Select(slot => new ActiveFellowSlot(slot.SetupType, slot.Position, slot.FellowNumber))
            .ToList();

    /// <summary>The three visible profile slots, using the party edited on the original Ally screen.</summary>
    public IReadOnlyList<FellowInfo> GetProfileParty(long userNumber)
    {
        Dictionary<long, long> slots = fellows.GetFilledSlots(userNumber)
            .Where(slot => slot.SetupType == FellowRules.DungeonSetupType && slot.Position < FellowRules.SlotsPerSetup)
            .ToDictionary(slot => slot.Position, slot => slot.FellowNumber);
        Dictionary<long, FellowInfo> owned = GetFellowInfos(userNumber, slots.Values.ToHashSet())
            .ToDictionary(fellow => fellow.FellowNumber);
        return Enumerable.Range(0, FellowRules.SlotsPerSetup)
            .Select(position => slots.TryGetValue(position, out long number) && owned.TryGetValue(number, out FellowInfo? fellow)
                ? fellow : new FellowInfo())
            .ToList();
    }

    /// <summary>Gives the player a fellow (and its collection record). Returns the fellow number.</summary>
    public long Grant(long userNumber, long fellowId)
    {
        long fellowNumber = fellows.Add(userNumber, fellowId, FellowRules.StartLevel);
        fellows.RecordInBook(userNumber, fellowId);
        return fellowNumber;
    }

    /// <summary>A uniform drawable fellow of one grade (the new ally of an enhance); null when the grade has none.</summary>
    public long? PickDrawableOfGrade(long grade)
    {
        List<long> ofGrade = masterData.GetDrawableFellows().Where(fellow => fellow.Grade == grade).Select(fellow => fellow.FellowId).ToList();
        return ofGrade.Count == 0 ? null : random.Pick(ofGrade);
    }

    /// <summary>A drawable fellow: the grade by <see cref="FellowRules.DrawGradeWeights"/>, then uniform within it.</summary>
    public long RollDrawnFellow(long? fixedGrade = null, bool premium = false, bool friendship = false)
    {
        Dictionary<long, List<long>> byGrade = masterData.GetDrawableFellows()
            .GroupBy(fellow => fellow.Grade)
            .ToDictionary(group => group.Key, group => group.Select(fellow => fellow.FellowId).ToList());

        if (fixedGrade is { } wanted && byGrade.TryGetValue(wanted, out List<long>? fellowsOfGrade))
        {
            return random.Pick(fellowsOfGrade);
        }

        var grades = FellowRules.DrawGradeWeights.Where(entry => byGrade.ContainsKey(entry.Grade) && (!premium || entry.Grade is >= 3 and <= 5) && (!friendship || entry.Grade is >= 1 and <= 3)).ToList();
        long grade = random.PickWeighted(grades.Select(entry => entry.Grade).ToList(), grades.Select(entry => entry.Weight).ToList());
        return random.Pick(byGrade[grade]);
    }

    /// <summary>Gives these allies exp (the bar fills like the hero's; the level cap stops it). Used by ally missions.</summary>
    public void AwardExperience(long userNumber, IEnumerable<long> fellowNumbers, long gain)
    {
        foreach (long number in fellowNumbers.Distinct())
        {
            if (fellows.FindProgress(userNumber, number) is not { } fellow)
            {
                continue;
            }

            Dictionary<long, long> experienceToNext = masterData.GetFellowExperienceTable(fellow.Grade);
            long level = fellow.Level;
            long experience = fellow.Experience + gain;
            while (level < fellow.MaxLevel && experienceToNext.TryGetValue(level, out long needed) && experience >= needed)
            {
                experience -= needed;
                level++;
            }

            fellows.SetProgress(fellow.FellowNumber, level, level >= fellow.MaxLevel ? 0 : experience);
        }
    }

    /// <summary>
    /// Gives the dungeon setup's fellows exp and returns the stage result's fellow_info: always
    /// three elements in slot order (four when the client opted in to the fourth slot), <c>{}</c> for an empty slot (the client applies those with a
    /// fellow number). Exp counts within the current level like the hero's;
    /// master_fellow_level fills the bar and master_fellow.max_level caps it.
    /// </summary>
    public IReadOnlyList<object> AwardDungeonExperience(long userNumber, long gain, bool fourthSlot = false)
    {
        Dictionary<long, long> fellowInSlot = fellows.GetFilledSlots(userNumber)
            .Where(slot => slot.SetupType == FellowRules.DungeonSetupType)
            .ToDictionary(slot => slot.Position, slot => slot.FellowNumber);

        var awarded = new List<long?>();
        for (long position = 0; position < FellowRules.SlotsFor(FellowRules.DungeonSetupType, fourthSlot); position++)
        {
            FellowProgressRecord? fellow = fellows.FindProgress(userNumber, fellowInSlot.GetValueOrDefault(position));
            if (fellow is null)
            {
                awarded.Add(null);
                continue;
            }

            Dictionary<long, long> experienceToNext = masterData.GetFellowExperienceTable(fellow.Grade);
            long level = fellow.Level;
            long experience = fellow.Experience + gain;
            while (level < fellow.MaxLevel && experienceToNext.TryGetValue(level, out long needed) && experience >= needed)
            {
                experience -= needed;
                level++;
            }

            if (level >= fellow.MaxLevel)
            {
                experience = 0;
            }

            fellows.SetProgress(fellow.FellowNumber, level, experience);
            awarded.Add(fellow.FellowNumber);
        }

        var infos = GetFellowInfos(userNumber, awarded.OfType<long>().ToHashSet())
            .ToDictionary(info => info.FellowNumber);
        return awarded
            .Select(number => number is { } n ? (object)infos[n] : EmptyObject.Instance)
            .ToList();
    }

    private FellowInfo ToFellowInfo(FellowRecord fellow, IEnumerable<FellowItemRecord> worn) => new()
    {
        FellowNumber = fellow.FellowNumber,
        EquippedWeapon = worn.FirstOrDefault(item => item.EquipIndex == FellowRules.WeaponEquipIndex)?.FellowItemNumber ?? 0,
        EquippedAccessory1 = worn.FirstOrDefault(item => item.EquipIndex == FellowRules.Accessory1EquipIndex)?.FellowItemNumber ?? 0,
        EquippedAccessory2 = worn.FirstOrDefault(item => item.EquipIndex == FellowRules.Accessory2EquipIndex)?.FellowItemNumber ?? 0,
        FellowId = fellow.FellowId,
        Upgrade = fellow.Upgrade,
        Experience = fellow.Experience,
        Level = fellow.Level,
        MaxLevel = fellow.MaxLevel,
        FailCount = fellow.FailCount,
        AttackBonus = fellow.AttackBonus,
        Locked = fellow.Locked,
        AwakenLevel = fellow.Awaken,
        MissionSkill1 = GetTalents(fellow)[0],
        MissionSkill2 = GetTalents(fellow)[1],
        NextAttributeReset = new AttributeResetInfo(FellowRules.TalentResetGems),
    };

    /// <summary>The ally's two talents (fellow_mission_skill01/02): stored ones, else its first two master mission skills at level 1.</summary>
    public IReadOnlyList<long> GetTalents(FellowRecord fellow)
    {
        IReadOnlyList<long> master = missionData.GetSkills(fellow.FellowId);
        return [FellowRules.Talent(fellow.Talent1, master.ElementAtOrDefault(0)), FellowRules.Talent(fellow.Talent2, master.ElementAtOrDefault(1))];
    }
}
