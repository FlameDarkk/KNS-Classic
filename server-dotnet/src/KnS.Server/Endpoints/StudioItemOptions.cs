using System.Text.Json.Nodes;
using KnS.Data;
using KnS.Data.Repositories;
using KnS.Game.Randomness;
using KnS.Game.Rules;

namespace KnS.Server.Endpoints;

/// <summary>Studio uses the native option slots, not a separate cosmetic rarity field.</summary>
internal static class StudioItemOptions
{
    public static string? Prepare(IServiceProvider services, long id, JsonObject body, out List<ItemOptionRecord>? result)
    {
        result = null;
        if (!body.ContainsKey("option_mode")) return null;
        string mode = body["option_mode"]?.ToString() ?? "";
        if (mode == "keep") return null;
        var master = services.GetRequiredService<MasterDataRepository>();
        var rule = master.GetOptionRule(id);
        if (rule is null || !ItemOptionRules.OptionBearingCategories.Contains(rule.Category)) return "this item has no editable option slots";
        var allowed = rule.HasFixedOptions == 1 ? rule.FixedOptions.ToList() : master.GetRollableOptions(rule.Category, rule.Grade).ToList();
        var random = services.GetRequiredService<IRandomSource>();
        var picked = new List<ItemOptionRecord>();
        if (mode == "manual")
        {
            if (body["options"] is not JsonArray rows || rows.Count > ItemOptionRules.MaxOptions) return "invalid option list";
            var families = new HashSet<long>();
            foreach (var node in rows)
            {
                if (node is not JsonObject row || !StudioInventoryEditor.Number(row, "id", 1, int.MaxValue, out long option)
                    || !allowed.Contains(option) || !families.Add(ItemOptionRules.FamilyOf(option))) return "option is incompatible or duplicates a family";
                var range = master.GetOptionValueRange(option)!;
                if (!StudioInventoryEditor.Number(row, "value1", range.Value1Min, range.Value1Max, out long value1)
                    || !StudioInventoryEditor.Number(row, "value2", range.Value2Min, range.Value2Max, out long value2)) return "option values exceed game data ranges";
                picked.Add(new ItemOptionRecord { OptionId=option, Value1=value1, Value2=value2 });
            }
            if (rule.HasFixedOptions == 1 && !picked.Select(x => x.OptionId).Order().SequenceEqual(allowed.Order())) return "this special item requires its fixed options";
        }
        else if (mode == "random")
        {
            if (!StudioInventoryEditor.Number(body, "option_count", 0, 6, out long count)) return "option count must be 0 to 6";
            if (rule.HasFixedOptions == 1 && count != allowed.Count) return "this special item requires its fixed option count";
            if (rule.HasFixedOptions != 1) random.Shuffle(allowed);
            foreach (long option in allowed.DistinctBy(ItemOptionRules.FamilyOf).Take((int)count))
            {
                var range = master.GetOptionValueRange(option)!;
                picked.Add(new ItemOptionRecord { OptionId=option,
                    Value1=random.NextInclusive(range.Value1Min, range.Value1Max),
                    Value2=random.NextInclusive(range.Value2Min, range.Value2Max) });
            }
            if (picked.Count != count) return "not enough compatible option families for this rarity";
        }
        else return "unknown option mode";
        result = picked;
        return null;
    }

    public static void Save(IServiceProvider services, long user, long number, IReadOnlyList<ItemOptionRecord> options)
    {
        services.GetRequiredService<DataSession>().Execute("DELETE FROM item_option WHERE item_no=@number AND user_no=@user", new { number, user });
        var items = services.GetRequiredService<ItemRepository>();
        for (int slot=0; slot<options.Count; slot++)
            items.SetOption(user, number, slot, options[slot].OptionId, options[slot].Value1, options[slot].Value2);
    }
}
