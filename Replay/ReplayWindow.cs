using System.Collections.Generic;

namespace Stellar.CombatMeter.Replay;

/// <summary>
/// Pure slicer for the delta-window replay upload (owner design 2026-07-19): the recorder never
/// stops mid-run; each banked archive uploads only the samples in the half-open-below / closed-above
/// window <c>(watermark, thisArchive]</c> and advances the watermark. The load-bearing invariant —
/// verified in <c>ReplayWindowTests</c> — is that two contiguous windows CONCATENATE to the single-doc
/// baseline: no gap, no overlap. Nothing here mutates the source buffers; freeing consumed samples is
/// a separate step (<c>TrimBelow</c>) the caller runs only once the window's hand-off has succeeded.
/// </summary>
internal static class ReplayWindow
{
    /// <summary>Position samples with <c>lowerExclusive &lt; Ms &lt;= upperInclusive</c>, in order.
    /// The initial watermark is below zero so window 1 carries the walk-in lead (ms=0); each later
    /// window's lower bound is the previous archive's upper bound, so the windows tile exactly.</summary>
    internal static PositionSample[] SlicePositions(
        PositionSample[] samples, long lowerExclusive, long upperInclusive)
    {
        var result = new List<PositionSample>(samples.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            var ms = samples[i].Ms;
            if (ms > lowerExclusive && ms <= upperInclusive) result.Add(samples[i]);
        }
        return result.ToArray();
    }

    /// <summary>
    /// Slices an HP timeline to the window. The track carries no per-sample timestamp — sample
    /// <c>i</c> lives at grid time <c>Ms0 + i * cadenceMs</c> (the site decodes it the same way), so a
    /// window is an index range and the emitted <see cref="HpTrack.Ms0"/> must be RECOMPUTED to the
    /// first included grid time. A <c>MarkDead</c> 0-stamp is just the last grid slot, so it lands in
    /// whichever window contains its grid time. Returns null when no sample falls in the window.
    /// </summary>
    internal static HpTrack? SliceHp(HpTrack track, long lowerExclusive, long upperInclusive, int cadenceMs)
    {
        var pct = track.Pct;
        var first = -1;
        var kept = new List<int>(pct.Count);
        for (var i = 0; i < pct.Count; i++)
        {
            long grid = track.Ms0 + (long)i * cadenceMs;
            if (grid <= lowerExclusive || grid > upperInclusive) continue;
            if (first < 0) first = i;
            kept.Add(pct[i]);
        }
        if (first < 0) return null;
        return new HpTrack(track.Ms0 + (long)first * cadenceMs, kept.ToArray());
    }
}
