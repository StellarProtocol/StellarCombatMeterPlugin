using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Content-based archive suppression (owner ruling 2026-07-19, run sea/192556848602152960).
// The old rule binned ANY auto archive with durMs < 3000 — that destroyed a 1.35s / 4-player kill
// tail INCLUDING the settlement (log: "suppressed reason=stage stats=4 durMs=1350"), losing the
// kill. The new rule suppresses only genuine junk: an auto archive that carries NO fresh run result
// AND is either all-zero (no dps/hps/taken on any row) or a lone single-participant instant hit.
// A MANUAL button/hotkey archive is NEVER suppressed. Real short combat (2+ participants, or any
// non-zero content beyond a single instant hit) SAVES even without a settlement (owner: "even 1-2
// secs after archive it still should save").
public class AutoArchiveContentGuardTests
{
    // ── Owner calibration cases (all from the owner's client, 2026-07-19) ──────────────────────

    // a. reason=stage stats=1 durMs=0 — a stray single-participant instant hit, no fresh
    //    settlement → SUPPRESS (the "0s · 1p" junk the guard was built for).
    [Fact]
    public void Case_a_single_participant_instant_no_result_is_suppressed()
        => Assert.True(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.StageChange, carriesFreshResult: false, allRowsZero: false,
            statsCount: 1, durationMs: 0));

    // b. reason=stage stats=4 durMs=1350, a FRESH kill settlement arrived → SAVE (the destroyed
    //    kill tail — the whole reason this ruling exists).
    [Fact]
    public void Case_b_short_kill_tail_with_fresh_result_is_saved()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.StageChange, carriesFreshResult: true, allRowsZero: false,
            statsCount: 4, durationMs: 1350));

    // c. same shape but NO fresh settlement — short residual real combat after a manual archive →
    //    SAVE (owner: "even 1-2 secs after archive it still should save"). Saved on CONTENT alone.
    [Fact]
    public void Case_c_short_real_combat_no_result_is_saved()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.StageChange, carriesFreshResult: false, allRowsZero: false,
            statsCount: 4, durationMs: 1350));

    // d. every stat row is 0 damage AND 0 healing AND 0 taken → SUPPRESS (owner: "shouldn't save
    //    empty into history").
    [Fact]
    public void Case_d_all_rows_zero_is_suppressed()
        => Assert.True(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.StageChange, carriesFreshResult: false, allRowsZero: true,
            statsCount: 4, durationMs: 1350));

    // ── Manual (user button/hotkey) is NEVER suppressed — whatever the content ─────────────────

    [Fact]
    public void Manual_is_never_suppressed_even_all_zero_single_instant()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.Manual, carriesFreshResult: false, allRowsZero: true,
            statsCount: 1, durationMs: 0));

    [Fact]
    public void Manual_is_never_suppressed_with_content()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.Manual, carriesFreshResult: false, allRowsZero: false,
            statsCount: 4, durationMs: 30_000));

    // ── A fresh run result force-keeps ANY auto archive, however tiny ──────────────────────────
    // Generalises case b: the dungeon-finish slice can be a single participant / zero span when the
    // user manually archived most of the fight moments earlier — losing the result is the worst case.
    // (ArchiveReason is internal, so one Fact per reason rather than a public [Theory] parameter.)

    private static bool SuppressTinyWithResult(AutoArchive.ArchiveReason reason)
        => Plugin.ShouldSuppressAutoArchive(
            reason, carriesFreshResult: true, allRowsZero: false, statsCount: 1, durationMs: 0);

    [Fact] public void Fresh_result_keeps_tiny_stage()  => Assert.False(SuppressTinyWithResult(AutoArchive.ArchiveReason.StageChange));
    [Fact] public void Fresh_result_keeps_tiny_boss()   => Assert.False(SuppressTinyWithResult(AutoArchive.ArchiveReason.BossPhase));
    [Fact] public void Fresh_result_keeps_tiny_wipe()   => Assert.False(SuppressTinyWithResult(AutoArchive.ArchiveReason.Wipe));
    [Fact] public void Fresh_result_keeps_tiny_scene()  => Assert.False(SuppressTinyWithResult(AutoArchive.ArchiveReason.SceneChange));
    [Fact] public void Fresh_result_keeps_tiny_idle()   => Assert.False(SuppressTinyWithResult(AutoArchive.ArchiveReason.Idle));

    // ── Genuine junk is suppressed on every auto trigger ───────────────────────────────────────

    private static bool SuppressAllZero(AutoArchive.ArchiveReason reason)
        => Plugin.ShouldSuppressAutoArchive(
            reason, carriesFreshResult: false, allRowsZero: true, statsCount: 4, durationMs: 1350);

    [Fact] public void All_zero_junk_suppressed_stage() => Assert.True(SuppressAllZero(AutoArchive.ArchiveReason.StageChange));
    [Fact] public void All_zero_junk_suppressed_boss()  => Assert.True(SuppressAllZero(AutoArchive.ArchiveReason.BossPhase));
    [Fact] public void All_zero_junk_suppressed_wipe()  => Assert.True(SuppressAllZero(AutoArchive.ArchiveReason.Wipe));
    [Fact] public void All_zero_junk_suppressed_scene() => Assert.True(SuppressAllZero(AutoArchive.ArchiveReason.SceneChange));
    [Fact] public void All_zero_junk_suppressed_idle()  => Assert.True(SuppressAllZero(AutoArchive.ArchiveReason.Idle));

    private static bool SuppressSingleInstant(AutoArchive.ArchiveReason reason)
        => Plugin.ShouldSuppressAutoArchive(
            reason, carriesFreshResult: false, allRowsZero: false, statsCount: 1, durationMs: 0);

    [Fact] public void Single_instant_junk_suppressed_stage() => Assert.True(SuppressSingleInstant(AutoArchive.ArchiveReason.StageChange));
    [Fact] public void Single_instant_junk_suppressed_boss()  => Assert.True(SuppressSingleInstant(AutoArchive.ArchiveReason.BossPhase));
    [Fact] public void Single_instant_junk_suppressed_wipe()  => Assert.True(SuppressSingleInstant(AutoArchive.ArchiveReason.Wipe));
    [Fact] public void Single_instant_junk_suppressed_scene() => Assert.True(SuppressSingleInstant(AutoArchive.ArchiveReason.SceneChange));
    [Fact] public void Single_instant_junk_suppressed_idle()  => Assert.True(SuppressSingleInstant(AutoArchive.ArchiveReason.Idle));

    // ── Real content saves without a result ────────────────────────────────────────────────────

    [Fact]
    public void Full_length_run_is_saved()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.SceneChange, carriesFreshResult: false, allRowsZero: false,
            statsCount: 5, durationMs: 90_000));

    // A lone participant that fought for real (a solo run) — not an instant hit — saves. The
    // single-participant clause only bins a sub-500ms lone row, not a genuine solo fight.
    [Fact]
    public void Solo_real_fight_is_saved()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.SceneChange, carriesFreshResult: false, allRowsZero: false,
            statsCount: 1, durationMs: 45_000));

    // ── Single-participant trivial-tail boundary (500 ms) ──────────────────────────────────────

    [Fact]
    public void Single_participant_just_below_trivial_floor_is_suppressed()
        => Assert.True(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.StageChange, carriesFreshResult: false, allRowsZero: false,
            statsCount: 1, durationMs: 499));

    [Fact]
    public void Single_participant_at_trivial_floor_is_saved()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.StageChange, carriesFreshResult: false, allRowsZero: false,
            statsCount: 1, durationMs: 500));
}
