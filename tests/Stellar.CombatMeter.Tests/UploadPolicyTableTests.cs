using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>Spec test 2 — migration from each legacy pref combination seeds the expected eight cells.</summary>
public class UploadPolicyTableTests
{
    [Fact]
    public void AllAuto_SeedsEveryCellAuto()
    {
        var t = UploadPolicyTable.AllAuto();
        foreach (var kind in UploadPolicyTable.Kinds)
        foreach (var artifact in UploadPolicyTable.Artifacts)
            Assert.Equal(UploadPolicyState.Auto, t[kind, artifact]);
    }

    [Fact]
    public void DefaultTable_IsAllAuto_SoAFreshInstallBehavesExactlyAsBefore()
    {
        var t = new UploadPolicyTable();
        foreach (var kind in UploadPolicyTable.Kinds)
        foreach (var artifact in UploadPolicyTable.Artifacts)
            Assert.Equal(UploadPolicyState.Auto, t[kind, artifact]);
    }

    // legacy autoUpload=true  → all four *.stats auto
    // legacy autoUpload=false → all four *.stats MANUAL (off would remove the hand-push it had)
    // legacy uploadReplay=true  → all four *.replay auto
    // legacy uploadReplay=false → all four *.replay OFF (no separate manual replay action exists)
    //
    // NOTE: asserted via a private helper rather than [Theory]/[InlineData] over the enums. xUnit
    // requires public test methods, and a public method may not take an internal parameter type
    // (CS0051) — UploadPolicyState is internal. Internal types therefore appear only in method
    // BODIES, matching how this suite already handles internal enums such as ArchiveReason.
    [Fact]
    public void Migrate_BothLegacyPrefsOn_SeedsEveryCellAuto()
        => AssertMigrationSeeds(true, true, UploadPolicyState.Auto, UploadPolicyState.Auto);

    [Fact]
    public void Migrate_AutoUploadOff_SeedsStatsManual_PreservingTheHandPush()
        => AssertMigrationSeeds(false, true, UploadPolicyState.Manual, UploadPolicyState.Auto);

    [Fact]
    public void Migrate_ReplayOff_SeedsReplayOff_ThereIsNoManualReplayAction()
        => AssertMigrationSeeds(true, false, UploadPolicyState.Auto, UploadPolicyState.Off);

    [Fact]
    public void Migrate_BothLegacyPrefsOff_SeedsStatsManualAndReplayOff()
        => AssertMigrationSeeds(false, false, UploadPolicyState.Manual, UploadPolicyState.Off);

    private static void AssertMigrationSeeds(
        bool legacyAutoUpload, bool legacyUploadReplay,
        UploadPolicyState expectedStats, UploadPolicyState expectedReplay)
    {
        var t = UploadPolicyTable.Migrate(legacyAutoUpload, legacyUploadReplay);
        foreach (var kind in UploadPolicyTable.Kinds)
        {
            Assert.Equal(expectedStats,  t[kind, UploadArtifact.Stats]);
            Assert.Equal(expectedReplay, t[kind, UploadArtifact.Replay]);
        }
    }

    [Fact]
    public void Indexer_CellsAreIndependent()
    {
        var t = UploadPolicyTable.AllAuto();
        t[ContentKind.WorldBoss, UploadArtifact.Stats] = UploadPolicyState.Manual;
        t[ContentKind.Other,     UploadArtifact.Replay] = UploadPolicyState.Off;

        Assert.Equal(UploadPolicyState.Manual, t[ContentKind.WorldBoss, UploadArtifact.Stats]);
        // Its sibling artifact and every other kind are untouched.
        Assert.Equal(UploadPolicyState.Auto, t[ContentKind.WorldBoss, UploadArtifact.Replay]);
        Assert.Equal(UploadPolicyState.Auto, t[ContentKind.Dungeon,   UploadArtifact.Stats]);
        Assert.Equal(UploadPolicyState.Off,  t[ContentKind.Other,     UploadArtifact.Replay]);
        Assert.Equal(UploadPolicyState.Auto, t[ContentKind.Other,     UploadArtifact.Stats]);
    }

    [Fact]
    public void KindsAndArtifacts_CoverTheFullEightCellGrid()
    {
        Assert.Equal(4, UploadPolicyTable.Kinds.Length);
        Assert.Equal(2, UploadPolicyTable.Artifacts.Length);
    }
}
