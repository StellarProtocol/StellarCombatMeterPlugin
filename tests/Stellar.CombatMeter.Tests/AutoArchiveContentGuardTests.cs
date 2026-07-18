using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Regression guard for junk history entries. The old eligibility gate (_stats.Count > 0) counted a
// taken-only phantom row (a player who took a hit but dealt nothing — the "0-DMG · 1p") as an
// encounter, so EVERY auto trigger (scene-enter, dungeon flow bump, false-start wipe, boss, idle)
// could bank a "0s · 1p" entry and upload it. The fix requires a real dealt-damage span
// (ComputeDurationMs, which only counts dealt hits, is ~0 for taken-only / single-instant content)
// for any AUTO archive. Only a MANUAL button/hotkey archive is unconditional.
public class AutoArchiveContentGuardTests
{
    // --- every auto trigger with no real span is suppressed ---

    [Fact]
    public void SceneChange_with_zero_span_is_suppressed()
        => Assert.True(Plugin.ShouldSuppressAutoArchive(AutoArchive.ArchiveReason.SceneChange, 0));

    [Fact]
    public void StageChange_with_zero_span_is_suppressed()
        => Assert.True(Plugin.ShouldSuppressAutoArchive(AutoArchive.ArchiveReason.StageChange, 0));

    [Fact]
    public void Wipe_with_zero_span_is_suppressed()   // the "Failed — Retry" 0s false-start
        => Assert.True(Plugin.ShouldSuppressAutoArchive(AutoArchive.ArchiveReason.Wipe, 0));

    [Fact]
    public void BossPhase_with_zero_span_is_suppressed()
        => Assert.True(Plugin.ShouldSuppressAutoArchive(AutoArchive.ArchiveReason.BossPhase, 0));

    [Fact]
    public void Idle_with_zero_span_is_suppressed()
        => Assert.True(Plugin.ShouldSuppressAutoArchive(AutoArchive.ArchiveReason.Idle, 0));

    // --- Manual (user button/hotkey) is NEVER suppressed ---

    [Fact]
    public void Manual_is_never_suppressed_even_at_zero_span()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(AutoArchive.ArchiveReason.Manual, 0));

    // --- real encounters are kept for every auto trigger ---

    [Fact]
    public void SceneChange_with_real_run_span_is_kept()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(AutoArchive.ArchiveReason.SceneChange, 24_000));

    [Fact]
    public void Wipe_after_a_real_fight_is_kept()   // a genuine 90s attempt that wiped
        => Assert.False(Plugin.ShouldSuppressAutoArchive(AutoArchive.ArchiveReason.Wipe, 90_000));

    [Fact]
    public void Real_dungeon_segment_span_is_kept()   // the legit 5s Mech Facility run
        => Assert.False(Plugin.ShouldSuppressAutoArchive(AutoArchive.ArchiveReason.StageChange, 5_000));

    // --- boundary at the floor ---

    [Fact]
    public void Span_just_below_floor_is_suppressed()
        => Assert.True(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.SceneChange, Plugin.MinAutoArchiveMs - 1));

    [Fact]
    public void Span_at_floor_is_kept()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.SceneChange, Plugin.MinAutoArchiveMs));
}
