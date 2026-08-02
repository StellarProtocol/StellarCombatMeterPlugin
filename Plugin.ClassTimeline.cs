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
    /// <c>ICombatSnapshot.ServerNowMs</c> — the SAME server-epoch-ms clock <c>CombatEvent.TimestampMs</c>
    /// is stamped with (confirmed from the framework contracts: <c>ICombatSnapshot.ServerNowMs</c>'s
    /// doc reads "Latest server epoch (ms)…"; <c>CombatEvent.TimestampMs</c>'s reads "Server epoch
    /// timestamp… in milliseconds" — one clock), so a baked span's ms lines up with the site's
    /// <c>CombatLogEvent.Ms</c>-anchored timeline. Called from <c>TickLoadoutCapture</c> at its
    /// existing ~10 Hz cadence (Plugin.LoadoutCapture.cs) — class swaps are rare, a ~100 ms
    /// resolution is ample.</summary>
    private void TickClassTimeline()
    {
        var self  = _services.CombatSnapshot.LocalEntityId;
        var nowMs = _services.CombatSnapshot.ServerNowMs;
        foreach (var id in _stats.Keys)
        {
            if (!id.IsPlayer) continue;
            var prof = id == self ? _services.PlayerState.Profession : ReadBroadcastProfession(id);
            if (prof == 0) continue;
            _classSpans.Observe(id.Value, prof, nowMs);
        }
    }

    private int ReadBroadcastProfession(EntityId id)
        => _services.EntityDetail.GetAttributes(id).TryGetValue(AttrProfessionIdForTimeline, out var v) ? (int)v : 0;

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
