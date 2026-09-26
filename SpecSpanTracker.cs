using System;
using System.Collections.Generic;

namespace Stellar.CombatMeter;

/// <summary>
/// Pure per-entity TALENT-spec timeline (spec upload design 2026-09-26 item 1; sibling of
/// <see cref="ClassSpanTracker"/>). Fed EVENT-DRIVEN from <c>CombatEvent.SpecChanged</c> (framework ≥ 2.11.0) — no
/// polling — and folded into <c>[specId, startMs, endMs]</c> spans at archive time. A span is open only while the
/// framework reports the spec as talent-derived (<c>FromTalent</c>, the same meaning as
/// <c>ICombatSpec.TryGetTalentSpec</c> returning true): owner ruling "we should send spec to server if we are able to
/// probe it correctly" — a cast-inferred period is a guess and closes the open span without opening one.
/// <para>Change points are <c>[ms, spec]</c> with spec 0 meaning "no talent spec from here". Keyed by
/// <c>EntityId.Value</c>; players only (the caller filters). Reset at RUN START only (like ClassSpanTracker) and then
/// re-seeded from <c>TryGetTalentSpec</c> (<see cref="Seed"/>), because a player already in view at run start raises no
/// event. Bounded at <see cref="MaxEntities"/> — a town crowd raises one SpecChanged per player in view — by dropping
/// the OLDEST-SEEN entity (O(1)); the run-start seed pass re-derives current state from the framework anyway.</para>
/// Plain data in / out; main-thread only (the combat event stream fires on the main thread).
/// </summary>
internal sealed class SpecSpanTracker
{
    internal const int MaxEntities = 512;

    private readonly Dictionary<long, List<long[]>> _points = new();
    private readonly Queue<long> _firstSeen = new();   // entity ids in first-seen order (eviction order)

    /// <summary>One <c>SpecChanged</c>: a talent-derived non-zero spec opens (or switches) the span; anything else
    /// closes the open span. A repeat of the current state adds no point.</summary>
    public void OnSpecChanged(long entityId, int newSpec, bool fromTalent, long ms)
    {
        var spec = fromTalent && newSpec > 0 ? newSpec : 0;
        if (!_points.TryGetValue(entityId, out var points))
        {
            if (spec == 0) return;                          // nothing open, nothing to close
            points = Admit(entityId);
        }
        if (points.Count > 0 && points[^1][1] == spec) return;
        points.Add(new[] { ms, (long)spec });
    }

    /// <summary>Run-start seed from <c>ICombatSpec.TryGetTalentSpec</c>: opens a span for an entity the tracker holds
    /// nothing for yet. <paramref name="talentSpec"/> ≤ 0 (no talent spec) seeds nothing.</summary>
    public void Seed(long entityId, int talentSpec, long ms)
    {
        if (talentSpec <= 0 || _points.ContainsKey(entityId)) return;
        Admit(entityId).Add(new[] { ms, (long)talentSpec });
    }

    /// <summary>Folds <paramref name="entityId"/>'s change points into talent spans, the open one capped at
    /// <paramref name="endMs"/> (the archive instant). A span starting at/after the cap is not emitted. Empty when the
    /// entity never held a talent spec. Always a fresh list — the caller may keep it.</summary>
    public IReadOnlyList<long[]> Spans(long entityId, long endMs)
    {
        if (!_points.TryGetValue(entityId, out var points)) return Array.Empty<long[]>();
        List<long[]>? spans = null;
        for (var i = 0; i < points.Count; i++)
        {
            var spec = points[i][1];
            var start = points[i][0];
            if (spec == 0 || start >= endMs) continue;
            var end = i + 1 < points.Count ? Math.Min(points[i + 1][0], endMs) : endMs;
            (spans ??= new List<long[]>()).Add(new[] { spec, start, end });
        }
        return spans ?? (IReadOnlyList<long[]>)Array.Empty<long[]>();
    }

    /// <summary>Every entity id currently tracked.</summary>
    public IReadOnlyList<long> Entities() => new List<long>(_points.Keys);

    /// <summary>Clears every timeline. Called at RUN START only, immediately followed by the seed pass.</summary>
    public void ResetForRun()
    {
        _points.Clear();
        _firstSeen.Clear();
    }

    private List<long[]> Admit(long entityId)
    {
        if (_points.Count >= MaxEntities && _firstSeen.Count > 0) _points.Remove(_firstSeen.Dequeue());
        var points = new List<long[]>(2);
        _points[entityId] = points;
        _firstSeen.Enqueue(entityId);
        return points;
    }
}
