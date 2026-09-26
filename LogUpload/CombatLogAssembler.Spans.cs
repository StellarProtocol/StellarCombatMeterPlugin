using System.Collections.Generic;

namespace Stellar.CombatMeter.LogUpload;

// Frozen-snapshot → upload mappers for the per-actor sparse arrays (attr peaks, class timeline, talent spec timeline).
// Split out of CombatLogAssembler.cs (over the 500-LoC cap) when specSpans was added (spec upload design 2026-09-26).
internal sealed partial class CombatLogAssembler
{
    /// <summary>Snapshot sparse peak arrays → [attrId, peakValue][] for upload; null when empty.</summary>
    internal static IReadOnlyList<long[]>? BuildActorAttrPeaks(EntitySnapshot snap)
    {
        if (snap.AttrPeakIds.Length == 0) return null;
        var peaks = new long[snap.AttrPeakIds.Length][];
        for (var i = 0; i < snap.AttrPeakIds.Length; i++)
            peaks[i] = new long[] { snap.AttrPeakIds[i], snap.AttrPeakValues[i] };
        return peaks;
    }

    /// <summary>Snapshot's parallel ClassSpan* arrays (baked in by <c>Plugin.ApplyClassSpans</c> at
    /// archive) → [professionId,startMs,endMs][] for upload; null when empty (single-class actor — no
    /// timeline needed). Populated for EVERY player actor, self AND party alike.</summary>
    internal static IReadOnlyList<long[]>? BuildActorClassSpans(EntitySnapshot snap)
    {
        if (snap.ClassSpanProf.Length == 0) return null;
        var spans = new long[snap.ClassSpanProf.Length][];
        for (var i = 0; i < snap.ClassSpanProf.Length; i++)
            spans[i] = new long[] { snap.ClassSpanProf[i], snap.ClassSpanStart[i], snap.ClassSpanEnd[i] };
        return spans;
    }

    /// <summary>Snapshot's parallel SpecSpan* arrays (baked in by <c>Plugin.ApplySpecSpans</c> at archive) →
    /// [specId,startMs,endMs][] for upload — TALENT-derived spans only, same time base as <c>classSpans</c>; null when
    /// empty so the actor block is byte-identical to 2.14.2 (the writer omits the key). Players only.</summary>
    internal static IReadOnlyList<long[]>? BuildActorSpecSpans(EntitySnapshot snap)
    {
        var n = System.Math.Min(snap.SpecSpanId.Length, System.Math.Min(snap.SpecSpanStart.Length, snap.SpecSpanEnd.Length));
        if (n == 0) return null;
        var spans = new long[n][];
        for (var i = 0; i < n; i++)
            spans[i] = new long[] { snap.SpecSpanId[i], snap.SpecSpanStart[i], snap.SpecSpanEnd[i] };
        return spans;
    }
}
