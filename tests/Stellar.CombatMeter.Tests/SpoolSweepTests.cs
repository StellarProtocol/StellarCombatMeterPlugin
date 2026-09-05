using System;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// The startup sweep's DECISION (Plugin.HistoryStore.SweepUnreferencedSpoolBlobs): which leftover
/// <c>spool/*</c> blobs a launch may delete. The rule that matters is the failure mode — a live
/// container the store could not read (I/O fault) or could not parse contributes NO references, so
/// treating it as "references nothing" would delete blobs a retained run still needs. Losing them is
/// permanent; keeping them costs disk only, so ANY unreadable live container skips the whole sweep.
/// </summary>
public sealed class SpoolSweepTests
{
    static byte[] Container(params string[] blobs)
    {
        var refs = new SpoolChunkRef[blobs.Length];
        for (var i = 0; i < blobs.Length; i++) refs[i] = new SpoolChunkRef("dmg", i, 1, 2, 3, blobs[i]);
        return ReUploadContainer.Serialize(new ReUploadPayload(
            ReUploadContainer.Version, "sea", 7, "log-1", "{\"s\":1}", Array.Empty<string>(), null, refs));
    }

    [Fact]
    public void All_containers_readable_deletes_only_the_unreferenced_blobs()
    {
        var (toDelete, skip) = SpoolSweep.Plan(
            new[] { "spool/a.gz", "spool/b.gz", "spool/orphan.gz" },
            new (string, byte[]?)[]
            {
                ("replay/1-1.replaydoc", Container("spool/a.gz")),
                ("replay/2-2.replaydoc", Container("spool/b.gz")),
            });

        Assert.Null(skip);
        Assert.Equal(new[] { "spool/orphan.gz" }, toDelete);
    }

    [Fact]
    public void An_unreadable_container_skips_the_whole_sweep()
    {
        var (toDelete, skip) = SpoolSweep.Plan(
            new[] { "spool/a.gz", "spool/orphan.gz" },
            new (string, byte[]?)[]
            {
                ("replay/1-1.replaydoc", Container("spool/a.gz")),
                ("replay/2-2.replaydoc", null),          // List() saw the file; Read() faulted
            });

        Assert.Equal("replay/2-2.replaydoc", skip);
        Assert.Empty(toDelete);
    }

    [Fact]
    public void An_unparseable_container_skips_the_whole_sweep()
    {
        var (toDelete, skip) = SpoolSweep.Plan(
            new[] { "spool/a.gz", "spool/orphan.gz" },
            new (string, byte[]?)[]
            {
                ("replay/1-1.replaydoc", new byte[] { 0, 1, 2, 3 }),   // not gzip / not our format
                ("replay/2-2.replaydoc", Container("spool/a.gz")),
            });

        Assert.Equal("replay/1-1.replaydoc", skip);
        Assert.Empty(toDelete);
    }
}
