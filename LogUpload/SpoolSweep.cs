using System;
using System.Collections.Generic;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// The startup blob sweep's DECISION, extracted pure so it pins headless (<c>SpoolSweepTests</c>) — the
/// I/O around it lives in <c>Plugin.HistoryStore.SweepUnreferencedSpoolBlobs</c>.
///
/// A <c>spool/*</c> blob is deletable only when NO live retention container references it. The dangerous
/// case is a container the store listed but could not READ (an I/O fault) or could not PARSE: it
/// contributes no references, so treating it as "references nothing" would delete the blobs a retained run
/// still needs — the raw event stream of that run, unrecoverable. Deleting is permanent; keeping is a few
/// megabytes of disk that the next healthy launch collects. So ANY unreadable live container aborts the
/// whole sweep for that launch.
/// </summary>
internal static class SpoolSweep
{
    /// <summary>Which of <paramref name="spoolBlobs"/> may be deleted. <c>SkipReason</c> non-null = the name of
    /// the first live container that could not be read/parsed; <c>ToDelete</c> is then empty and the caller
    /// must delete NOTHING. <paramref name="liveContainers"/> is an <see cref="IEnumerable{T}"/> on purpose:
    /// the caller yields (name, bytes) lazily, so the sweep never holds every container's bytes at once
    /// (measured on the owner's client: 94 containers, ~18 MB compressed).</summary>
    internal static (IReadOnlyList<string> ToDelete, string? SkipReason) Plan(
        IReadOnlyList<string> spoolBlobs,
        IEnumerable<(string Name, byte[]? Bytes)> liveContainers)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, bytes) in liveContainers)
        {
            if (bytes is null || !ReUploadContainer.TryReferencedBlobs(bytes, out var blobs))
                return (Array.Empty<string>(), name);
            foreach (var blob in blobs) referenced.Add(blob);
        }

        var toDelete = new List<string>();
        foreach (var blob in spoolBlobs)
            if (!referenced.Contains(blob)) toDelete.Add(blob);
        return (toDelete, null);
    }
}
