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

    // -------------------------------------------------------------------------------------------
    // The archive-time gate itself (Plugin.ReplayAutoUploadAllowed's pure static overload).
    //
    // The [Fact]s above only restate UploadPolicy.Allows primitives — they pass no matter what the
    // production gate does with them. These call the real seam, so the three axes it composes (which
    // ARTIFACT cell it reads, which TRIGGER it asks about, and that the kind comes from the ARCHIVED
    // ENTRY) are each pinned by a test that goes red when that axis is broken.
    // -------------------------------------------------------------------------------------------

    // 1151 = dungeon, 13021 = raid, 7152 = worldboss; every other map id resolves `other`.
    private const string KindMapPayload =
        "{\"version\":1,\"kinds\":{\"dungeon\":[1151],\"raid\":[13021],\"worldboss\":[7152],\"other\":[]}}";

    private static ContentKindMap KindMap()
    {
        Assert.True(ContentKindMap.TryParse(KindMapPayload, out var map));
        return map;
    }

    /// <summary>A table with every one of the eight cells explicitly <c>off</c> — the default ctor
    /// yields all-<c>auto</c> (enum 0), so a test that means "nothing enabled" must say so.</summary>
    private static UploadPolicyTable AllOff()
    {
        var t = new UploadPolicyTable();
        foreach (var kind in UploadPolicyTable.Kinds)
        foreach (var artifact in UploadPolicyTable.Artifacts)
            t[kind, artifact] = UploadPolicyState.Off;
        return t;
    }

    [Fact]
    public void ReplayAutoUploadAllowed_AutoCell_PermitsTheArchiveTimeUpload()
    {
        var policy = AllOff();
        policy[ContentKind.Dungeon, UploadArtifact.Replay] = UploadPolicyState.Auto;

        var entry = new Plugin.EncounterHistoryEntry { SceneName = "1151" };   // dungeon
        Assert.True(Plugin.ReplayAutoUploadAllowed(KindMap(), policy, entry));
    }

    // TRIGGER axis. `manual` means "only when the user pushes it" — the archive-time path asks with
    // UploadTrigger.Auto and must be REFUSED. Swapping that Auto for Manual in the production seam
    // would silently auto-upload every `manual` cell's replay; this is the test that catches it.
    [Fact]
    public void ReplayAutoUploadAllowed_ManualCell_RefusesTheArchiveTimeUpload()
    {
        var policy = AllOff();
        policy[ContentKind.Dungeon, UploadArtifact.Replay] = UploadPolicyState.Manual;

        var entry = new Plugin.EncounterHistoryEntry { SceneName = "1151" };   // dungeon
        Assert.False(Plugin.ReplayAutoUploadAllowed(KindMap(), policy, entry));
    }

    [Fact]
    public void ReplayAutoUploadAllowed_OffCell_RefusesTheArchiveTimeUpload()
    {
        var entry = new Plugin.EncounterHistoryEntry { SceneName = "1151" };   // dungeon
        Assert.False(Plugin.ReplayAutoUploadAllowed(KindMap(), AllOff(), entry));
    }

    // ARTIFACT axis. Stats `auto` must not let the replay through when the same kind's replay cell is
    // `manual`. Reading UploadArtifact.Stats in the seam would return true here.
    [Fact]
    public void ReplayAutoUploadAllowed_ReadsTheReplayCell_NotTheStatsCell()
    {
        var policy = AllOff();
        policy[ContentKind.Dungeon, UploadArtifact.Stats]  = UploadPolicyState.Auto;
        policy[ContentKind.Dungeon, UploadArtifact.Replay] = UploadPolicyState.Manual;

        var entry = new Plugin.EncounterHistoryEntry { SceneName = "1151" };   // dungeon
        Assert.False(Plugin.ReplayAutoUploadAllowed(KindMap(), policy, entry));
    }

    // ENTRY-DERIVED-KIND axis. Two entries, same map + same policy table, different stored SceneName ⇒
    // different verdicts. A gate that resolved a single (live) kind would answer both identically.
    [Fact]
    public void ReplayAutoUploadAllowed_KindComesFromTheArchivedEntrysSceneName()
    {
        var map = KindMap();
        var policy = AllOff();
        policy[ContentKind.Raid,    UploadArtifact.Replay] = UploadPolicyState.Auto;
        policy[ContentKind.Dungeon, UploadArtifact.Replay] = UploadPolicyState.Off;

        var raidEntry    = new Plugin.EncounterHistoryEntry { SceneName = "13021" };
        var dungeonEntry = new Plugin.EncounterHistoryEntry { SceneName = "1151" };

        Assert.True(Plugin.ReplayAutoUploadAllowed(map, policy, raidEntry));
        Assert.False(Plugin.ReplayAutoUploadAllowed(map, policy, dungeonEntry));
    }

    // -------------------------------------------------------------------------------------------
    // Capture gate (Plugin.AnyReplayCellEnabled) — the P0 fix.
    //
    // Capture is kind-INDEPENDENT while the upload gate above is entry-derived. That asymmetry is
    // deliberate: the two resolve the kind from different sources (live scene vs the archived entry's
    // stored scene) and a sample that was never taken cannot be recovered later. A raid lobby /
    // dungeon approach is usually an UNLISTED map id ⇒ `other`, and the sample buffer is deliberately
    // kept across that hop, so gating capture on the live kind with `other.replay = off` would drop the
    // walk-in and CLIP THE START of the raid's replay — the exact P0 the owner escalated. Never narrow
    // this to the current kind.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void AnyReplayCellEnabled_AllCellsOff_DoesNotCapture()
        => Assert.False(Plugin.AnyReplayCellEnabled(AllOff()));

    [Fact]
    public void AnyReplayCellEnabled_AllAuto_Captures()
        => Assert.True(Plugin.AnyReplayCellEnabled(UploadPolicyTable.AllAuto()));

    // int index rather than a ContentKind parameter: a public test method may not take an internal
    // parameter type (CS0051), so the kind is resolved inside the body.
    [Theory]
    [InlineData(0)]   // dungeon
    [InlineData(1)]   // raid
    [InlineData(2)]   // worldboss
    [InlineData(3)]   // other
    public void AnyReplayCellEnabled_ExactlyOneKindEnabled_StillCaptures(int kindIndex)
    {
        var policy = AllOff();
        policy[UploadPolicyTable.Kinds[kindIndex], UploadArtifact.Replay] = UploadPolicyState.Auto;

        // Whichever single kind it is — including `other`, and including a kind the player is NOT
        // currently standing in — capture must run, or that kind's walk-in is unrecoverable.
        Assert.True(Plugin.AnyReplayCellEnabled(policy));
    }

    [Fact]
    public void AnyReplayCellEnabled_OneKindOnManual_StillCaptures()
    {
        // `manual` needs samples on hand for the user's hand-push, so it captures like `auto`.
        var policy = AllOff();
        policy[ContentKind.Raid, UploadArtifact.Replay] = UploadPolicyState.Manual;
        Assert.True(Plugin.AnyReplayCellEnabled(policy));
    }

    // ARTIFACT axis for the capture gate: every STATS cell on `auto` while every REPLAY cell is `off`
    // must NOT capture positions. A seam that scanned UploadArtifact.Stats would return true here.
    [Fact]
    public void AnyReplayCellEnabled_OnlyStatsCellsEnabled_DoesNotCapture()
    {
        var policy = AllOff();
        foreach (var kind in UploadPolicyTable.Kinds)
            policy[kind, UploadArtifact.Stats] = UploadPolicyState.Auto;

        Assert.False(Plugin.AnyReplayCellEnabled(policy));
    }
}
