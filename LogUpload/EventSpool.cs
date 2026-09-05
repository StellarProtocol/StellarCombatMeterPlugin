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
/// CombatEvent on the main thread and appends it to one of two tracks (<see cref="SpoolTrack"/>): <c>dmg</c>
/// (skill + damage) and <c>buff</c> (only rows <see cref="BuffUploadFilter"/> admits — capture is
/// unconditional, the FILTER is the send gate). <see cref="Rotate"/> is called exactly where the old ring was
/// flushed (an archive boundary) and never decides anything itself; <see cref="Discard"/> replaces
/// <c>Clear()</c>. Not thread-safe: Add/Rotate/Discard are main-thread only, like the ring they replaced.
/// </summary>
internal sealed class EventSpool
{
    private readonly IPluginDataStore _store;
    private readonly int _chunkEvents;
    private static int _seq;
    private string _segmentId = NewSegmentId();
    private SpoolTrack _dmg, _buff;

    internal EventSpool(IPluginDataStore store, int chunkEvents = EventChunker.ChunkEvents)
    {
        _store = store; _chunkEvents = chunkEvents;
        _dmg = new SpoolTrack("dmg", _segmentId, store, chunkEvents);
        _buff = new SpoolTrack("buff", _segmentId, store, chunkEvents);
    }

    /// <summary>Events whose CombatEvent case has no wire mapping since the last Rotate (forward-compat net).
    /// Read it BEFORE <see cref="Rotate"/> — rotating starts a fresh segment and zeroes the counter.</summary>
    internal int SkippedUnknownEvents { get; private set; }

    internal void Add(CombatEvent evt, EntityId self)
    {
        var wire = CombatLogEventConverter.Convert(evt);
        if (wire is null) { SkippedUnknownEvents++; return; }
        if (evt is CombatEvent.BuffChanged b)
        {
            if (BuffUploadFilter.ShouldUpload(b.FirerId, b.TargetId, self)) _buff.Add(wire);
            return;
        }
        _dmg.Add(wire);
    }

    /// <summary>Seal both tracks into a segment and start a fresh one. Main thread; O(1) apart from the last
    /// batch hand-off (its serialize+gzip+write runs on the thread pool, awaited via the segment's
    /// <see cref="SpoolSegment.Completion"/> — the main thread NEVER awaits it).</summary>
    internal SpoolSegment Rotate()
    {
        var (dmg, tDmg, cDmg) = _dmg.Seal();
        var (buff, tBuff, cBuff) = _buff.Seal();
        var seg = new SpoolSegment(_segmentId, dmg, buff, tDmg, tBuff, Task.WhenAll(cDmg, cBuff));
        StartFresh();
        return seg;
    }

    /// <summary>Drop the current segment: its blobs are deleted after their writes finish. Replaces the
    /// ring's Clear(). Fire-and-forget — the deletion task is rooted by its own continuation, so nothing
    /// is retained here (a per-call list would grow for the life of the process).</summary>
    internal void Discard() => _ = DiscardAsync();

    /// <summary>Awaitable form of <see cref="Discard"/> — tests await it to observe the blobs gone.</summary>
    internal Task DiscardAsync()
    {
        var (dmg, _, cDmg) = _dmg.Seal();
        var (buff, _, cBuff) = _buff.Seal();
        var store = _store;
        var names = new List<string>(dmg.Count + buff.Count);
        foreach (var r in dmg) names.Add(r.BlobName);
        foreach (var r in buff) names.Add(r.BlobName);
        StartFresh();
        return Task.WhenAll(cDmg, cBuff).ContinueWith(_ => { foreach (var n in names) store.Delete(n); }, TaskScheduler.Default);
    }

    private void StartFresh()
    {
        _segmentId = NewSegmentId();
        _dmg = new SpoolTrack("dmg", _segmentId, _store, _chunkEvents);
        _buff = new SpoolTrack("buff", _segmentId, _store, _chunkEvents);
        SkippedUnknownEvents = 0;
    }

    private static string NewSegmentId()
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-" +
           Interlocked.Increment(ref _seq).ToString(CultureInfo.InvariantCulture);
}
