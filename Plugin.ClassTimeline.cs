using System;
using System.Collections.Generic;

namespace Stellar.CombatMeter;

/// <summary>
/// Pure per-entity professionId timeline accumulator (owner design 2026-08-03, per-entity class
/// detection). Site guessed which class a player was playing from the skills they cast, which is
/// wrong the moment a class swap happens mid-run — the framework broadcasts the real professionId
/// per entity (attr 220), so this records EVERY class an entity was observed playing this run as a
/// list of change points, then folds them into contiguous <c>[professionId, startMs, endMs]</c> spans
/// at archive time. Keyed by <c>EntityId.Value</c> so self AND every party member get their own
/// timeline. Plain data in / plain data out, so it is unit-testable without an IL2CPP/IPluginServices
/// fake (see <c>ClassSpanTrackerTests</c>). Reset at RUN START only (see <c>Plugin.LoadoutCapture.cs</c>
/// <c>TickLoadoutRunBoundary</c> wiring) — never per-archive, mirroring <c>AttrRangeTracker</c>.
/// </summary>
internal sealed class ClassSpanTracker
{
    // entityId -> change points [ms, professionId], in the order they were observed (the tick that
    // calls Observe runs in ascending-ms order, so no sort is needed).
    private readonly Dictionary<long, List<long[]>> _changePoints = new();

    /// <summary>Records a change point for <paramref name="entityId"/> when its professionId differs
    /// from the last one recorded for it. Profession 0 (unknown) is ignored — never recorded, never
    /// closes out a real span early. Repeated observations of the SAME profession are no-ops (only
    /// genuine class swaps add a point).</summary>
    public void Observe(long entityId, int professionId, long ms)
    {
        if (professionId == 0) return;
        if (!_changePoints.TryGetValue(entityId, out var points))
            _changePoints[entityId] = points = new List<long[]>();
        if (points.Count > 0 && points[^1][1] == professionId) return;   // unchanged — no new point
        points.Add(new[] { ms, (long)professionId });
    }

    /// <summary>Folds <paramref name="entityId"/>'s recorded change points into contiguous
    /// <c>[professionId, startMs, endMs]</c> spans, the last one capped at <paramref name="endMs"/>
    /// (the archive boundary). Empty when the entity had ≤1 distinct class this run — a single-class
    /// entity needs no timeline at all.</summary>
    public IReadOnlyList<long[]> Spans(long entityId, long endMs)
    {
        if (!_changePoints.TryGetValue(entityId, out var points) || points.Count <= 1)
            return Array.Empty<long[]>();

        var spans = new List<long[]>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var start = points[i][0];
            var prof  = points[i][1];
            var end   = i + 1 < points.Count ? points[i + 1][0] : endMs;
            spans.Add(new[] { prof, start, end });
        }
        return spans;
    }

    /// <summary>Every entity id observed so far this run (regardless of how many classes it played).</summary>
    public IReadOnlyList<long> Entities() => new List<long>(_changePoints.Keys);

    /// <summary>Clears every entity's timeline. Called at RUN START only.</summary>
    public void ResetForRun() => _changePoints.Clear();
}
