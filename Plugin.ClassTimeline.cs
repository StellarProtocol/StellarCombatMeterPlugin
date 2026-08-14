using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

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

public sealed partial class Plugin
{
    private readonly ClassSpanTracker _classSpans = new();

    // Attr id carrying professionId on the broadcast attribute stream — matches AttrProfessionId
    // used throughout the plugin (Plugin.SessionSnapshot.cs, CombatLogAssembler.SnapToActor).
    private const int AttrProfessionIdForTimeline = 220;

    /// <summary>Samples every entity the meter tracks this run (<c>_stats.Keys</c> — the same set
    /// <c>SnapshotEntities</c> uses) into <see cref="_classSpans"/>. Self reads the reliable
    /// <c>IPlayerState.Profession</c> (matches AttrRangeTracker's self sampling); every other player
    /// reads attr 220 off the broadcast attribute stream (<c>IEntityDetail.GetAttributes</c>) — the
    /// SAME source <c>CaptureAttributes</c> (EntitySnapshot.cs) already reads for its snapshot, so
    /// it's available for party members too, not just self. Stamped with
    /// <c>ICombatSnapshot.ServerNowMs</c>, which is ASSUMED aligned with <c>CombatEvent.TimestampMs</c>
    /// (the site anchors <c>CombatLogEvent.Ms</c> from it) closely enough for timeline boundaries —
    /// the SAME assumption the shipped elapsed-duration already relies on (<c>ArchivedAtMs</c>=ServerNowMs
    /// vs <c>EnteredAtMs</c>=event-clock, Plugin.History.cs). They are NOT literally one clock
    /// (ServerNowMs is server-broadcast-anchored; TimestampMs is client wall-clock at wire-receive), so a
    /// class-swap boundary lining up with nearby event ms within tolerance is an IN-GAME verification item.
    /// Throttled to ~5 Hz (every other 10 Hz tick, like <c>TickAttrRangeSample</c>) so the per-entity
    /// <c>GetAttributes</c> dict-copy isn't paid for every party member every frame — class swaps are rare,
    /// ~200 ms resolution is ample.</summary>
    private int _classTimelineThrottle;

    // Illusion-Breaking Strength = attr 11440 (AttrSeasonStrength, AttrCatalog.g.cs). Cached per member off
    // the SAME throttled GetAttributes read the class timeline already pays (owner 2026-08-15) so the row
    // display never touches GetAttributes on the render path — the exact anti-pattern this throttle exists
    // to avoid. Bounded by party size; cleared with the run in Clear().
    private const int AttrSeasonStrengthId = 11440;
    private readonly System.Collections.Generic.Dictionary<EntityId, long> _memberSeasonStrength = new();

    private void TickClassTimeline()
    {
        _classTimelineThrottle ^= 1;
        if (_classTimelineThrottle == 0) return;   // every other tick → ~5 Hz
        var self  = _services.CombatSnapshot.LocalEntityId;
        var nowMs = _services.CombatSnapshot.ServerNowMs;

        // Season strength (Illusion-Breaking Strength) for every DISPLAYED party entity — self + the party
        // roster — sampled UNCONDITIONALLY, in OR out of combat (owner: "no need to wait on fight"). Ability
        // Score (GetFightPoint) shows for these regardless of combat, so this must match. Same
        // IEntityDetail.GetAttributes read EntityInspector uses per target (Plugin.Header.cs's AttrOr). The
        // _stats loop below additionally covers any list-mode / open-world combatant not in the party.
        SampleSeasonStrength(self);
        foreach (var m in _services.PartyRoster.Members) SampleSeasonStrength(m.EntityId);

        foreach (var id in _stats.Keys)
        {
            if (!id.IsPlayer) continue;
            // ONE GetAttributes dict-copy per member per throttled tick (unchanged cost) — now yielding BOTH
            // the profession (220) and the Illusion-Breaking Strength (11440).
            var attrs = _services.EntityDetail.GetAttributes(id);
            if (attrs.TryGetValue(AttrSeasonStrengthId, out var ss)) _memberSeasonStrength[id] = ss;
            var prof = id == self ? _services.PlayerState.Profession
                     : attrs.TryGetValue(AttrProfessionIdForTimeline, out var v) ? (int)v : 0;
            if (prof == 0) continue;
            _classSpans.Observe(id.Value, prof, nowMs);
        }
    }

    // Cache attr 11440 for one displayed entity — the EntityInspector read (IEntityDetail.GetAttributes),
    // ~5 Hz, so BuildRowData never touches GetAttributes on the render path. No-op for a non-player or an
    // entity whose attrs have not broadcast (leaves the cache untouched → Ability Score shows alone).
    private void SampleSeasonStrength(EntityId id)
    {
        if (!id.IsPlayer) return;
        if (_services.EntityDetail.GetAttributes(id).TryGetValue(AttrSeasonStrengthId, out var ss))
            _memberSeasonStrength[id] = ss;
    }

    // The row's Illusion-Breaking Strength (0 when not yet sampled / not broadcast) — a cheap dict read for
    // BuildRowData, never GetAttributes on the render path.
    private long SeasonStrengthOf(EntityId id) => _memberSeasonStrength.TryGetValue(id, out var v) ? v : 0;

    /// <summary>Pure: writes each <c>[professionId, startMs, endMs]</c> triple into
    /// <paramref name="snap"/>'s parallel ClassSpan* arrays. Testable without services (mirrors
    /// <c>WriteRangeToSnapshot</c>, Plugin.AttrRange.cs).</summary>
    internal static void WriteClassSpansToSnapshot(EntitySnapshot snap, IReadOnlyList<long[]> spans)
    {
        snap.ClassSpanProf  = new long[spans.Count];
        snap.ClassSpanStart = new long[spans.Count];
        snap.ClassSpanEnd   = new long[spans.Count];
        for (var i = 0; i < spans.Count; i++)
        {
            snap.ClassSpanProf[i]  = spans[i][0];
            snap.ClassSpanStart[i] = spans[i][1];
            snap.ClassSpanEnd[i]   = spans[i][2];
        }
    }

    /// <summary>Distinct professionIds this snapshot's baked class timeline shows the entity playing
    /// during <c>[startMs, endMs]</c>, in first-appearance (chronological) order. Empty when the
    /// snapshot carries no timeline (a single-class archive — the tracker bakes spans only for an
    /// entity that swapped) or when nothing overlaps the window.
    ///
    /// This is what lets an archived row show EVERY class the player actually played in THAT archive
    /// instead of the single frozen professionId (attr 220 at bank time) — which mislabels a
    /// clear-phase archive banked after a swap as the boss class (the LUz6opkvNX bug: a 34s clear
    /// phase read "Frost Mage" though the player was Verdant Oracle for 26 of its 34s). The classSpans
    /// are RUN-anchored and accumulate across archives (<see cref="ClassSpanTracker"/> resets per RUN,
    /// not per archive), so clamping each span to the archive's own <c>[EnteredAtMs, ArchivedAtMs]</c>
    /// window is exactly what stops an earlier archive's span from bleeding into a later one. Pure —
    /// unit-tested in <c>ClassSpanTrackerTests</c>.</summary>
    internal static IReadOnlyList<int> ClassesPlayedInWindow(EntitySnapshot snap, long startMs, long endMs)
    {
        var n = System.Math.Min(snap.ClassSpanProf.Length, System.Math.Min(snap.ClassSpanStart.Length, snap.ClassSpanEnd.Length));
        if (n == 0 || endMs <= startMs) return System.Array.Empty<int>();
        var seen = new HashSet<int>();
        var order = new List<int>();
        for (var i = 0; i < n; i++)
        {
            // Overlap of span i with the archive window — a span entirely before/after contributes none.
            var ovStart = System.Math.Max(snap.ClassSpanStart[i], startMs);
            var ovEnd   = System.Math.Min(snap.ClassSpanEnd[i], endMs);
            if (ovEnd - ovStart <= 0) continue;
            var prof = (int)snap.ClassSpanProf[i];
            if (prof > 0 && seen.Add(prof)) order.Add(prof);   // dedupe a class played across two spans
        }
        return order;
    }

    /// <summary>At archive: bakes EVERY tracked player's professionId timeline into its frozen
    /// EntitySnapshot — self AND party alike (mirrors <c>ApplyAttrRanges</c>'s bake-in pattern,
    /// Plugin.AttrRange.cs). No-op for an entity the tracker never saw a class swap for
    /// (<c>ClassSpanTracker.Spans</c> returns empty — a single-class entity needs no timeline). Call
    /// right after <c>ApplyAttrRanges</c> in <c>BuildHistoryEntry</c> (Plugin.History.cs) — the
    /// tracker is fully accumulated by archive time, same as AttrRangeTracker.
    /// NOTE: mutates the (possibly sticky) EntitySnapshot in place — safe under the same ownership
    /// contract <c>ApplyAttrRanges</c> documents (SnapshotEntities transfers ownership at archive;
    /// ManualArchive Clear()s _entitySnaps right after).</summary>
    private void ApplyClassSpans(EncounterHistoryEntry entry)
    {
        foreach (var (id, snap) in entry.Entities)
        {
            var spans = _classSpans.Spans(id.Value, entry.ArchivedAtMs);
            if (spans.Count > 0) WriteClassSpansToSnapshot(snap, spans);
        }
    }
}
