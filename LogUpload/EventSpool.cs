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

    internal EventSpool(IPluginDataStore store, SummonOwnerMap? owners = null,
                         Func<IReadOnlyDictionary<int, long>>? readSelfSheet = null, int chunkEvents = EventChunker.ChunkEvents)
    {
        _store = store; _owners = owners ?? new SummonOwnerMap(); _readSelfSheet = readSelfSheet; _chunkEvents = chunkEvents;
        _dmg = new SpoolTrack(SpoolCodec.TrackDmg, _segmentId, store, chunkEvents);
        _buff = new SpoolTrack(SpoolCodec.TrackBuff, _segmentId, store, chunkEvents);
        _sheet = new SpoolTrack(SpoolCodec.TrackSheet, _segmentId, store, chunkEvents);
        _buffx = new SpoolTrack(SpoolCodec.TrackBuffRejected, _segmentId, store, chunkEvents);
    }

    /// <summary>Events whose CombatEvent case has no wire mapping since the last Rotate (forward-compat net). Read it BEFORE <see cref="Rotate"/> — rotating starts a fresh segment and zeroes the counter. EntityAttributesChanged is handled before the converter and never counts here.</summary>
    internal int SkippedUnknownEvents { get; private set; }

    /// <summary>True until this segment has its keyframe. Plugin.SheetCapture's tick asks this once per tick.</summary>
    internal bool NeedsSheetKeyframe => !_sheetKeyframeWritten;

    /// <summary>Writes the segment's ONE keyframe from the live sheet (no-op after the first, or without a reader,
    /// or when the sheet carries no tracked attr yet). <paramref name="ms"/> is in the wire-receive clock domain.</summary>
    internal void AddSheetKeyframe(long ms)
    {
        if (_sheetKeyframeWritten || _readSelfSheet is null) return;
        var kf = SheetRowBuilder.Keyframe(ms, _readSelfSheet());
        if (kf is null) return;
        _sheet.Add(kf);
        _sheetKeyframeWritten = true;
    }

    internal void Add(CombatEvent evt, EntityId self)
    {
        if (evt is CombatEvent.EntityAttributesChanged ac)
        {
            if (ac.EntityId != self) return;                    // teammates' AOI attrs: not this track
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
            // ROUTE, never drop: the filter picks the uploaded track or the disk-only one. The firer is
            // resolved to its OWNER first (spec § 6.8) so a player's summon counts as that player.
            (BuffUploadFilter.ShouldUpload(_owners.OwnerOf(b.FirerId), b.TargetId, self) ? _buff : _buffx).Add(wire);
            return;
        }
        _dmg.Add(wire);
    }

    /// <summary>Seal all four tracks into a segment and start a fresh one. Main thread; O(1) apart from the last batch hand-off (its serialize+gzip+write runs on the thread pool, awaited via the segment's <see cref="SpoolSegment.Completion"/> — the main thread NEVER awaits it).</summary>
    internal SpoolSegment Rotate()
    {
        var (dmg, tDmg, cDmg, fDmg) = _dmg.Seal();
        var (buff, tBuff, cBuff, fBuff) = _buff.Seal();
        var (sheet, tSheet, cSheet, fSheet) = _sheet.Seal();
        var (buffx, tBuffx, cBuffx, fBuffx) = _buffx.Seal();
        var seg = new SpoolSegment(_segmentId, dmg, buff, sheet, buffx, tDmg, tBuff, tSheet, tBuffx,
                                   Task.WhenAll(cDmg, cBuff, cSheet, cBuffx), fDmg + fBuff + fSheet + fBuffx);
        StartFresh();
        return seg;
    }

    /// <summary>Drop the current segment: its blobs are deleted after their writes finish. Replaces the ring's Clear(). Fire-and-forget — the deletion task is rooted by its own continuation, so nothing is retained here (a per-call list would grow for the life of the process).</summary>
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
        SkippedUnknownEvents = 0;
    }

    private static string NewSegmentId()
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-" +
           Interlocked.Increment(ref _seq).ToString(CultureInfo.InvariantCulture);
}
