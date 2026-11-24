using System.Text.Json.Serialization;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Gateway;
using KnS.Game.Models;
using KnS.Game.Players;
using KnS.Game.Randomness;
using Microsoft.Extensions.Logging;

namespace KnS.Game.Protocols.Fellows;

/// <summary>
/// Protocol 1101: Altar (Superb Evolution, 5★/6★ Transform, Arena). Request (Scene_PointerReleaseEvent_SC_POPUP_ALTAR
/// 0x82668-0x826c8, built @0x14f8aa): {recipe_id, main_material_01, main_material_02, main_material_03 (always 0), group_num (ignored)}.
/// Reply (parser @0x15f24e, applied @0x155588): update_item_etc_info, one fellow_info (the new ally, drives the result animation),
/// fellow_book_info, used_item (material fellow_nos the client deletes).
/// </summary>
/// <remarks>
/// Materials (MakeAltarFellowMaterialList@0x7fabc): kinds 1/2 take two owned copies of the recipe's material ids at MAX level and +5;
/// kind 0 (transform) takes one ally of the recipe's grade. Unlocked and not placed in a slot. Kinds 1/2 give the recipe id; kind 0
/// gives a uniform pick from the native transform pool (master int 95, the client's cycling preview) of that grade, other than the
/// material's own id; the uniform pick is a restoration choice. Results start at level 1 and +0 (the confirm text: "The Powered-up
/// ATK will reset"). Refusals use table 14 errnos so the client shows text instead of an empty box.
/// </remarks>
public sealed class AltarCraftHandler(
    PlayerDirectory directory,
    PlayerProfileService profiles,
    FellowService fellowService,
    FellowRepository fellows,
    FellowItemRepository fellowItems,
    KnS.Game.Missions.FellowMissionService missions,
    MasterDataRepository masterData,
    IRandomSource random,
    ILogger<AltarCraftHandler> logger) : IProtocolHandler
{
    public const int ErrnoNotEnoughGold = 12004;
    public const int ErrnoPossessions = 12007;
    public const int ErrnoNoAlly = 21001;
    public const int ErrnoAllyInUse = 21008;
    public const int ErrnoAllyLocked = 21009;
    public const int ErrnoNotMaxPowerUp = 21011;
    public const int ErrnoInvalidAlly = 21016;
    public const long RequiredUpgrade = 5;

    public ProtocolResult Handle(GatewayRequest request)
    {
        long user = directory.ResolveCurrent(request).UserNumber;
        long recipeId = request.Parameters.GetInt64("recipe_id");
        long first = request.Parameters.GetInt64("main_material_01");
        long second = request.Parameters.GetInt64("main_material_02");
        AltarRecipe? recipe = masterData.GetAltarRecipe(recipeId);
        if (recipe is null || request.Parameters.GetInt64("main_material_03") != 0)
            return Refuse(recipeId, "unknown altar recipe", ErrnoInvalidAlly);

        bool transform = recipe.Kind == 0;
        long[] numbers = transform ? [first] : [first, second];
        long[] wantedIds = transform ? [] : [recipe.MaterialFellow1, recipe.MaterialFellow2];
        if (numbers.Any(number => number <= 0) || numbers.Distinct().Count() != numbers.Length)
            return Refuse(recipeId, "altar needs distinct material allies", ErrnoInvalidAlly);

        HashSet<long> slotted = fellows.GetFilledSlots(user).Select(slot => slot.FellowNumber).ToHashSet();
        HashSet<long> busy = missions.GetBusyFellows(user);
        var materials = new List<FellowUpgradeRecord>();
        for (int i = 0; i < numbers.Length; i++)
        {
            FellowUpgradeRecord? ally = fellows.FindForUpgrade(user, numbers[i]);
            if (ally is null) return Refuse(recipeId, "material ally not owned", ErrnoNoAlly);
            if (ally.Locked != 0) return Refuse(recipeId, "material ally locked", ErrnoAllyLocked);
            if (slotted.Contains(ally.FellowNumber) || busy.Contains(ally.FellowNumber))
                return Refuse(recipeId, "material ally in use", ErrnoAllyInUse);
            if (transform ? ally.Grade != recipe.TransformGrade : ally.FellowId != wantedIds[i])
                return Refuse(recipeId, "wrong material ally", ErrnoInvalidAlly);
            if (!transform && (ally.Upgrade < RequiredUpgrade || ally.Level < ally.MaxLevel))
                return Refuse(recipeId, "material ally not MAX +5", ErrnoNotMaxPowerUp);
            materials.Add(ally);
        }

        long resultId = recipe.RecipeId;
        if (transform)
        {
            List<long> pool = masterData.GetFellowPoolFlags()
                .Where(row => row.Transformable && row.Grade == recipe.TransformGrade && row.FellowId != materials[0].FellowId)
                .Select(row => row.FellowId).ToList();
            if (pool.Count == 0) return Refuse(recipeId, "empty transform pool", ErrnoInvalidAlly);
            resultId = random.Pick(pool);
        }

        // Check every cost before spending: a refusal still commits the transaction.
        if (recipe.CurrencyAmount > 0 && profiles.GetBalance(user, recipe.CurrencyItem) < recipe.CurrencyAmount)
            return Refuse(recipeId, "not enough gold", ErrnoNotEnoughGold);
        if (recipe.Items.Any(item => profiles.GetBalance(user, item.ItemId) < item.Count))
            return Refuse(recipeId, "not enough materials", ErrnoPossessions);

        if (recipe.CurrencyAmount > 0) profiles.TrySpend(user, recipe.CurrencyItem, recipe.CurrencyAmount);
        foreach ((long itemId, long count) in recipe.Items) profiles.TrySpend(user, itemId, count);
        var freed = new List<long>();
        foreach (FellowUpgradeRecord ally in materials)
        {
            freed.AddRange(fellowItems.ReleaseFromFellow(user, ally.FellowNumber));
            fellows.Delete(ally.FellowNumber);
        }

        long newNumber = fellowService.Grant(user, resultId);
        logger.LogInformation("Altar {Recipe}: {Materials} -> {Result} (fellow_no {Number})",
            recipeId, string.Join(",", materials.Select(ally => ally.FellowId)), resultId, newNumber);
        return ProtocolResult.Success(new AltarCraftResponse
        {
            Goods = profiles.GetGoods(user),
            Fellow = fellowService.GetFellowInfos(user, new HashSet<long> { newNumber }).Single(),
            FellowBook = fellowService.GetBook(user, new HashSet<long> { resultId }),
            UsedFellows = materials.Select(ally => ally.FellowNumber).ToList(),
            UnequippedFellowItems = freed,
        });
    }

    private ProtocolResult Refuse(long recipeId, string reason, int errno)
    {
        logger.LogInformation("Altar {Recipe} refused: {Reason}", recipeId, reason);
        return ProtocolResult.Refused(reason, errno);
    }
}

public sealed record AltarCraftResponse
{
    [JsonPropertyName("update_item_etc_info")] public IReadOnlyList<GoodsEntry> Goods { get; init; } = [];
    [JsonPropertyName("fellow_info")] public FellowInfo Fellow { get; init; } = new();
    [JsonPropertyName("fellow_book_info")] public IReadOnlyList<FellowBookEntry> FellowBook { get; init; } = [];
    [JsonPropertyName("used_item")] public IReadOnlyList<long> UsedFellows { get; init; } = [];

    /// <summary>Not read by the 1101 parser; items worn by consumed allies return to the bag on the next load.</summary>
    [JsonPropertyName("unequip_fellow_item_no_list")] public IReadOnlyList<long> UnequippedFellowItems { get; init; } = [];
}
