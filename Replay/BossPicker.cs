using System.Collections.Generic;

namespace Stellar.CombatMeter.Replay;

/// <summary>
/// Pure boss selection among captured non-player entities: picks the <c>isBoss</c>-tagged
/// entity; when several share the tag, the one with the highest <c>maxHp</c> wins
/// (highest observed MaxHp = the real boss vs. its dummy/sub-boss adds).
/// Returns <c>null</c> when no candidate is tagged as a boss.
/// </summary>
internal static class BossPicker
{
    /// <summary>
    /// Returns the entity id of the selected boss, or <c>null</c> when no candidate has
    /// <c>isBoss == true</c>.
    /// </summary>
    public static long? Pick(IReadOnlyList<(long id, bool isBoss, long maxHp)> candidates)
    {
        long? best = null;
        long bestHp = long.MinValue;
        foreach (var c in candidates)
            if (c.isBoss && c.maxHp > bestHp) { bestHp = c.maxHp; best = c.id; }
        return best;
    }
}
