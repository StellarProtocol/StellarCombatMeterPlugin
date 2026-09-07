using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Uncapped, disk-backed replacement for the two in-memory event rings it retired. Converts each
/// CombatEvent on the main thread and appends it to one of FOUR tracks (<see cref="SpoolTrack"/>):
/// <c>dmg</c> (skill + damage), <c>buff</c> (buff rows <see cref="BuffUploadFilter"/> admits), <c>sheet</c>
/// (the local player's damage-relevant attributes, spec § 6.1) — all three uploaded — and <c>buffx</c> (buff
/// rows the filter rejects, disk-only). Capture is UNCONDITIONAL — every converted row reaches disk; the
/// filter only ROUTES, deciding which track is uploaded (<c>dmg</c>/<c>buff</c>/<c>sheet</c>) and which stays
/// local (<c>buffx</c>, never posted anywhere — spec § 4.2 / § 9 invariant 2, owner's capture-is-default-on
/// doctrine). <see cref="Rotate"/> is called exactly where the old ring was flushed (an archive boundary)
/// and never decides anything itself; <see cref="Discard"/> replaces <c>Clear()</c>. Not thread-safe:
/// Add/Rotate/Discard are main-thread only, like the ring they replaced.
/// </summary>
internal sealed class EventSpool
{
    private readonly IPluginDataStore _store;
    private readonly SummonOwnerMap _owners;
    private readonly Func<IReadOnlyDictionary<int, long>>? _readSelfSheet;
    private readonly int _chunkEvents;
    private static int _seq;
    private string _segmentId = NewSegmentId();
    private SpoolTrack _dmg, _buff, _sheet, _buffx;
    private bool _sheetKeyframeWritten;
    private readonly LiveBuffSet _liveBuffs = new();
    private bool _buffKeyframeWritten;

    internal EventSpool(IPluginDataStore store, SummonOwnerMap? owners = null,
                         Func<IReadOnlyDictionary<int, long>>? readSelfSheet = null, int chunkEvents = EventChunker.ChunkEvents)
    {
        _store = store; _owners = owners ?? new SummonOwnerMap(); _readSelfSheet = readSelfSheet; _chunkEvents = chunkEvents;
        _dmg = new SpoolTrack(SpoolCodec.TrackDmg, _segmentId, store, chunkEvents);
        _buff = new SpoolTrack(SpoolCodec.TrackBuff, _segmentId, store, chunkEvents);
        _sheet = new SpoolTrack(SpoolCodec.TrackSheet, _segmentId, store, chunkEvents);
        _buffx = new SpoolTrack(SpoolCodec.TrackBuffRejected, _segmentId, store, chunkEvents);
    }

    /// <summary>Events whose CombatEvent case has no wire mapping since the last Rotate (forward-compat
    /// net). Read it BEFORE <see cref="Rotate"/> — rotating starts a fresh segment and zeroes the counter.
    /// EntityAttributesChanged is handled before the converter and never counts here.</summary>
    internal int SkippedUnknownEvents { get; private set; }

    internal int CastRows { get; private set; }

    /// <summary>Real <c>CombatEvent</c> rows captured since the last Rotate — the archive-decision signal that
    /// rides the rotated segment as <see cref="SpoolSegment.HasGameEvents"/> (via <see cref="SpoolCounts.GameEventRows"/>).
    /// Incremented ONLY for a converted dmg/skill row (<see cref="Add"/>'s final <c>_dmg.Add(wire)</c> fallthrough)
    /// or a LIVE <see cref="CombatEvent.BuffChanged"/> row — NEVER for a cast row (<see cref="CastRows"/>) or a
    /// buff/sheet KEYFRAME row (<see cref="AddBuffKeyframe"/>/<see cref="AddSheetKeyframe"/>), both of which are
    /// capture-channel rows that must never force an archive/upload (final review C1).</summary>
    internal int GameEventRows { get; private set; }

    /// <summary>True until this segment has its keyframe. Plugin.SheetCapture's tick asks this once per
    /// tick.</summary>
    internal bool NeedsSheetKeyframe => !_sheetKeyframeWritten;

    /// <summary>True until this segment has its buff keyframe. Plugin.BuffKeyframe's tick asks this once per
    /// tick; the first live buff row of a segment also satisfies it (see <see cref="Add"/>).</summary>
    internal bool NeedsBuffKeyframe => !_buffKeyframeWritten;

    /// <summary>Writes the segment's ONE keyframe from the live sheet (no-op after the first, or without a
    /// reader, or when the sheet carries no tracked attr yet — in that last case <see cref="NeedsSheetKeyframe"/>
    /// stays true and a later call retries). <paramref name="ms"/> is in the wire-receive clock domain.
    /// <para>ENVELOPE-WINDOW CONTRACT (do not "fix" by sorting): within a sheet chunk the keyframe is first by
    /// ARRAY order, but <c>ms</c> is NOT guaranteed monotonic across it. A tick-written keyframe is stamped with
    /// <c>UtcNow</c> on the update thread while delta rows carry the earlier network-thread receive stamp, so the
    /// first row's <c>ms</c> can exceed the second's. The VALUES stay consistent either way: the attribute sink is
    /// written at packet receive and the keyframe reads that live sink, so a consumer that sorts by <c>ms</c>
    /// (the worker's <c>sheetSteps</c>) reconstructs the same step function as array order would. The chunk ref's
    /// window is therefore built as min/max over the batch, not first/last — see
    /// <see cref="SpoolTrack"/>.<c>SealOpen</c> and <see cref="SheetEvent"/>.</para></summary>
    internal void AddSheetKeyframe(long ms)
    {
        if (_sheetKeyframeWritten || _readSelfSheet is null) return;
        var kf = SheetRowBuilder.Keyframe(ms, _readSelfSheet());
        if (kf is null) return;
        _sheet.Add(kf);
        _sheetKeyframeWritten = true;
    }

    /// <summary>Once per segment: re-states every live buff as a `kf` applied row through the send filter. Set the flag
    /// FIRST — an empty live set is still "keyframe done" for this segment.
    /// <para>Same envelope-window contract as <see cref="AddSheetKeyframe"/>: a tick-written keyframe (Plugin.BuffKeyframe's
    /// <c>TickBuffKeyframe</c>) carries an update-thread <c>UtcNow</c> stamp, so its <c>ms</c> is not guaranteed to be ≤ a
    /// subsequent live row's network-thread receive stamp — see <see cref="SpoolTrack"/>.<c>SealOpen</c>'s MIN/MAX note.</para></summary>
    internal void AddBuffKeyframe(long ms, EntityId self)
    {
        if (_buffKeyframeWritten) return;
        _buffKeyframeWritten = true;
        foreach (var live in _liveBuffs.Keyframe(ms)) RouteBuff(live, self);
    }

    internal void ClearLiveBuffs() => _liveBuffs.Clear();

    private void RouteBuff(LiveBuff live, EntityId self)
        => (BuffUploadFilter.ShouldUpload(_owners.OwnerOf(live.Firer), live.Target, self) ? _buff : _buffx).Add(live.Row);

    internal void Add(CombatEvent evt, EntityId self)
    {
        if (evt is CombatEvent.EntityAttributesChanged ac)
        {
            // Phase 2: ANY player's cast (AttrSkillId write) → a `skill` row in the dmg track, self and AOI
            // teammates alike (decision D1). Before the self-only sheet gate, which is unchanged.
            var cast = CastRowBuilder.Project(ac);
            if (cast is not null) { _dmg.Add(cast); CastRows++; }
            if (ac.TargetId != self) return;                    // teammates' AOI attrs: not this track
            var row = SheetRowBuilder.Project(ac);
            if (row is null) return;                            // no tracked attr in this packet
            AddSheetKeyframe(ac.TimestampMs);                   // keyframe precedes the first delta row
            _sheet.Add(row);
            return;
        }

        var wire = CombatLogEventConverter.Convert(evt, _owners);
        if (wire is null) { SkippedUnknownEvents++; return; }
        if (evt is CombatEvent.BuffChanged b)
        {
            AddBuffKeyframe(b.TimestampMs, self);            // snapshot of what was live BEFORE this change (phase 2)
            var live = new LiveBuff((BuffEvent)wire, b.FirerId, b.TargetId);
            _liveBuffs.Apply(live);
            RouteBuff(live, self);                           // ROUTE, never drop (spec § 6.8 owner resolution inside)
            GameEventRows++;                                 // a REAL live buff change — archive-decision gate
            return;
        }
        _dmg.Add(wire);
        GameEventRows++;                                     // a REAL converted dmg/skill row — archive-decision gate
    }

    /// <summary>Seal all four tracks into a segment and start a fresh one. Main thread; O(1) apart from the
    /// last batch hand-off (its serialize+gzip+write runs on the thread pool, awaited via the segment's
    /// <see cref="SpoolSegment.Completion"/> — the main thread NEVER awaits it).</summary>
    internal SpoolSegment Rotate()
    {
        var (dmg, tDmg, cDmg, fDmg) = _dmg.Seal();
        var (buff, tBuff, cBuff, fBuff) = _buff.Seal();
        var (sheet, tSheet, cSheet, fSheet) = _sheet.Seal();
        var (buffx, tBuffx, cBuffx, fBuffx) = _buffx.Seal();
        var castRows = CastRows;             // read BEFORE StartFresh — it zeroes the counter
        var gameEventRows = GameEventRows;    // ditto
        var counts = new SpoolCounts(fDmg + fBuff + fSheet + fBuffx, castRows, gameEventRows);
        var seg = new SpoolSegment(_segmentId, dmg, buff, sheet, buffx, tDmg, tBuff, tSheet, tBuffx,
                                   Task.WhenAll(cDmg, cBuff, cSheet, cBuffx), counts);
        StartFresh();
        return seg;
    }

    /// <summary>Drop the current segment: its blobs are deleted after their writes finish. Replaces the
    /// ring's Clear(). Fire-and-forget — the deletion task is rooted by its own continuation, so nothing is
    /// retained here (a per-call list would grow for the life of the process).</summary>
    internal void Discard() => _ = DiscardAsync();

    /// <summary>Awaitable form of <see cref="Discard"/> — tests await it to observe the blobs gone.</summary>
    internal Task DiscardAsync()
    {
        var (dmg, _, cDmg, _) = _dmg.Seal();
        var (buff, _, cBuff, _) = _buff.Seal();
        var (sheet, _, cSheet, _) = _sheet.Seal();
        var (buffx, _, cBuffx, _) = _buffx.Seal();
        var store = _store;
        var names = new List<string>(dmg.Count + buff.Count + sheet.Count + buffx.Count);
        foreach (var r in dmg) names.Add(r.BlobName);
        foreach (var r in buff) names.Add(r.BlobName);
        foreach (var r in sheet) names.Add(r.BlobName);
        foreach (var r in buffx) names.Add(r.BlobName);
        StartFresh();
        return Task.WhenAll(cDmg, cBuff, cSheet, cBuffx)
                   .ContinueWith(_ => { foreach (var n in names) store.Delete(n); }, TaskScheduler.Default);
    }

    private void StartFresh()
    {
        _segmentId = NewSegmentId();
        _dmg = new SpoolTrack(SpoolCodec.TrackDmg, _segmentId, _store, _chunkEvents);
        _buff = new SpoolTrack(SpoolCodec.TrackBuff, _segmentId, _store, _chunkEvents);
        _sheet = new SpoolTrack(SpoolCodec.TrackSheet, _segmentId, _store, _chunkEvents);
        _buffx = new SpoolTrack(SpoolCodec.TrackBuffRejected, _segmentId, _store, _chunkEvents);
        _sheetKeyframeWritten = false;
        _buffKeyframeWritten = false;   // the live buff set itself persists across segments — that is the point
        SkippedUnknownEvents = 0;
        CastRows = 0;
        GameEventRows = 0;
    }

    private static string NewSegmentId()
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-" +
           Interlocked.Increment(ref _seq).ToString(CultureInfo.InvariantCulture);
}
