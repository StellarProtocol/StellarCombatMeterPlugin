using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Content-based archive suppression (owner ruling 2026-07-19, NARROWED — supersedes the earlier
// trivial-tail rule). Owner verbatim: "junk = when nothing happen DPS=0, HPS=0, TAKEN=0. and even I
// do nothing and all other player keep having DPS/HPS/TAKEN update it's not junk too." So an auto
// archive is suppressed iff it carries NO fresh run result AND every stat row is all-zero. ANY
// nonzero row — even a single participant with a lone instant hit and a zero span — is real activity
// and BANKS as its own entry (the old `statsCount <= 1 && durationMs < 500` trivial-tail clause is
// GONE; "1 player · 0s" with a nonzero value now saves, the owner's explicit choice). A MANUAL
// button/hotkey archive is NEVER suppressed. A fresh kill/settlement force-keeps ANY auto archive.
public class AutoArchiveContentGuardTests
{
    // ── Owner calibration cases (2026-07-19) ───────────────────────────────────────────────────

    // a. reason=stage stats=1 durMs=0 WITH a nonzero row, no fresh settlement → now BANKS (flipped by
    //    the narrowed ruling: any activity is not junk, even a lone single-participant instant hit).
    [Fact]
    public void Case_a_single_participant_instant_with_activity_now_banks()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.StageChange, carriesFreshResult: false, allRowsZero: false));

    // b. a FRESH kill settlement arrived → SAVE (the destroyed kill tail — the whole reason the guard
    //    force-keeps a result).
    [Fact]
    public void Case_b_short_kill_tail_with_fresh_result_is_saved()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.StageChange, carriesFreshResult: true, allRowsZero: false));

    // c. short residual real combat after a manual archive, no fresh settlement → SAVE on CONTENT
    //    alone (owner: "even 1-2 secs after archive it still should save").
    [Fact]
    public void Case_c_short_real_combat_no_result_is_saved()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.StageChange, carriesFreshResult: false, allRowsZero: false));

    // d. every stat row is 0 damage AND 0 healing AND 0 taken → SUPPRESS (owner: "when nothing happen
    //    DPS=0, HPS=0, TAKEN=0"). This is now the ONLY junk shape.
    [Fact]
    public void Case_d_all_rows_zero_is_suppressed()
        => Assert.True(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.StageChange, carriesFreshResult: false, allRowsZero: true));

    // ── Manual (user button/hotkey) is NEVER suppressed — whatever the content ─────────────────

    [Fact]
    public void Manual_is_never_suppressed_even_all_zero()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.Manual, carriesFreshResult: false, allRowsZero: true));

    [Fact]
    public void Manual_is_never_suppressed_with_content()
        => Assert.False(Plugin.ShouldSuppressAutoArchive(
            AutoArchive.ArchiveReason.Manual, carriesFreshResult: false, allRowsZero: false));

    // ── A fresh run result force-keeps ANY auto archive, even an all-zero one ───────────────────
    // (ArchiveReason is internal, so one Fact per reason rather than a public [Theory] parameter.)

    private static bool SuppressAllZeroWithResult(AutoArchive.ArchiveReason reason)
        => Plugin.ShouldSuppressAutoArchive(reason, carriesFreshResult: true, allRowsZero: true);

    [Fact] public void Fresh_result_keeps_allzero_stage() => Assert.False(SuppressAllZeroWithResult(AutoArchive.ArchiveReason.StageChange));
    [Fact] public void Fresh_result_keeps_allzero_boss()  => Assert.False(SuppressAllZeroWithResult(AutoArchive.ArchiveReason.BossPhase));
    [Fact] public void Fresh_result_keeps_allzero_wipe()  => Assert.False(SuppressAllZeroWithResult(AutoArchive.ArchiveReason.Wipe));
    [Fact] public void Fresh_result_keeps_allzero_scene() => Assert.False(SuppressAllZeroWithResult(AutoArchive.ArchiveReason.SceneChange));
    [Fact] public void Fresh_result_keeps_allzero_idle()  => Assert.False(SuppressAllZeroWithResult(AutoArchive.ArchiveReason.Idle));

    // ── All-zero junk is suppressed on every auto trigger ──────────────────────────────────────

    private static bool SuppressAllZero(AutoArchive.ArchiveReason reason)
        => Plugin.ShouldSuppressAutoArchive(reason, carriesFreshResult: false, allRowsZero: true);

    [Fact] public void All_zero_junk_suppressed_stage() => Assert.True(SuppressAllZero(AutoArchive.ArchiveReason.StageChange));
    [Fact] public void All_zero_junk_suppressed_boss()  => Assert.True(SuppressAllZero(AutoArchive.ArchiveReason.BossPhase));
    [Fact] public void All_zero_junk_suppressed_wipe()  => Assert.True(SuppressAllZero(AutoArchive.ArchiveReason.Wipe));
    [Fact] public void All_zero_junk_suppressed_scene() => Assert.True(SuppressAllZero(AutoArchive.ArchiveReason.SceneChange));
    [Fact] public void All_zero_junk_suppressed_idle()  => Assert.True(SuppressAllZero(AutoArchive.ArchiveReason.Idle));

    // ── Any nonzero activity BANKS on every auto trigger — no participant-count or span floor ──────

    private static bool SuppressWithActivity(AutoArchive.ArchiveReason reason)
        => Plugin.ShouldSuppressAutoArchive(reason, carriesFreshResult: false, allRowsZero: false);

    [Fact] public void Activity_banks_stage() => Assert.False(SuppressWithActivity(AutoArchive.ArchiveReason.StageChange));
    [Fact] public void Activity_banks_boss()  => Assert.False(SuppressWithActivity(AutoArchive.ArchiveReason.BossPhase));
    [Fact] public void Activity_banks_wipe()  => Assert.False(SuppressWithActivity(AutoArchive.ArchiveReason.Wipe));
    [Fact] public void Activity_banks_scene() => Assert.False(SuppressWithActivity(AutoArchive.ArchiveReason.SceneChange));
    [Fact] public void Activity_banks_idle()  => Assert.False(SuppressWithActivity(AutoArchive.ArchiveReason.Idle));
}
