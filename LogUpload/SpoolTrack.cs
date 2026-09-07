using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// One spool track — FOUR exist (<c>dmg</c> = skill+damage; <c>buff</c> = uploaded buff rows; <c>sheet</c> =
/// the local player's damage-relevant attributes, one keyframe + change-only deltas per segment, spec § 6.1;
/// <c>buffx</c> = filter-rejected buff rows, disk only). Main thread appends to an open batch; at
/// <paramref name="chunkEvents"/> the batch is handed to the thread pool to be serialized (EventsJsonWriter),
/// gzipped and written as ONE blob = one future upload chunk. Beyond <paramref name="maxChunks"/> (the
/// server's per-track chunk cap) batches are dropped and the track is flagged truncated — the only cap
/// left, and it is honest. Not thread-safe: Add/Seal are main-thread only (same as the old ring).
/// </summary>
internal sealed class SpoolTrack
{
    /// <summary>Matches the worker's MAX_EVENT_CHUNKS AFTER the buff-track server release (128; the
    /// currently deployed worker still enforces 33 — see the release checklist: worker before plugin).</summary>
    internal const int MaxChunksPerTrack = 128;

    private readonly string _track, _segmentId;
    private readonly IPluginDataStore _store;
    private readonly int _chunkEvents, _maxChunks;
    private List<CombatLogEvent> _open;
    private readonly List<SpoolChunkRef> _refs = new();
    private readonly List<Task> _writes = new();
    private bool _truncated;
    private int _writeFaults;

    /// <summary>Count of blob writes that faulted (serialize/gzip/store.Write threw), so far — writes may
    /// still be in flight when this is read, and a fault after that read would not be reflected. Never faults
    /// <see cref="Seal"/>'s <c>completion</c> task (see the catch in <see cref="SealOpen"/>); this is the
    /// only surviving signal that a chunk silently failed to reach disk.</summary>
    internal int WriteFaults => Volatile.Read(ref _writeFaults);

    internal SpoolTrack(string track, string segmentId, IPluginDataStore store,
                        int chunkEvents = EventChunker.ChunkEvents, int maxChunks = MaxChunksPerTrack)
    {
        _track = track; _segmentId = segmentId; _store = store;
        _chunkEvents = chunkEvents; _maxChunks = maxChunks;
        _open = new List<CombatLogEvent>(chunkEvents);
    }

    internal int OpenCount => _open.Count;

    internal void Add(CombatLogEvent e)
    {
        _open.Add(e);
        if (_open.Count >= _chunkEvents) SealOpen();
    }

    internal (IReadOnlyList<SpoolChunkRef> refs, bool truncated, Task completion, int faultsSoFar) Seal()
    {
        if (_open.Count > 0) SealOpen();
        return (_refs.ToArray(), _truncated, Task.WhenAll(_writes.ToArray()), WriteFaults);
    }

    private void SealOpen()
    {
        var batch = _open;
        _open = new List<CombatLogEvent>(_chunkEvents);
        if (_refs.Count >= _maxChunks) { _truncated = true; return; }
        var index = _refs.Count;
        var name = SpoolCodec.BlobName(_segmentId, _track, index);
        // The envelope window is MIN/MAX over the batch, not first/last: array order is not ms order on the
        // `sheet` OR `buff` tracks. A tick-written keyframe (sheet: TickSheetKeyframe; buff: TickBuffKeyframe)
        // carries an update-thread UtcNow stamp while the delta rows behind it carry the earlier network-thread
        // receive stamp, so batch[0].Ms can EXCEED batch[1].Ms and first/last would emit an inverted
        // (StartMs > EndMs) window. O(n) over ≤ 4,000 rows, once per chunk. The row ORDER on disk is
        // deliberately left alone — see EventSpool.AddSheetKeyframe's and AddBuffKeyframe's contract notes.
        long startMs = batch[0].Ms, endMs = batch[0].Ms;
        for (var i = 1; i < batch.Count; i++)
        {
            var ms = batch[i].Ms;
            if (ms < startMs) startMs = ms;
            if (ms > endMs) endMs = ms;
        }
        _refs.Add(new SpoolChunkRef(_track, index, startMs, endMs, batch.Count, name));
        var store = _store;
        _writes.Add(Task.Run(() =>
        {
            // A serialization/gzip/write fault must NEVER fault this task. The segment's Completion is
            // Task.WhenAll over these writes and the uploader AWAITS it — a faulted Completion would abort
            // the whole segment's upload, losing the chunks that DID land. A blob that failed to land is
            // detected at upload time as store.Read(name) == null (ChunkUploader.PostRefsAsync warns and
            // skips just that chunk). Pinned by SpoolTrackTests.A_failed_blob_write_never_faults_the_completion.
            // The fault is still COUNTED (never silent) so a caller can warn — see WriteFaults.
            try { store.Write(name, SpoolCodec.Gzip(EventsJsonWriter.Write(batch))); }
            catch { Interlocked.Increment(ref _writeFaults); }
        }));
    }
}
