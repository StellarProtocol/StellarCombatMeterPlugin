using System.Collections.Generic;
using System.Threading.Tasks;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>One future upload chunk, already serialized to a gzipped blob. <c>Total</c> for the envelope is the
/// owning track's ref count at upload time.</summary>
internal sealed record SpoolChunkRef(string Track, int Index, long StartMs, long EndMs, int Count, string BlobName);

/// <summary>Per-segment counters carried alongside a rotated segment's chunk refs — folded into one nested record
/// (was two trailing positional <c>int</c> params directly on <see cref="SpoolSegment"/>'s constructor) so that
/// constructor stays under the STELLAR0003 parameter gate. <paramref name="WriteFaults"/> is the sum of all four
/// tracks' <see cref="SpoolTrack.WriteFaults"/> AT ROTATE TIME — writes may still be in flight when a track is
/// sealed, so this is "faults so far", not a final count; a fault landing after Rotate is still safely swallowed
/// (never surfaced here), only unseen by this particular number. <paramref name="CastRows"/> is the segment's own
/// <see cref="EventSpool.CastRows"/> count, read by <see cref="EventSpool.Rotate"/> before <c>StartFresh</c>
/// zeroes it — rides the segment so BOTH archive paths (upload and retain-without-upload) can print it in the ONE
/// per-archive outcome line (<c>Plugin.LogUpload.Outcome.cs</c>'s <c>LogSegmentOutcome</c>) without either
/// duplicating the read or gating it on which path fired. <paramref name="GameEventRows"/> is the segment's own
/// <see cref="EventSpool.GameEventRows"/> count — incremented ONLY on a real <c>CombatEvent</c> (a converted
/// dmg/skill row, or a LIVE <see cref="Stellar.Abstractions.Domain.CombatEvent.BuffChanged"/> row), NEVER on a
/// cast row or a buff/sheet KEYFRAME row — see <see cref="SpoolSegment.HasGameEvents"/>, the archive decision
/// this backs (final review C1).</summary>
internal sealed record SpoolCounts(int WriteFaults, int CastRows, int GameEventRows)
{
    /// <summary>All-zero — the value <see cref="SpoolSegment.Empty"/> and <see cref="SpoolSegment.EmptyTruncated"/>
    /// carry (no spool ran, so nothing to count).</summary>
    internal static readonly SpoolCounts Empty = new(0, 0, 0);
}

/// <summary>A rotated segment: everything captured between two archive boundaries, in FOUR tracks —
/// <paramref name="Dmg"/>, <paramref name="Buff"/> and <paramref name="Sheet"/> upload,
/// <paramref name="BuffRejected"/> is disk-only (rows <see cref="BuffUploadFilter"/> rejects: captured because
/// capture is unconditional, never sent). <see cref="Completion"/> completes when every blob write has finished
/// (writes run on the thread pool). <paramref name="Counts"/> carries the per-segment counters (write faults,
/// cast rows, game-event rows) — see <see cref="SpoolCounts"/> for what each means and why they are folded into
/// one nested record instead of trailing positional params.</summary>
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
    SpoolCounts Counts)
{
    private static SpoolChunkRef[] None => new SpoolChunkRef[0];

    internal static readonly SpoolSegment Empty =
        new("", None, None, None, None, false, false, false, false, Task.CompletedTask, SpoolCounts.Empty);

    /// <summary>No chunks, but flagged truncated: the manual re-upload of a PRE-spool archive, which has no
    /// retained event stream at all — the summary must say so rather than claim a complete (empty) one.</summary>
    internal static readonly SpoolSegment EmptyTruncated =
        new("", None, None, None, None, true, false, false, false, Task.CompletedTask, SpoolCounts.Empty);

    /// <summary>Uploadable chunks: dmg + buff + sheet. The disk-only buffx track is excluded. Drives the
    /// "is there anything to POST" decision (<see cref="ChunkUploader.UploadSegmentFireAndForget"/>) and the
    /// "n chunk(s)" info lines, so it deliberately excludes the disk-only track — a segment carrying nothing
    /// but rejected buff rows has nothing to send. NOT the archive decision: use
    /// <see cref="HasGameEvents"/> for that.</summary>
    internal int ChunkCount => Dmg.Count + Buff.Count + Sheet.Count;

    /// <summary>The ARCHIVE-decision gate: true iff <see cref="SpoolCounts.GameEventRows"/> is non-zero — a REAL
    /// game event (a converted dmg/skill row, or a live buff change) was captured this segment. Deliberately NOT
    /// <c>Dmg.Count &gt; 0 || Buff.Count &gt; 0</c>: both tracks also carry capture-channel rows (cast rows in
    /// Dmg; buff/sheet KEYFRAME rows in Buff) that must never force an archive/upload, so a cast- or
    /// keyframe-only segment has non-zero chunk counts but <c>HasGameEvents == false</c>. Every segment gets a
    /// sheet keyframe (and, from the 2nd segment of a dungeon onward, the live buff set almost always keyframes a
    /// buff too), so gating on chunk counts instead would make the "No events captured — skipping auto-upload"
    /// retain-only branch in <c>Plugin.LogUpload.AssembleAndUpload</c> unreachable and start UPLOADING the
    /// no-damage tail archives the owner had purged server-side (kill-board P2 ruling 2026-09-02). Owner-visible
    /// behavior change — keep this the only gate for that branch.</summary>
    internal bool HasGameEvents => Counts.GameEventRows > 0;

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
