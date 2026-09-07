using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>Builds the `sheet` track rows (spec § 6.1): the local player's damage-relevant attributes as
/// ABSOLUTE values, change-only (a delta row lists the tracked attrs a packet carried) behind one keyframe
/// per segment (every tracked attr the live sheet has). Pure.</summary>
internal static class SheetRowBuilder
{
    /// <summary>The 34 `IsSyncMe` attrs the buff fit regresses (FightAttrTable): crit/lucky chance (11710/11780),
    /// crit/lucky damage (12510/12530), generic/boss damage bonus (12670/12630), element damage bonuses
    /// (13100–13180), physical/magic attack (11330/11340), element attacks (11500–11580), plus the phase 2b
    /// plugin-derived local cooldown ratio (<see cref="CdRatioTracker.AttrId"/>).</summary>
    internal static readonly int[] TrackedAttrs =
    {
        11710, 11780, 12510, 12530, 12670, 12630,
        13100, 13110, 13120, 13130, 13140, 13150, 13160, 13170, 13180,
        11330, 11340, 11500, 11510, 11520, 11530, 11540, 11550, 11560, 11570, 11580,
        11720, 11760, 11840, 11930, 11940, 11960, 11980, // phase 2 (2.8.0): attack speed, skill CD reduction, versatility DMG, haste, mastery, CD acceleration, resource CD
        CdRatioTracker.AttrId, // phase 2b (2.9.0): plugin-derived local cooldown ratio (D10)
    };

    private static readonly HashSet<int> Tracked = new(TrackedAttrs);

    internal static SheetEvent? Project(CombatEvent.EntityAttributesChanged ac)
    {
        List<long[]>? rows = null;
        for (var i = 0; i < ac.Attrs.Count; i++)
        {
            var a = ac.Attrs[i];
            if (!Tracked.Contains(a.AttrId)) continue;
            (rows ??= new List<long[]>(4)).Add(new[] { (long)a.AttrId, a.Value });
        }
        return rows is null ? null : new SheetEvent(ac.TimestampMs, false, rows);
    }

    internal static SheetEvent? Keyframe(long ms, IReadOnlyDictionary<int, long> sheet)
    {
        List<long[]>? rows = null;
        foreach (var id in TrackedAttrs)
            if (sheet.TryGetValue(id, out var v)) (rows ??= new List<long[]>(TrackedAttrs.Length)).Add(new[] { (long)id, v });
        return rows is null ? null : new SheetEvent(ms, true, rows);
    }

    /// <summary>One synthetic, unflagged (non-keyframe) sheet row carrying a single plugin-derived attr (D10).</summary>
    internal static SheetEvent Synthetic(long ms, int attrId, long value) =>
        new(ms, false, new List<long[]>(1) { new[] { (long)attrId, value } });
}
