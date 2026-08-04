using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// D1 regression pin (bug introduced by 9d03cfe, delta-window): the run-stats upload must NOT be
/// gated on a non-null replay doc. Before this fix Plugin.History.cs returned early when
/// PrepareReplayDoc yielded null — which happens when replay upload is off, when the run has no
/// level id, or when the position window is empty — so turning replay off silently stopped stats
/// uploads too. NEVER weaken these: they encode the stats/replay independence spec test #5 needs.
/// </summary>
public class FinalizeUploadDecoupleTests
{
    [Fact]
    public void NoReplayDoc_DoesNotAdvanceWatermark()
    {
        // No doc was serialized ⇒ nothing was handed off ⇒ the watermark must hold so the samples
        // merge into the next window (P0: entry-to-end coverage, at-least-once).
        Assert.False(Plugin.ShouldAdvanceWatermark(replayDocPresent: false, summaryFired: true,  directUploadHandedOff: false));
        Assert.False(Plugin.ShouldAdvanceWatermark(replayDocPresent: false, summaryFired: false, directUploadHandedOff: false));
    }

    [Fact]
    public void ReplayDoc_AdvancesOnlyOnAGenuineHandOff()
    {
        // summaryFired ⇒ the summary callback owns and uploads the doc.
        Assert.True(Plugin.ShouldAdvanceWatermark(replayDocPresent: true, summaryFired: true, directUploadHandedOff: false));
        // summary refused ⇒ the direct positions upload decides.
        Assert.True(Plugin.ShouldAdvanceWatermark(replayDocPresent: true, summaryFired: false, directUploadHandedOff: true));
        Assert.False(Plugin.ShouldAdvanceWatermark(replayDocPresent: true, summaryFired: false, directUploadHandedOff: false));
    }
}
