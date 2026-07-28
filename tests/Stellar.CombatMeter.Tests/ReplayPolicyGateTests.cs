using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Spec tests 4 + 5, and D2's watermark invariant.
///
/// D2: a `manual` replay cell prepares NO doc, so the watermark HOLDS and the samples merge into the
/// next window — exactly the tested suppressed-archive path. Advancing the watermark on local
/// retention instead would leave a GAP in the run's server-side replay, i.e. a P0 clip. Never
/// weaken these.
/// </summary>
public class ReplayPolicyGateTests
{
    // Spec test 4: a manual upload attaches the replay only when the cell is not `off`.
    //
    // NOTE: [Fact] with inline assertions, not [Theory] over the enum — xUnit requires public test
    // methods and a public method may not take an internal parameter type (CS0051). Internal types
    // stay in method BODIES only, as elsewhere in this suite.
    [Fact]
    public void ManualTrigger_AttachesReplayUnlessOff()
    {
        Assert.True(UploadPolicy.Allows(UploadPolicyState.Auto,   UploadTrigger.Manual));
        Assert.True(UploadPolicy.Allows(UploadPolicyState.Manual, UploadTrigger.Manual));
        Assert.False(UploadPolicy.Allows(UploadPolicyState.Off,   UploadTrigger.Manual));
    }

    [Fact]
    public void NoDocPrepared_NeverAdvancesTheWatermark()
    {
        // D2 + Task 1: a `manual`/`off` replay cell yields no doc, so nothing is handed off and the
        // samples stay for the next window (P0: dungeon entry → run end, at-least-once).
        Assert.False(Plugin.ShouldAdvanceWatermark(replayDocPresent: false, summaryFired: true, directUploadHandedOff: false));
    }

    [Fact]
    public void StatsAndReplayAreIndependent_SpecTest5()
    {
        // stats auto + replay off ⇒ the log uploads, with no positions.
        Assert.True(UploadPolicy.Allows(UploadPolicyState.Auto, UploadTrigger.Auto));   // stats
        Assert.False(UploadPolicy.Allows(UploadPolicyState.Off, UploadTrigger.Auto));   // replay
    }
}
