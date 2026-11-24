namespace KnS.Data;

/// <summary>
/// What a new player starts with. Registration writes these explicitly: older databases still carry the
/// earlier testing values (99,999 gold, 9,999 gems, level 10) as gameuser column defaults.
/// </summary>
public static class StoredPlayerDefaults
{
    public const long Gold = 0;
    public const long Gems = 0;

    /// <summary>The hero row in the client's master data that every player owns.</summary>
    public const long HeroPcid = 1;

    public const long Level = 1;

    /// <summary>Hero Points (1900016) a new player starts with.</summary>
    public const long HeroPoints = 0;
}
