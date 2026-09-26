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
/// event.</para>
/// <para>BOUNDED. A town crowd raises one SpecChanged per player in view, so at most <see cref="MaxEntities"/> entities
/// are held: a new entity evicts the least-recently-CHANGED entity that is NOT pinned (the pin predicate supplied by the
/// plugin = self, the current party roster, the current combatants — review of e86a595, finding 1). Entities are kept
/// in last-change order, so the victim is found by walking past the (few) pinned ones at the old end. If every held
/// entity is pinned the new one is admitted over the bound rather than dropping anyone who matters. Each entity keeps at
/// most <see cref="MaxPointsPerEntity"/> change points (oldest dropped).</para>
/// Plain data in / out; main-thread only (the combat event stream fires on the main thread).
/// </summary>
internal sealed class SpecSpanTracker
{
    internal const int MaxEntities = 512;
    internal const int MaxPointsPerEntity = 64;

    private sealed class Timeline
    {
        public readonly List<long[]> Points = new(2);
        public LinkedListNode<long> Node = null!;
    }

    private readonly Func<long, bool>? _isPinned;
    private readonly Dictionary<long, Timeline> _timelines = new();
    private readonly LinkedList<long> _byLastChange = new();   // least-recently-changed first

    /// <param name="isPinned">Entities that must never be evicted by the bound (null = none).</param>
    internal SpecSpanTracker(Func<long, bool>? isPinned = null) => _isPinned = isPinned;

    /// <summary>One <c>SpecChanged</c>: a talent-derived non-zero spec opens (or switches) the span; anything else
    /// closes the open span. A repeat of the current state adds no point.</summary>
    public void OnSpecChanged(long entityId, int newSpec, bool fromTalent, long ms)
    {
        var spec = fromTalent && newSpec > 0 ? newSpec : 0;
        if (!_timelines.TryGetValue(entityId, out var tl))
        {
            if (spec == 0) return;                          // nothing open, nothing to close
            tl = Admit(entityId);
        }
        else if (tl.Points.Count > 0 && tl.Points[^1][1] == spec) return;
        AddPoint(tl, ms, spec);
    }

    /// <summary>Run-start seed from <c>ICombatSpec.TryGetTalentSpec</c>: opens a span for an entity the tracker holds
    /// nothing for yet. <paramref name="talentSpec"/> ≤ 0 (no talent spec) seeds nothing.</summary>
    public void Seed(long entityId, int talentSpec, long ms)
    {
        if (talentSpec <= 0 || _timelines.ContainsKey(entityId)) return;
        AddPoint(Admit(entityId), ms, talentSpec);
    }

    /// <summary>Folds <paramref name="entityId"/>'s change points into talent spans, the open one capped at
    /// <paramref name="endMs"/> (the archive instant). A span starting at/after the cap, or of zero length (two change
    /// points in the same ms), is not emitted. Empty when the entity never held a talent spec. Always a fresh list.</summary>
    public IReadOnlyList<long[]> Spans(long entityId, long endMs)
    {
        if (!_timelines.TryGetValue(entityId, out var tl)) return Array.Empty<long[]>();
        var points = tl.Points;
        List<long[]>? spans = null;
        for (var i = 0; i < points.Count; i++)
        {
            var spec = points[i][1];
            var start = points[i][0];
            if (spec == 0 || start >= endMs) continue;
            var end = i + 1 < points.Count ? Math.Min(points[i + 1][0], endMs) : endMs;
            if (end <= start) continue;
            (spans ??= new List<long[]>()).Add(new[] { spec, start, end });
        }
        return spans ?? (IReadOnlyList<long[]>)Array.Empty<long[]>();
    }

    /// <summary>Every entity id currently tracked.</summary>
    public IReadOnlyList<long> Entities() => new List<long>(_timelines.Keys);

    /// <summary>Clears every timeline. Called at RUN START only, immediately followed by the seed pass.</summary>
    public void ResetForRun()
    {
        _timelines.Clear();
        _byLastChange.Clear();
    }

    private void AddPoint(Timeline tl, long ms, int spec)
    {
        if (tl.Points.Count >= MaxPointsPerEntity) tl.Points.RemoveAt(0);
        tl.Points.Add(new[] { ms, (long)spec });
        _byLastChange.Remove(tl.Node);
        _byLastChange.AddLast(tl.Node);                    // most recently changed
    }

    private Timeline Admit(long entityId)
    {
        if (_timelines.Count >= MaxEntities) EvictOne();
        var tl = new Timeline();
        tl.Node = _byLastChange.AddLast(entityId);
        _timelines[entityId] = tl;
        return tl;
    }

    // The least-recently-changed UNPINNED entity. Pinned ones (self + party + combatants, a few dozen at most) are
    // skipped in place, so the walk is bounded by the pinned count, not the set size.
    private void EvictOne()
    {
        for (var node = _byLastChange.First; node is not null; node = node.Next)
        {
            if (_isPinned is not null && _isPinned(node.Value)) continue;
            _timelines.Remove(node.Value);
            _byLastChange.Remove(node);
            return;
        }
    }
}
