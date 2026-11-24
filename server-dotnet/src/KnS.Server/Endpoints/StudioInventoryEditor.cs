using System.Text.Json;
using System.Text.Json.Nodes;
using KnS.Data;
using KnS.Data.Repositories;
using KnS.Game.Fellows;
using KnS.Game.Inventory;
using KnS.Game.Missions;
using KnS.Game.Rules;

namespace KnS.Server.Endpoints;

/// <summary>Bounded administrative inventory edits. The caller owns the transaction.</summary>
internal static class StudioInventoryEditor
{
    public static void AddMetadata(JsonObject snapshot, IServiceProvider services, long user)
    {
        var inventory = services.GetRequiredService<InventoryService>();
        var fellows = services.GetRequiredService<FellowRepository>();
        snapshot["heroes"] = JsonSerializer.SerializeToNode(inventory.GetHeroes(user));
        snapshot["hero_equipment"] = JsonSerializer.SerializeToNode(inventory.GetWornItems(user));
        snapshot["starter_items"] = JsonSerializer.SerializeToNode(inventory.GetStarterItems(user, inventory.GetWornItems(user)));
        snapshot["party"] = JsonSerializer.SerializeToNode(fellows.GetFilledSlots(user));
        snapshot["busy_fellows"] = JsonSerializer.SerializeToNode(services.GetRequiredService<FellowMissionService>().GetBusyFellows(user));
        snapshot["player_levels"] = JsonSerializer.SerializeToNode(services.GetRequiredService<MasterDataRepository>().GetExperienceTable());
        snapshot["capabilities"] = new JsonArray("inventory_edit", "events", "material_grants", "grant_quantity", "item_options", "batch_inventory", "party_swap");
        snapshot["item_options"] = JsonSerializer.SerializeToNode(ItemsForSnapshot(services, user));
    }

    private static Dictionary<long, IReadOnlyList<ItemOptionRecord>> ItemsForSnapshot(IServiceProvider services, long user)
    {
        var items = services.GetRequiredService<ItemRepository>();
        return items.GetOwned(user).ToDictionary(i => i.ItemNumber, i => items.GetOptions(i.ItemNumber));
    }

    public static bool Number(JsonObject body, string key, long min, long max, out long value) =>
        long.TryParse(body[key]?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

    public static string? Grant(IServiceProvider services, long user, string action, JsonObject body)
    {
        var master = services.GetRequiredService<MasterDataRepository>();
        var items = services.GetRequiredService<ItemRepository>();
        var gear = services.GetRequiredService<FellowItemRepository>();
        if (!Number(body, "item_id", 1, int.MaxValue, out long id)) return "invalid master ID";
        long quantity = 1;
        if (body.ContainsKey("quantity") && !Number(body, "quantity", 1, 100, out quantity)) return "quantity must be between 1 and 100";
        bool valid = action switch
        {
            "item" => master.ItemExists(id),
            "material" => id < 1900000 && master.GetMaterialItemIds().Contains(id),
            "fellow" => master.GetDrawableFellows().Any(f => f.FellowId == id)
                || services.GetRequiredService<DataSession>().Exists("SELECT fellow_id FROM master_fellow WHERE fellow_id=@id", new { id }),
            "fellow_item" => master.GetFellowItem(id) is not null,
            _ => false,
        };
        if (!valid) return "unknown master ID for this kind";
        if (action == "item")
        {
            string? error = StudioItemOptions.Prepare(services, id, body, out _);
            if (error is not null) return error;
        }
        else if (body.ContainsKey("option_mode")) return "options only apply to hero equipment";
        // Granting uses the same repositories/services as gameplay; no invented balances or stats.
        for (long i = 0; i < quantity; i++)
        {
            if (action is "item" or "material")
            {
                long number = items.Add(user, id, true, services.GetRequiredService<InventoryService>().GetNewItemStats(id));
                if (action == "item")
                {
                    StudioItemOptions.Prepare(services, id, body, out var options);
                    if (options is not null) StudioItemOptions.Save(services, user, number, options);
                    else services.GetRequiredService<ItemOptionRoller>().Roll(user, number, id, 1);
                }
            }
            else if (action == "fellow") services.GetRequiredService<FellowService>().Grant(user, id);
            else gear.Add(user, id);
        }
        return null;
    }

    public static string? Apply(DataSession db, IServiceProvider services, long user, string action, JsonObject body)
    {
        if (!Number(body, "number", 1, int.MaxValue, out long number)) return "invalid instance number";
        var items = services.GetRequiredService<ItemRepository>();
        var fellows = services.GetRequiredService<FellowRepository>();
        var gear = services.GetRequiredService<FellowItemRepository>();
        var inventory = services.GetRequiredService<InventoryService>();
        var master = services.GetRequiredService<MasterDataRepository>();
        var busy = services.GetRequiredService<FellowMissionService>().GetBusyFellows(user);

        if (action is "item_edit" or "item_delete" or "item_equip")
        {
            ItemRecord? item = items.FindOwned(user, number);
            StarterItem? starter = StarterGear.FindByItemNumber(number);
            if (starter is not null && items.GetGivenUpStarterItems(user).Contains(number)) starter = null;
            if (item is null && starter is null) return "item is not owned by this account";
            long id = item?.ItemId ?? starter!.ItemId;
            var worn = inventory.GetWornItems(user);
            bool locked = item?.Locked ?? items.GetLockedStarterItems(user).Contains(number);
            if (action == "item_delete")
            {
                if (locked || worn.ContainsKey(number)) return "unlock and unequip this item before removing it";
                if (starter is not null) items.MarkStarterItemGivenUp(user, number); else items.Delete(number);
            }
            else if (action == "item_equip")
            {
                if (!Number(body, "hero_no", 0, int.MaxValue, out long hero)) return "invalid hero";
                if (hero == 0)
                {
                    // Explicit zero overrides prevent default starter gear from reappearing as equipped.
                    foreach (var owner in inventory.GetHeroes(user).Where(h => worn.GetValueOrDefault(number) == h.HeroNumber))
                        services.GetRequiredService<HeroRepository>().Equip(user, owner.HeroNumber, EquipmentRanges.CategoryOf(id), 0);
                }
                else
                {
                    var target = inventory.GetHeroes(user).FirstOrDefault(h => h.HeroNumber == hero);
                    if (target is null || !services.GetRequiredService<HeroGearRules>().CanEquip(id, master.GetHeroSeries(target.HeroPcid))) return "item does not fit this owned hero";
                    if (worn.TryGetValue(number, out long previous) && previous != hero) return "unequip this item from its current hero first";
                    services.GetRequiredService<HeroRepository>().Equip(user, hero, EquipmentRanges.CategoryOf(id), number);
                }
            }
            else
            {
                if (!Number(body, "locked", 0, 1, out long lockValue)) return "invalid lock state";
                string? optionError = StudioItemOptions.Prepare(services, id, body, out var options);
                if (optionError is not null) return optionError;
                if (starter is not null && options is not null) return "starter options cannot be edited; grant an owned copy";
                long upgrade = item?.Upgrade ?? 0;
                if (body.ContainsKey("upgrade"))
                {
                    var rule = master.GetPowerUpRule(id);
                    if (starter is not null || rule is null || !Number(body, "upgrade", 0, Math.Max(0, rule.MaxUpgrade), out upgrade)) return "invalid upgrade for this item";
                    long delta = upgrade - item!.Upgrade;
                    long attack = item.Attack + delta * Math.Max(0, rule.AttackGain);
                    long defence = item.Defence + delta * Math.Max(0, rule.DefenceGain);
                    long hp = item.Hp + delta * Math.Max(0, rule.HpGain);
                    if (attack < 0 || defence < 0 || hp < 0 || attack > int.MaxValue || defence > int.MaxValue || hp > int.MaxValue) return "upgrade would produce invalid stats";
                    db.Execute("UPDATE item SET upgrade=@upgrade,attack=@attack,defence=@defence,hp=@hp WHERE rowid_alias=@rowId AND user_no=@user", new { upgrade, attack, defence, hp, rowId = number-WireIdentifiers.ItemNumberOffset, user });
                }
                if (options is not null) StudioItemOptions.Save(services, user, number, options);
                if (starter is not null) items.SetStarterLocked(user, number, lockValue != 0);
                else items.SetLocked(user, number, lockValue != 0);
            }
            return null;
        }
        if (action is "fellow_edit" or "fellow_delete" or "fellow_slot" or "fellow_swap")
        {
            var fellow = fellows.GetOwned(user).FirstOrDefault(f => f.FellowNumber == number);
            if (fellow is null) return "ally is not owned by this account";
            if (busy.Contains(number)) return "ally is on a mission; collect its mission before editing";
            if (action == "fellow_delete")
            {
                if (fellow.Locked != 0 || fellows.GetFilledSlots(user).Any(s => s.FellowNumber == number)
                    || gear.GetOwned(user).Any(g => g.FellowNumber == number)) return "unlock, remove from party and unequip this ally before removing it";
                fellows.Delete(number);
            }
            else if (action == "fellow_swap")
            {
                if (!Number(body, "setup", 1, 3, out long setup) || !Number(body, "position", 0, 2, out long position)
                    || !Number(body, "expected_target", 0, int.MaxValue, out long expected)) return "invalid party destination";
                if (FellowRules.IsAngel(fellow.FellowId)) return "material angels cannot join a party";
                var slots = fellows.GetFilledSlots(user).Where(s => s.SetupType == setup).ToList();
                var source = slots.FirstOrDefault(s => s.FellowNumber == number);
                var target = slots.FirstOrDefault(s => s.Position == position);
                long displaced = target?.FellowNumber ?? 0;
                if (displaced != expected) return "party changed; reload before swapping";
                if (displaced == number) return null;
                if (busy.Contains(displaced)) return "party slot belongs to a mission ally";
                if (source is not null) fellows.SetSlot(user, setup, source.Position, displaced);
                fellows.SetSlot(user, setup, position, number);
            }
            else if (action == "fellow_slot")
            {
                if (!Number(body, "setup", 1, 3, out long setup) || !Number(body, "position", -1, 2, out long position)) return "invalid party setup or position";
                if (position >= 0 && FellowRules.IsAngel(fellow.FellowId)) return "material angels cannot join a party";
                if (position >= 0 && fellows.GetFilledSlots(user).Any(s => s.SetupType == setup && s.Position == position && s.FellowNumber != number && busy.Contains(s.FellowNumber))) return "party slot belongs to a mission ally";
                foreach (var slot in fellows.GetFilledSlots(user).Where(s => s.SetupType == setup && s.FellowNumber == number)) fellows.SetSlot(user, setup, slot.Position, 0);
                if (position >= 0) fellows.SetSlot(user, setup, position, number);
            }
            else
            {
                if (!Number(body, "level", 1, fellow.MaxLevel, out long level) || !Number(body, "experience", 0, int.MaxValue, out long xp)
                    || !Number(body, "upgrade", 0, FellowRules.IsAngel(fellow.FellowId) ? 0 : FellowRules.MaxUpgrade, out long upgrade)
                    || !Number(body, "locked", 0, 1, out long locked)) return "invalid ally level, XP, upgrade or lock";
                var progress = fellows.FindProgress(user, number)!;
                var growth = master.GetFellowExperienceTable(progress.Grade);
                if (level == fellow.MaxLevel ? xp != 0 : !growth.TryGetValue(level, out long limit) || xp >= limit) return "XP must fit the selected ally level; capped allies require zero XP";
                fellows.SetProgress(number, level, xp);
                fellows.SetUpgradeState(number, upgrade, fellow.FailCount, fellow.AttackBonus);
                fellows.SetLocked(user, number, locked);
            }
            return null;
        }
        if (action is "fellow_item_edit" or "fellow_item_delete" or "fellow_item_equip")
        {
            var item = gear.Find(user, number);
            if (item is null) return "ally equipment is not owned by this account";
            if (item.FellowNumber != 0 && busy.Contains(item.FellowNumber)) return "equipment belongs to an ally on a mission";
            if (action == "fellow_item_delete")
            {
                if (item.Locked != 0 || item.FellowNumber != 0) return "unlock and unequip this item before removing it";
                gear.Delete(number);
            }
            else if (action == "fellow_item_edit")
            {
                if (!Number(body, "locked", 0, 1, out long locked)) return "invalid lock state";
                gear.SetLocked(number, locked != 0);
            }
            else
            {
                if (!Number(body, "fellow_no", 0, int.MaxValue, out long target)) return "invalid ally";
                if (target == 0) gear.SetEquipped(number, 0, 0);
                else
                {
                    if (!fellows.GetOwnedFellowNumbers(user).Contains(target) || busy.Contains(target)) return "target ally is not available";
                    if (!Number(body, "equip_index", 1, 3, out long slot) || !FellowRules.FitsEquipIndex(item.FellowItemId, slot)) return "item does not fit this slot";
                    if (item.FellowNumber != 0 && item.FellowNumber != target) return "unequip this item from its current ally first";
                    foreach (var other in gear.GetOwned(user).Where(g => g.FellowNumber == target && g.EquipIndex == slot && g.FellowItemNumber != number)) gear.SetEquipped(other.FellowItemNumber, 0, 0);
                    gear.SetEquipped(number, target, slot);
                }
            }
            return null;
        }
        return "unknown inventory edit action";
    }
}
