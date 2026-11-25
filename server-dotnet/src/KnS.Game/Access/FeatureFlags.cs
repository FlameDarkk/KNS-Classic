using KnS.Data.Repositories;

namespace KnS.Game.Access;

/// <summary>The feature_flag table, read on every use so a change applies to the next request.</summary>
public sealed class FeatureFlags(FeatureFlagRepository flags)
{
    /// <summary>The state of a flag whose row is missing (the schema seeds these as rows).</summary>
    public static readonly IReadOnlyDictionary<string, bool> Defaults = new Dictionary<string, bool>();

    public bool IsEnabled(string featureKey) => flags.IsEnabled(featureKey, Defaults.GetValueOrDefault(featureKey));
}
