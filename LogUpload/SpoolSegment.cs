using System.Collections.Generic;
using System.Threading.Tasks;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>One future upload chunk, already serialized to a gzipped blob. <c>Total</c> for the envelope is the
/// owning track's ref count at upload time.</summary>
internal sealed record SpoolChunkRef(string Track, int Index, long StartMs, long EndMs, int Count, string BlobName);

/// <summary>A rotated segment: everything captured between two archive boundaries, in FOUR tracks —
/// <paramref name="Dmg"/>, <paramref name="Buff"/> and <paramref name="Sheet"/> upload,
/// <paramref name="BuffRejected"/> is disk-only (rows <see cref="BuffUploadFilter"/> rejects: captured because
/// capture is unconditional, never sent).
/// <see cref="Completion"/> completes when every blob write has finished (writes run on the thread pool).
/// <paramref name="WriteFaults"/> is the sum of all four tracks' <see cref="SpoolTrack.WriteFaults"/> AT
/// ROTATE TIME — writes may still be in flight when a track is sealed, so this is "faults so far", not a
/// final count; a fault landing after Rotate is still safely swallowed (never surfaced here), only unseen
/// by this particular number. Defaulted so every pre-existing positional construction (<see cref="Empty"/>,
/// <see cref="EmptyTruncated"/>, and any fixture that builds a SpoolSegment directly) keeps compiling.</summary>
internal sealed record SpoolSegment(
    string SegmentId,
    IReadOnlyList<SpoolChunkRef> Dmg,
    IReadOnlyList<SpoolChunkRef> Buff,
    IReadOnlyList<SpoolChunkRef> Sheet,
    IReadOnlyList<SpoolChunkRef> BuffRejected,
    bool TruncatedDmg,
    bool TruncatedBuff,
    bool TruncatedSheet,
    bool TruncatedBuffRejected,
    Task Completion,
    int WriteFaults = 0)
{
    private static SpoolChunkRef[] None => new SpoolChunkRef[0];

    internal static readonly SpoolSegment Empty = new("", None, None, None, None, false, false, false, false, Task.CompletedTask);

    /// <summary>No chunks, but flagged truncated: the manual re-upload of a PRE-spool archive, which has no
    /// retained event stream at all — the summary must say so rather than claim a complete (empty) one.</summary>
    internal static readonly SpoolSegment EmptyTruncated = new("", None, None, None, None, true, false, false, false, Task.CompletedTask);

    /// <summary>Uploadable chunks: dmg + buff + sheet. The disk-only buffx track is excluded. Drives the
    /// zero-event early return and the "n chunk(s)" info lines, so it deliberately excludes the disk-only
    /// track — a segment carrying nothing but rejected buff rows has nothing to send and must still take the
    /// retain-and-return path.</summary>
    internal int ChunkCount => Dmg.Count + Buff.Count + Sheet.Count;

    /// <summary>Every chunk this segment put ON DISK, uploadable or not — what the retention container must
    /// reference so the startup sweep keeps (and eventually deletes) all four tracks with the container.</summary>
    internal int DiskChunkCount => ChunkCount + BuffRejected.Count;

    /// <summary>All four tracks' refs, in track order — the retention container's <c>chunkRefs</c>. Includes
    /// the disk-only track ON PURPOSE (see <see cref="DiskChunkCount"/>); the upload legs drop it again via
    /// <see cref="ChunkUploader.SplitUploadable"/>.</summary>
    internal IReadOnlyList<SpoolChunkRef> AllChunkRefs()
    {
        var all = new List<SpoolChunkRef>(DiskChunkCount);
        all.AddRange(Dmg); all.AddRange(Buff); all.AddRange(Sheet); all.AddRange(BuffRejected);
        return all;
    }
}
