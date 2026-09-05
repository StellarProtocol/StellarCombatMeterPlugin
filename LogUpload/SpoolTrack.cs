using System.Collections.Generic;
using System.Threading.Tasks;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// One spool track (<c>dmg</c> = skill+damage; <c>buff</c> = uploaded buff rows; <c>buffx</c> = filter-rejected
/// buff rows, disk only). Main thread appends to an open batch; at
/// <paramref name="chunkEvents"/> the batch is handed to the thread pool to be serialized (EventsJsonWriter),
/// gzipped and written as ONE blob = one future upload chunk. Beyond <paramref name="maxChunks"/> (the
/// server's per-track chunk cap) batches are dropped and the track is flagged truncated — the only cap
/// left, and it is honest. Not thread-safe: Add/Seal are main-thread only (same as the old ring).
/// </summary>
internal sealed class SpoolTrack
{
    /// <summary>Mirrors the worker's MAX_EVENT_CHUNKS (128 × 4,000 = 512k events per track per segment).</summary>
    internal const int MaxChunksPerTrack = 128;

    private readonly string _track, _segmentId;
    private readonly IPluginDataStore _store;
    private readonly int _chunkEvents, _maxChunks;
    private List<CombatLogEvent> _open;
    private readonly List<SpoolChunkRef> _refs = new();
    private readonly List<Task> _writes = new();
    private bool _truncated;

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

    internal (IReadOnlyList<SpoolChunkRef> refs, bool truncated, Task completion) Seal()
    {
        if (_open.Count > 0) SealOpen();
        return (_refs.ToArray(), _truncated, Task.WhenAll(_writes.ToArray()));
    }

    private void SealOpen()
    {
        var batch = _open;
        _open = new List<CombatLogEvent>(_chunkEvents);
        if (_refs.Count >= _maxChunks) { _truncated = true; return; }
        var index = _refs.Count;
        var name = SpoolCodec.BlobName(_segmentId, _track, index);
        _refs.Add(new SpoolChunkRef(_track, index, batch[0].Ms, batch[batch.Count - 1].Ms, batch.Count, name));
        var store = _store;
        _writes.Add(Task.Run(() =>
        {
            // A serialization/gzip/write fault must NEVER fault this task. The segment's Completion is
            // Task.WhenAll over these writes and the uploader AWAITS it — a faulted Completion would abort
            // the whole segment's upload, losing the chunks that DID land. A blob that failed to land is
            // detected at upload time as store.Read(name) == null (ChunkUploader.PostRefsAsync warns and
            // skips just that chunk). Pinned by SpoolTrackTests.A_failed_blob_write_never_faults_the_completion.
            try { store.Write(name, SpoolCodec.Gzip(EventsJsonWriter.Write(batch))); }
            catch { }
        }));
    }
}
