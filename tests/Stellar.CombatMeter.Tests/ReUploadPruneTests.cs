using System.Collections.Generic;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class ReUploadPruneTests
{
    [Fact]
    public void OrphanContainerNames_returns_files_with_no_live_entry()
    {
        var existing = new[] { "replay/1-100.replaydoc", "replay/2-200.replaydoc", "replay/9-999.replaydoc" };
        var live = new List<(long, long)> { (1, 100), (2, 200) };   // 9-999 is orphaned
        var orphans = ReUploadContainer.OrphanContainerNames(existing, live);
        Assert.Equal(new[] { "replay/9-999.replaydoc" }, orphans);
    }

    [Fact]
    public void OrphanContainerNames_ignores_unrelated_names()
    {
        var existing = new[] { "replay/1-100.replaydoc", "notes/x.txt" };
        var orphans = ReUploadContainer.OrphanContainerNames(existing, new List<(long, long)> { (1, 100) });
        Assert.Empty(orphans);
    }

    // Pins the duplicate-runid hazard: the game can reuse a levelUuid across genuinely different
    // runs, so two runs (A, B) can share one levelUuid while differing in archivedAtMs. Pruning
    // must key on the FULL (levelUuid, archivedAtMs) composite — never on levelUuid alone — or
    // deleting run B's entry would also nuke run A's still-live replay container(s).
    [Fact]
    public void Prune_removing_one_runs_entry_keeps_another_runs_container_that_shares_the_levelUuid()
    {
        const long levelUuid = 244376118654664704L;
        const long t1 = 1784604916545L;    // run A, archive 1 — still live
        const long t2 = 1784604940589L;    // run A, archive 2 — still live
        const long t3 = 1784605999999L;    // run B (same levelUuid, later run) — entry deleted

        var existing = new[]
        {
            ReUploadContainer.ContainerName(levelUuid, t1),
            ReUploadContainer.ContainerName(levelUuid, t2),
            ReUploadContainer.ContainerName(levelUuid, t3),
        };
        var live = new List<(long, long)> { (levelUuid, t1), (levelUuid, t2) };   // run B not in history anymore

        var orphans = ReUploadContainer.OrphanContainerNames(existing, live);

        Assert.Equal(new[] { ReUploadContainer.ContainerName(levelUuid, t3) }, orphans);
    }
}
