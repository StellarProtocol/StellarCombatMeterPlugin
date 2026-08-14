using System;
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
        // PINNED (Critical fix, 2026-08-12 review): the CUT stays gated on an active segment — this must
        // STILL be false after decoupling admission from the cut. Only ADMISSION (below) dropped the
        // bossSegmentActive term; the cut decision itself is untouched.
        Assert.False(Plugin.ShouldConsiderInlineBossCut(bossEnabled: true,  bossSegmentActive: true,  inRun: true));  // segment running → fast-exit
        Assert.False(Plugin.ShouldConsiderInlineBossCut(bossEnabled: true,  bossSegmentActive: false, inRun: false)); // open world — no cut
    }

    // ── Admission is a SEPARATE, less-strict gate than the cut ────────────────────────────────────────
    //
    // RE-PINNED 2026-08-14 (owner ruling — this test previously pinned the OLD, stricter gating and now
    // pins the ruling that superseded it; agent-process-rules § 9 corollary: when a fix deliberately
    // changes a pinned contract, RE-PIN in the same commit with the rationale in the test comment).
    //
    // History, in two rounds — each round DELETED one term from this guard, and each deletion IS the fix:
    //
    //   Round 1 (Critical fix, 2026-08-12 review) removed `bossSegmentActive`. The ONLY call path into
    //   StageBossSet.Admit was MaybeCutForBossPhase, gated by ShouldConsiderInlineBossCut — which
    //   fast-exits the instant bossSegmentActive is true (the first boss-touching event sets it via
    //   TryBeginBossSegmentCut). So a co-boss engaged AFTER the first hit was NEVER admitted; the set
    //   could never exceed one member in a real simultaneous fight, defeating the multi-boss spec (§3.2:
    //   "admit every IsBoss-flagged, not-already-killed entity while the stage is open").
    //
    //   Round 2 (OWNER RULING 2026-08-14, verbatim intent: "boss tracking is supposed to be a default
    //   feature") removed `bossEnabled`. Boss-set ADMISSION must be always-on during an instanced run,
    //   independent of the "Boss phase" auto-archive sub-toggle — exactly like elite capture (owner
    //   ruling 2026-08-13). This extends protected archive-flow invariant 5 ("boss detection is
    //   always-on; the toggle gates only per-boss archive CUTS") from the retired single bossId latch to
    //   the multi-boss SET. The OLD assertion here — `ShouldConsiderBossAdmission(bossEnabled: false,
    //   inRun: true)` is false — was the pin the ruling overturned; it is deliberately GONE, replaced by
    //   its exact inverse below. The second half of the ruling (the toggle STILL gates cuts) is pinned by
    //   ShouldConsiderInlineBossCut's test above, which keeps its bossEnabled term, and end-to-end at the
    //   engine by AutoArchiveEngineTests.Boss_readings_are_inert_while_boss_disabled.
    //
    // What survives both rounds is a single term: inRun. Admission is now the LEAST strict of the three
    // boss guards, and the parameter list itself is the proof — a re-added term would not compile here.
    [Fact]
    public void ShouldConsiderBossAdmission_is_always_on_in_a_run_regardless_of_toggle_or_segment()
    {
        // In an instanced run admission ALWAYS runs — the guard has no bossEnabled term and no
        // bossSegmentActive term left to block it (owner ruling 2026-08-14 / Critical fix 2026-08-12).
        Assert.True(Plugin.ShouldConsiderBossAdmission(inRun: true));
        // Open world — the ONLY remaining gate, unchanged by either round.
        Assert.False(Plugin.ShouldConsiderBossAdmission(inRun: false));
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
    //
    // Critical A (review round 2026-07-27, second pass): finding 4's fix was too broad — clearing ONLY
    // on confirmed death also means an eviction with NO segment open (the fight already ended via an
    // earlier archive: a wipe, a scene change, a stage cut) never clears either, pinning the id to a
    // dead-and-gone entity for the rest of the session (ObserveAutoArchiveBoss's `!= 0` early-out then
    // blocks every later boss from ever being adopted again). ShouldClearTrackedBoss now takes the
    // segment-active state too and clears whenever there is no open segment left to protect. All three
    // reachable shapes are pinned below: death always clears; a blink WITH an open segment keeps (the
    // finding-4 behaviour, unchanged); an eviction with NO open segment clears (the Critical A fix).

    [Fact]
    public void ShouldClearTrackedBoss_only_on_confirmed_death()
    {
        Assert.True(Plugin.ShouldClearTrackedBoss(confirmedDead: true, segmentActive: true));    // death clears regardless of segment state
        Assert.True(Plugin.ShouldClearTrackedBoss(confirmedDead: true, segmentActive: false));
        Assert.False(Plugin.ShouldClearTrackedBoss(confirmedDead: false, segmentActive: true));  // blink WITH an open segment keeps — the fight isn't over
        Assert.True(Plugin.ShouldClearTrackedBoss(confirmedDead: false, segmentActive: false));  // eviction with NO open segment clears (Critical A)
    }

    // ---- finding 3 (review round 2026-07-27), RETIRED (owner ruling 2026-07-28, defect 2 of the
    // bosskill-settle branch's raid-testing fixes) ----
    // The old wiring (Plugin.Capture.cs) stamped _lastBossDamageMs off _bossCheck[TargetId] — a cache
    // Clear() wipes on every banked archive, including the trash→boss bank that OPENS the very fight
    // the clock is meant to watch, so the clock never engaged for the whole fight. Finding 3's fix
    // (2026-07-27) introduced a dedicated _settleBossId that survived both Clear() and the boss's own
    // death, and IsSettleBossDamage(isHeal, targetId, settleBossId) decided which events fed the
    // boss-only settle clock. That whole narrowing is now WITHDRAWN: the owner reported residual damage
    // spilling into the head of the FOLLOWING archive ("there's mini dps that left to early of
    // 2,4,6") because adds/DoTs elsewhere kept the boss-only clock quiet while still landing damage
    // that should have held the window open. The settle window now watches the SAME general damage
    // clock for every reason (Plugin.AutoArchive.cs's retired-SettleClockMs note has the full story) —
    // there is no more "does this event belong to the boss-specific clock" decision to make, so
    // IsSettleBossDamage, _settleBossId, and _lastBossDamageMs are all deleted along with this test
    // (IsSettleBossDamage_true_only_for_damage_targeting_the_settle_boss). Heals still never count —
    // that part of the ruling stands, now expressed simply by _lastDamageMs only ever being stamped by
    // AccumulateDamage (non-heal player damage).

    // ---- Critical A / Important B (review round 2026-07-27, second pass): EventInvolvesBoss replaces
    // the `_autoArchiveBossId.Value == 0` proxy in MaybeCutForBossPhase (Plugin.AutoArchive.cs). The
    // proxy read "a boss is tracked at all" as "this event is about the boss" — valid only the instant
    // the id was just set FROM this same event. Once the id survives past that moment (a still-alive
    // boss after a wipe archive, or a stale id that used to pin forever pre-Critical-A), the proxy let
    // ANY subsequent event reach the inline cut. EventInvolvesBoss checks the actual src/tgt instead.

    [Fact]
    public void EventInvolvesBoss_true_when_source_is_boss()
        => Assert.True(Plugin.EventInvolvesBoss(new EntityId(555), new EntityId(1), new EntityId(555)));

    [Fact]
    public void EventInvolvesBoss_true_when_target_is_boss()
        => Assert.True(Plugin.EventInvolvesBoss(new EntityId(1), new EntityId(555), new EntityId(555)));

    [Fact]
    public void EventInvolvesBoss_false_when_neither_side_is_the_boss()
        => Assert.False(Plugin.EventInvolvesBoss(new EntityId(1), new EntityId(2), new EntityId(555)));

    [Fact]
    public void EventInvolvesBoss_false_when_no_boss_ever_adopted()
        => Assert.False(Plugin.EventInvolvesBoss(new EntityId(1), new EntityId(2), default));

    // Important B regression pin: after a wipe archive closes the segment and Clear()s, _autoArchiveBossId
    // still holds the SURVIVING boss (Clear() spares it on purpose — the boss is still alive, still the
    // same fight's boss). The old proxy treated the run-back's first event — a rez heal between two
    // players, nothing to do with the boss — as "involves the boss" merely because a boss id was tracked,
    // which let MaybeCutForBossPhase open a spurious boss segment over the run-back trash (banking
    // nothing, and leaving the real first hit on the boss with a segment already open so it never cuts).
    [Fact]
    public void EventInvolvesBoss_wipe_then_retry_rez_heal_does_not_involve_the_surviving_boss()
    {
        var survivingBoss = new EntityId(555);
        var healer        = new EntityId(1);
        var downedAlly    = new EntityId(2);
        Assert.False(Plugin.EventInvolvesBoss(healer, downedAlly, survivingBoss));
    }

    // ---- Final review, Critical 1: kill archives were shipping empty bosses[] ----
    // Plugin.BossDetection.cs's BossStatus() drains _stageBosses on the SAME tick the last member
    // dies/is scripted-killed, and Plugin.RunBoundary.cs's ResetRunScopedTrackers clears it again before
    // the always-firing scene archive banks — both BEFORE a deferred BuildHistoryEntry (Plugin.History.cs)
    // gets to read it. PreferLiveStageBosses is the pure preference rule BuildHistoryEntry/BuildBossHpTracks
    // apply via ResolveCurrentStageBosses (an IL2CPP-adjacent instance method, unreachable headlessly
    // without a live Plugin — "Plugin can't be instantiated in tests"). This pins the rule itself: prefer
    // the live set, fall back to the latch ONLY when live is empty — never the other way around.
    private static readonly (EntityId Id, int ConfigId, bool Killed) LiveOnly =
        (new EntityId(10), 102800, true);
    private static readonly (EntityId Id, int ConfigId, bool Killed) LatchedOnly =
        (new EntityId(11), 102801, false);

    [Fact]
    public void PreferLiveStageBosses_prefers_live_when_non_empty()
    {
        var live    = new[] { LiveOnly };
        var latched = new[] { LatchedOnly };

        var result = Plugin.PreferLiveStageBosses(live, latched);

        Assert.Same(live, result);
    }

    [Fact]
    public void PreferLiveStageBosses_falls_back_to_latch_when_live_is_empty()
    {
        // The exact shape a deferred kill/scene archive hits: the live set already drained/reset.
        var live    = Array.Empty<(EntityId Id, int ConfigId, bool Killed)>();
        var latched = new[] { LatchedOnly };

        var result = Plugin.PreferLiveStageBosses(live, latched);

        Assert.Same(latched, result);
    }

    [Fact]
    public void PreferLiveStageBosses_both_empty_returns_the_empty_live_set()
    {
        var live    = Array.Empty<(EntityId Id, int ConfigId, bool Killed)>();
        var latched = Array.Empty<(EntityId Id, int ConfigId, bool Killed)>();

        var result = Plugin.PreferLiveStageBosses(live, latched);

        Assert.Empty(result);
    }
}
