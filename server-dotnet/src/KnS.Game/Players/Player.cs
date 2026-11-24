using KnS.Data.Repositories;
using KnS.Game.Rules;

namespace KnS.Game.Players;

/// <summary>A registered player.</summary>
/// <param name="HubUid">The Hub uid registered with ("-1" for Hive guests); may be null on old rows.</param>
/// <param name="Nickname">The nickname, or the default "Squire&lt;user number&gt;".</param>
public sealed record Player(long UserNumber, string? HubUid, string Nickname)
{
    public static Player From(PlayerRecord record) =>
        new(record.UserNumber, record.HubUid,
            string.IsNullOrEmpty(record.Nickname) ? PlayerRules.DefaultNickname(record.UserNumber) : record.Nickname);
}
