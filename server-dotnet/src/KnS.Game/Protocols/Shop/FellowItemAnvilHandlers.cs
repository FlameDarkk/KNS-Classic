using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Randomness;
using KnS.Game.Rules;
using KnS.Protocol.Json;
using Microsoft.Extensions.Logging;

namespace KnS.Game.Protocols.Shop;

/// <summary>Steel Anvil (Scene_PointerReleaseEvent_SC_POPUP_ANVIL@0x883a0) rules shared by craft, power-up and disassembly.</summary>
public static class AnvilRules
{
    /// <summary>Sub-materials Cast Iron Ingot ... Crystal of Cruelty.</summary>
    public const long FirstSubMaterial = 1902001;
    public const long LastSubMaterial = 1902008;

    /// <summary>dt_etc_setting SubMaterialEXP: exp per fed sub-material.</summary>
    public const long SubMaterialExp = 10;

    /// <summary>dt_etc_setting SubMaterialSellGold: gold per fed sub-material in the power-up Total Cost (DrawUpgradeItemInfo@0x869c0).</summary>
    public const long SubMaterialGold = 50;

    /// <summary>Fallback disassembly return % when a row has no c43/c44 range (restoration choice).</summary>
    public const long DefaultReturnPercent = 20;

    public static bool IsSubMaterial(long itemId) => itemId is >= FirstSubMaterial and <= LastSubMaterial;
}

/// <summary>
/// Protocol 335: Steel Anvil Power-up (request @0x14e47e, parser @0x160bbc, post-handler @0x157300):
/// {fellow_item_no, used_fellow_item_no_list:[uid], used_currency_list:[{id, count}]}. Fed gear gives int 8 + 10 % per its level,
/// each sub-material 10 exp; the target levels while exp reaches the need (int 6 grown by int 7 % per level) up to int 5.
/// Gold cost = each fed item's c40 + 50 per sub-material (the client's Total Cost). Reply: update_item_etc_info,
/// fellow_item_info (the target), used_fellow_item_no_list (the client deletes those).
/// </summary>
public sealed class FellowItemPowerUpHandler(
    PlayerDirectory directory,
    PlayerProfileService profiles,
    FellowItemService fellowItemService,
    FellowItemRepository fellowItems,
    MasterDataRepository masterData,
    ILogger<FellowItemPowerUpHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        long targetNumber = request.Parameters.GetInt64("fellow_item_no");
        long[] fed = request.Parameters.GetList("used_fellow_item_no_list").Select(LenientJson.ToInt64).Where(n => n > 0).ToArray();
        var subs = new Dictionary<long, long>();
        foreach (JsonNode? entry in request.Parameters.GetList("used_currency_list"))
        {
            long id = LenientJson.TryToInt64(entry?["id"]) ?? 0;
            long count = LenientJson.TryToInt64(entry?["count"]) ?? 0;
            if (count <= 0) continue;
            if (!AnvilRules.IsSubMaterial(id)) return Refuse("not a sub-material");
            subs[id] = subs.GetValueOrDefault(id) + count;
        }

        FellowItemRecord? target = fellowItems.Find(user, targetNumber);
        FellowItemRule? rule = target is null ? null : masterData.GetFellowItem(target.FellowItemId);
        if (target is null || rule is null) return Refuse("no such item");
        if (target.Upgrade >= FellowItemService.MaxUpgrade(rule)) return Refuse("already at max level");
        if (fed.Length == 0 && subs.Count == 0) return Refuse("nothing to feed");
        if (fed.Distinct().Count() != fed.Length || fed.Contains(targetNumber)) return Refuse("invalid feed list");

        long gain = subs.Values.Sum() * AnvilRules.SubMaterialExp;
        long gold = subs.Values.Sum() * AnvilRules.SubMaterialGold;
        foreach (long number in fed)
        {
            FellowItemRecord? item = fellowItems.Find(user, number);
            FellowItemRule? itemRule = item is null ? null : masterData.GetFellowItem(item.FellowItemId);
            if (item is null || itemRule is null || item.Locked != 0 || item.FellowNumber != 0) return Refuse("fed item unavailable");
            gain += FellowItemService.FeedExperience(itemRule, item.Upgrade);
            gold += itemRule.SellPrice;
        }

        if (profiles.GetBalance(user, CurrencyItems.Gold) < gold) return Refuse("not enough gold", 12004);
        if (subs.Any(sub => profiles.GetBalance(user, sub.Key) < sub.Value)) return Refuse("not enough materials", 12007);

        profiles.TrySpend(user, CurrencyItems.Gold, gold);
        foreach ((long id, long count) in subs) profiles.TrySpend(user, id, count);
        foreach (long number in fed) fellowItems.Delete(number);

        long upgrade = target.Upgrade;
        long experience = target.UpgradeExperience + gain;
        long max = FellowItemService.MaxUpgrade(rule);
        while (upgrade < max && experience >= FellowItemService.NeededExperience(rule, upgrade))
        {
            experience -= FellowItemService.NeededExperience(rule, upgrade);
            upgrade++;
        }
        if (upgrade >= max) experience = 0;
        fellowItems.SetUpgrade(targetNumber, upgrade, experience);

        logger.LogInformation("Ally item power-up: {Item} +{Before} -> +{After} ({Gain} exp, {Gold} G, fed [{Fed}], subs {Subs})",
            targetNumber, target.Upgrade, upgrade, gain, gold, string.Join(",", fed), subs.Values.Sum());
        return ProtocolResult.Success(new FellowItemPowerUpResponse
        {
            Goods = profiles.GetGoods(user),
            Item = fellowItemService.ToInfo(fellowItems.Find(user, targetNumber)!),
            UsedItems = fed,
        });
    }

    private ProtocolResult Refuse(string reason, int errno = 1)
    {
        logger.LogInformation("Ally item power-up refused: {Reason}", reason);
        return ProtocolResult.Refused(reason, errno);
    }
}

public sealed record FellowItemPowerUpResponse
{
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("fellow_item_info")] public required FellowItemInfo Item { get; init; }
    [JsonPropertyName("used_fellow_item_no_list")] public IReadOnlyList<long> UsedItems { get; init; } = [];
    [JsonPropertyName("update_achievement_info")] public IReadOnlyList<object> Achievements { get; init; } = [];
}

/// <summary>
/// Protocol 336: Steel Anvil Item Disassemble (request @0x14fd46, parser @0x160c32, post-handler @0x154aee/0x15652c):
/// {fellow_item_no_list:[uid]}. The result popup lists fellow_item_infos (gear given back) and the change of sub-materials
/// 1902001-1902008 between the counts before the request and update_item_etc_info (Scene_Process_SC_POPUP_ANVIL@0x8a012),
/// so the reply must carry the new absolute counts.
/// </summary>
/// <remarks>
/// Native data has no disassembly yield. Restoration choice: every sub-material along the item's craft chain (its recipe and
/// the recipes of its main materials), times a random c43..c44 % of the row (20 % when unset), at least 1 each. No gear is
/// returned (great_success_count 0).
/// </remarks>
public sealed class FellowItemDisassembleHandler(
    PlayerDirectory directory,
    PlayerProfileService profiles,
    FellowItemRepository fellowItems,
    MasterDataRepository masterData,
    Data.Repositories.GoodsRepository goods,
    IRandomSource random,
    ILogger<FellowItemDisassembleHandler> logger) : IProtocolHandler
{
    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        long[] numbers = request.Parameters.GetList("fellow_item_no_list").Select(LenientJson.ToInt64).Where(n => n > 0).Distinct().ToArray();
        if (numbers.Length == 0) return Refuse("nothing to disassemble");

        var items = new List<FellowItemRecord>();
        foreach (long number in numbers)
        {
            FellowItemRecord? item = fellowItems.Find(user, number);
            if (item is null || item.Locked != 0 || item.FellowNumber != 0) return Refuse("item unavailable");
            items.Add(item);
        }

        var returned = new Dictionary<long, long>();
        foreach (FellowItemRecord item in items)
        {
            FellowItemRule rule = masterData.GetFellowItem(item.FellowItemId) ?? new FellowItemRule();
            long low = rule.C43 > 0 ? rule.C43 : AnvilRules.DefaultReturnPercent;
            long high = Math.Max(low, rule.C44 > 0 ? rule.C44 : low);
            long percent = random.NextInclusive(low, high);
            foreach ((long id, long count) in ChainSubMaterials(item.FellowItemId))
                returned[id] = returned.GetValueOrDefault(id) + Math.Max(1, count * percent / 100);
            fellowItems.Delete(item.FellowItemNumber);
        }

        foreach ((long id, long count) in returned) goods.Add(user, id, count);
        logger.LogInformation("Ally item disassemble: [{Items}] -> {Materials}",
            string.Join(",", items.Select(i => i.FellowItemId)), string.Join(",", returned.Select(r => $"{r.Key}x{r.Value}")));
        return ProtocolResult.Success(new FellowItemDisassembleResponse
        {
            Goods = profiles.GetGoods(user),
            Disassembled = numbers,
        });
    }

    /// <summary>Sub-materials of the item's recipe plus, recursively, those of the gear it was crafted from.</summary>
    private Dictionary<long, long> ChainSubMaterials(long fellowItemId)
    {
        var total = new Dictionary<long, long>();
        var pending = new Stack<long>([fellowItemId]);
        var seen = new HashSet<long>();
        while (pending.Count > 0)
        {
            long id = pending.Pop();
            if (!seen.Add(id) || masterData.GetFellowItemRecipe(id) is not { } recipe) continue;
            foreach ((long sub, long count) in recipe.SubMaterials) total[sub] = total.GetValueOrDefault(sub) + count;
            foreach (long main in recipe.MainMaterials) pending.Push(main);
        }
        return total;
    }

    private ProtocolResult Refuse(string reason)
    {
        logger.LogInformation("Ally item disassemble refused: {Reason}", reason);
        return ProtocolResult.Refused(reason);
    }
}

public sealed record FellowItemDisassembleResponse
{
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("fellow_item_infos")] public IReadOnlyList<FellowItemInfo> ReturnedItems { get; init; } = [];
    [JsonPropertyName("decomposition_fellow_item_no_list")] public IReadOnlyList<long> Disassembled { get; init; } = [];
    [JsonPropertyName("great_success_count")] public long GreatSuccessCount { get; init; }
    [JsonPropertyName("update_achievement_info")] public IReadOnlyList<object> Achievements { get; init; } = [];
}

/// <summary>
/// Protocol 331: Steel Anvil craft (request @0x14e0bc, parser @0x15dbf8): {recipe_id, main_material_list:[{main_material_01..03}]},
/// one craft per list entry. Each entry's gear must match the recipe's main material ids (unlocked, not worn); every craft costs
/// the recipe's sub-materials and gold. Reply: fellow_item_infos (new gear), update_item_etc_info, used_fellow_item_no_list, make_count.
/// </summary>
public sealed class FellowItemCraftHandler(
    PlayerDirectory directory,
    PlayerProfileService profiles,
    FellowItemService fellowItemService,
    FellowItemRepository fellowItems,
    MasterDataRepository masterData,
    ILogger<FellowItemCraftHandler> logger) : IProtocolHandler
{
    private static readonly string[] MainMaterialKeys = ["main_material_01", "main_material_02", "main_material_03"];

    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        long recipeId = request.Parameters.GetInt64("recipe_id");
        FellowItemRecipe? recipe = masterData.GetFellowItemRecipe(recipeId);
        if (recipe is null) return Refuse("unknown recipe");

        IReadOnlyList<JsonNode?> entries = request.Parameters.GetList("main_material_list");
        int crafts = Math.Max(1, entries.Count);
        if (crafts > 100) return Refuse("too many crafts");

        var used = new List<long>();
        for (int i = 0; i < crafts; i++)
        {
            JsonNode? entry = entries.ElementAtOrDefault(i);
            long[] given = MainMaterialKeys.Select(key => LenientJson.TryToInt64(entry?[key]) ?? 0).Where(n => n > 0).ToArray();
            var wanted = recipe.MainMaterials.ToList();
            foreach (long number in given)
            {
                FellowItemRecord? item = fellowItems.Find(user, number);
                if (item is null || item.Locked != 0 || item.FellowNumber != 0 || used.Contains(number) || !wanted.Remove(item.FellowItemId))
                    return Refuse("main material does not match the recipe");
                used.Add(number);
            }
            if (wanted.Count != 0) return Refuse("missing main material");
        }

        long gold = recipe.CurrencyAmount * crafts;
        if (profiles.GetBalance(user, recipe.CurrencyId) < gold) return Refuse("not enough gold", 12004);
        if (recipe.SubMaterials.Any(sub => profiles.GetBalance(user, sub.ItemId) < sub.Count * crafts)) return Refuse("not enough materials", 12007);

        if (gold > 0) profiles.TrySpend(user, recipe.CurrencyId, gold);
        foreach ((long id, long count) in recipe.SubMaterials) profiles.TrySpend(user, id, count * crafts);
        foreach (long number in used) fellowItems.Delete(number);
        var made = new List<FellowItemInfo>();
        for (int i = 0; i < crafts; i++)
            made.Add(fellowItemService.ToInfo(fellowItems.Find(user, fellowItems.Add(user, recipe.ResultId))!));

        logger.LogInformation("Ally item craft: {Recipe} x{Count} ({Gold} G), used [{Used}]", recipeId, crafts, gold, string.Join(",", used));
        return ProtocolResult.Success(new FellowItemCraftResponse
        {
            Goods = profiles.GetGoods(user),
            Made = made,
            UsedItems = used,
            MakeCount = crafts,
        });
    }

    private ProtocolResult Refuse(string reason, int errno = 1)
    {
        logger.LogInformation("Ally item craft refused: {Reason}", reason);
        return ProtocolResult.Refused(reason, errno);
    }
}

public sealed record FellowItemCraftResponse
{
    [JsonPropertyName("fellow_item_infos")] public IReadOnlyList<FellowItemInfo> Made { get; init; } = [];
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("used_fellow_item_no_list")] public IReadOnlyList<long> UsedItems { get; init; } = [];
    [JsonPropertyName("make_count")] public long MakeCount { get; init; }
    [JsonPropertyName("update_achievement_info")] public IReadOnlyList<object> Achievements { get; init; } = [];
}
