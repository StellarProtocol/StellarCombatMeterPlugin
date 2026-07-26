using Stellar.Abstractions.Domain;
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

    // ── Inline boss-phase cut: archive the pre-boss trash ONLY when there was prior combat (Task 7) ──
    // Direct engage (no combat before the boss) must NOT emit a spurious pre-fight archive — the boss
    // fight is one clean segment starting at the first hit (owner spec point 2). Trash→boss archives the
    // accumulated trash as its own segment (owner spec point 3). The boss-enabled + once-per-fight
    // gating is applied separately by AutoArchiveEngine.TryBeginBossSegmentCut (see AutoArchiveEngineTests).

    [Fact]
    public void ShouldArchiveTrashForBoss_true_only_with_prior_combat()
    {
        Assert.True(Plugin.ShouldArchiveTrashForBoss(priorCombat: true));    // trash → boss: bank the trash
        Assert.False(Plugin.ShouldArchiveTrashForBoss(priorCombat: false));  // direct engage: no spurious archive
    }

    // ── Inline boss cut is considered only when enabled, NO segment active, AND in an instanced run ──
    // (recut-fix, 2026-07-21). Keying on segment-active (NOT "boss already known") is what makes a
    // re-detect cut again capped once UpdateLatches re-arms the latch. The inRun gate keeps the cut out
    // of the open world.

    [Fact]
    public void ShouldConsiderInlineBossCut_requires_enabled_no_active_segment_and_in_run()
    {
        Assert.True(Plugin.ShouldConsiderInlineBossCut(bossEnabled: true,  bossSegmentActive: false, inRun: true));   // fresh OR re-detect → cut (capped)
        Assert.False(Plugin.ShouldConsiderInlineBossCut(bossEnabled: false, bossSegmentActive: false, inRun: true));  // boss auto-archive off
        Assert.False(Plugin.ShouldConsiderInlineBossCut(bossEnabled: true,  bossSegmentActive: true,  inRun: true));  // segment running → fast-exit
        Assert.False(Plugin.ShouldConsiderInlineBossCut(bossEnabled: true,  bossSegmentActive: false, inRun: false)); // open world — no cut
    }

    // ---- killed-boss marks (2026-07-26): the corpse-readoption loop that produced the sliver spam ----

    [Fact]
    public void A_live_boss_is_adopted()
        => Assert.True(Plugin.ShouldAdoptBossCandidate(isBoss: true, alreadyKilled: false));

    [Fact]
    public void A_killed_boss_is_never_readopted()
        // The loop: death pulse re-armed the latch, the next boss-tagged event re-adopted the DEAD boss
        // (still cached isBoss=true), the inline cut fired, the next tick saw the corpse dead again...
        // one 0 ms archive per turn. Barring re-adoption breaks it at the source.
        => Assert.False(Plugin.ShouldAdoptBossCandidate(isBoss: true, alreadyKilled: true));

    [Fact]
    public void A_nonboss_entity_is_never_adopted()
        => Assert.False(Plugin.ShouldAdoptBossCandidate(isBoss: false, alreadyKilled: false));

    // ---- finding 4 (review round 2026-07-27): the tracked boss id must survive a transient blink ----
    // BossStatus used to clear _autoArchiveBossId on ANY "gone" reading (dead OR a vitals-cache
    // eviction). Re-adoption is gated behind !bossSegmentActive, which stays closed for the WHOLE
    // fight, so a blink-cleared id was never re-set: no further BossDead could ever rise for that
    // fight, and it only ever banked at the eventual run-end/scene archive. The fix: clear ONLY on a
    // confirmed death.

    [Fact]
    public void ShouldClearTrackedBoss_only_on_confirmed_death()
    {
        Assert.True(Plugin.ShouldClearTrackedBoss(confirmedDead: true));
        Assert.False(Plugin.ShouldClearTrackedBoss(confirmedDead: false));   // a transient eviction (blink) must not clear it
    }

    // ---- finding 3 (review round 2026-07-27): which damage events feed the BossKill settle clock ----
    // The old wiring (Plugin.Capture.cs) stamped _lastBossDamageMs off _bossCheck[TargetId] — a cache
    // Clear() wipes on every banked archive, including the trash→boss bank that OPENS the very fight
    // the clock is meant to watch, so the clock never engaged for the whole fight. The fix reads a
    // dedicated _settleBossId that survives both Clear() and the boss's own death.

    [Fact]
    public void IsSettleBossDamage_true_only_for_damage_targeting_the_settle_boss()
    {
        var boss = new EntityId(555);
        var add  = new EntityId(1);
        // A corpse DoT tick on the DEAD boss still counts — settleBossId is never cleared by the death,
        // only by a scene boundary — which is exactly what holds the settle window open post-kill.
        Assert.True(Plugin.IsSettleBossDamage(isHeal: false, targetId: boss, settleBossId: boss));
        Assert.False(Plugin.IsSettleBossDamage(isHeal: true, targetId: boss, settleBossId: boss));    // heals never count
        Assert.False(Plugin.IsSettleBossDamage(isHeal: false, targetId: add, settleBossId: boss));    // add cleanup, not the boss
        Assert.False(Plugin.IsSettleBossDamage(isHeal: false, targetId: default, settleBossId: default)); // no boss ever adopted this run
    }
}
