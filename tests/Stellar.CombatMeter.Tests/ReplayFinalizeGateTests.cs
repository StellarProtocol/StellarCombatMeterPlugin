using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// The position replay is ONE continuous capture per dungeon RUN. Mid-run auto archives
// (stage change / boss phase / idle) bank their DAMAGE segment but must NOT truncate the
// replay — doing so split one dungeon into slices, so the uploaded replay was only the final
// slice (shorter than the game's clear time; owner report 2026-07-19 run 381145082998292480).
// ShouldFinalizeReplay decides which archives assemble+upload+reset the whole-run replay:
// only run-TERMINAL ones (manual, scene-leave, wipe, or the kill that ends the run).
public class ReplayFinalizeGateTests
{
    // --- terminal archives finalize the whole-run replay ---

    [Fact]
    public void Manual_finalizes()
        => Assert.True(Plugin.ShouldFinalizeReplay(AutoArchive.ArchiveReason.Manual, isKill: false));

    [Fact]
    public void SceneChange_finalizes()   // leaving the dungeon = run boundary
        => Assert.True(Plugin.ShouldFinalizeReplay(AutoArchive.ArchiveReason.SceneChange, isKill: false));

    [Fact]
    public void Wipe_finalizes()          // run failed/ended
        => Assert.True(Plugin.ShouldFinalizeReplay(AutoArchive.ArchiveReason.Wipe, isKill: false));

    [Fact]
    public void A_kill_stage_archive_finalizes()   // the settlement that ends the run
        => Assert.True(Plugin.ShouldFinalizeReplay(AutoArchive.ArchiveReason.StageChange, isKill: true));

    [Fact]
    public void A_kill_boss_archive_finalizes()
        => Assert.True(Plugin.ShouldFinalizeReplay(AutoArchive.ArchiveReason.BossPhase, isKill: true));

    // --- mid-run NON-terminal archives keep the replay accumulating (the fix) ---

    [Fact]
    public void StageChange_without_kill_does_not_finalize()
        => Assert.False(Plugin.ShouldFinalizeReplay(AutoArchive.ArchiveReason.StageChange, isKill: false));

    [Fact]
    public void BossPhase_without_kill_does_not_finalize()
        => Assert.False(Plugin.ShouldFinalizeReplay(AutoArchive.ArchiveReason.BossPhase, isKill: false));

    [Fact]
    public void Idle_without_kill_does_not_finalize()
        => Assert.False(Plugin.ShouldFinalizeReplay(AutoArchive.ArchiveReason.Idle, isKill: false));
}
