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
}
