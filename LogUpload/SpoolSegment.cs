using System.Collections.Generic;
using System.Threading.Tasks;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>One future upload chunk, already serialized to a gzipped blob. <c>Total</c> for the envelope is the
/// owning track's ref count at upload time.</summary>
internal sealed record SpoolChunkRef(string Track, int Index, long StartMs, long EndMs, int Count, string BlobName);

/// <summary>A rotated segment: everything captured between two archive boundaries. <see cref="Completion"/>
/// completes when every blob write has finished (writes run on the thread pool).</summary>
internal sealed record SpoolSegment(
    string SegmentId,
    IReadOnlyList<SpoolChunkRef> Dmg,
    IReadOnlyList<SpoolChunkRef> Buff,
    bool TruncatedDmg,
    bool TruncatedBuff,
    Task Completion)
{
    internal static readonly SpoolSegment Empty = new("", new SpoolChunkRef[0], new SpoolChunkRef[0], false, false, Task.CompletedTask);
    internal int ChunkCount => Dmg.Count + Buff.Count;
}
