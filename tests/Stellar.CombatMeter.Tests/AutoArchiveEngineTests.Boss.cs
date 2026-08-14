using System.Collections.Generic;
using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Boss-phase invariant (the engine never fires BossPhase itself), the BossKill confirmed-death
// trigger, and the end-to-end raid-sequence pin. Split out of AutoArchiveEngineTests.cs
// (2026-07-26, review round) — see that file's banner for the full partial map. Live()/Armed()
// live there.
public partial class AutoArchiveEngineTests
{
    // ---- boss phase ----

    // PINNED REGRESSION (recut-fix, 2026-07-21, run sea/U051Yv8lf2): the engine must NEVER return
    // BossPhase. ALL boss cuts route through the INLINE capped path (Plugin.Capture.cs
    // MaybeCutForBossPhase → ManualArchive(BossPhase, replayUpperCapServerMs)); the engine's old
    // Evaluate boss branch fired an UNCAPPED archive at the engine-tick "now", which placed the
    // keep-before boss boundary at "now" (owner saw 0:55) instead of firstHit − keepBefore (0:48) on a
    // re-detect (where _bossSegmentActive was re-armed but the boss was still known, so the inline gate
    // skipped and the engine won the race). Removing the branch closes that uncapped path entirely.
    [Fact]
    public void Evaluate_never_returns_bossphase()
    {
        var e = Armed(Live());
        // The exact conditions the old branch fired on: boss present, no segment active, in a run.
        var s = Live() with { BossPresent = true, BossGone = false, BossDead = false };
        Assert.Null(e.Evaluate(in s));   // NOT ArchiveReason.BossPhase — the engine can't fire an uncapped boss cut
        // …even when a stale banked-style sighting persists across a cooldown window.
        var later = s with { NowMs = s.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Null(e.Evaluate(in later));
    }

    // ---- BossKill (2026-07-26): the confirmed-death archive, deferred by the caller's settle window ----

    [Fact]
    public void BossKill_fires_on_confirmed_death_while_segment_open()
    {
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));                 // adopt flow version
        Assert.True(e.TryBeginBossSegmentCut());   // a boss segment is now open
        var s = Live(nowMs: 260_000) with { BossDead = true, BossGone = true, BossPresent = false };
        Assert.Equal(ArchiveReason.BossKill, e.Evaluate(in s));
    }

    [Fact]
    public void BossKill_does_not_fire_without_an_open_segment()
    {
        // A corpse observed with no segment open (died to someone else / segment already banked) must
        // never bank. This is the guard that made the old post-kill spam impossible to reproduce here.
        // IdleEnabled=false isolates this from the UNRELATED idle trigger: at nowMs=260_000 the fixed
        // Live() LastDamageMs (160_000) is > IdleTimeoutMs (60_000 default) stale, which would otherwise
        // fire Idle and mask the assertion this test actually cares about (no segment => no BossKill).
        var e = new AutoArchiveEngine { IdleEnabled = false };
        Assert.Null(e.Evaluate(Live()));
        var s = Live(nowMs: 260_000) with { BossDead = true, BossGone = true, BossPresent = false };
        Assert.Null(e.Evaluate(in s));
    }

    [Fact]
    public void BossKill_fires_once_per_segment_no_matter_how_many_death_readings_arrive()
    {
        // THE regression pin for the reported bug: repeated post-death readings produced one archive
        // per engine tick (owner log: durMs=0 archives ~1 s apart). Exactly one fire per open segment.
        // IdleEnabled=false isolates this from the UNRELATED idle trigger — see the comment on
        // BossKill_does_not_fire_without_an_open_segment; the same nowMs-vs-fixed-LastDamageMs staleness
        // applies to every tick in the loop below.
        var e = new AutoArchiveEngine { IdleEnabled = false };
        Assert.Null(e.Evaluate(Live()));
        Assert.True(e.TryBeginBossSegmentCut());
        var dead = Live(nowMs: 260_000) with { BossDead = true, BossGone = true, BossPresent = false };
        Assert.Equal(ArchiveReason.BossKill, e.Evaluate(in dead));
        e.OnArchived(260_000, ArchiveReason.BossKill);
        for (long t = 271_000; t <= 300_000; t += 1_000)
            Assert.Null(e.Evaluate(Live(nowMs: t) with { BossDead = true, BossGone = true, BossPresent = false }));
    }

    [Fact]
    public void BossKill_survives_a_cooldown_and_fires_when_it_lifts()
    {
        // The death arrives as a one-tick pulse and TickAutoArchiveTriggers skips Evaluate while another
        // archive is pending — so the want must be LATCHED, not edge-consumed, or the fight never banks.
        // The cooldown must still be RUNNING when the death lands, and the segment must still be OPEN.
        // Only a BossPhase archive satisfies both: it arms the cooldown without closing the segment it
        // just opened (the trash bank). Any other reason closes the latch, and then there is no open
        // segment for BossKill to belong to.
        var e = new AutoArchiveEngine { CooldownMs = 10_000, IdleEnabled = false };
        Assert.Null(e.Evaluate(Live()));
        Assert.True(e.TryBeginBossSegmentCut());   // segment opens
        e.OnArchived(200_000, ArchiveReason.BossPhase);   // trash bank: cooldown armed, segment stays open
        var dead = Live(nowMs: 205_000) with { BossDead = true, BossGone = true, BossPresent = false };
        Assert.Null(e.Evaluate(in dead));                 // 5 s in — cooldown suppresses the fire
        // 11 s in: cooldown lifted. The death PULSE is gone by now (BossDead false), so only a latched
        // want can still fire — which is exactly the property under test.
        Assert.Equal(ArchiveReason.BossKill,
            e.Evaluate(Live(nowMs: 211_000) with { BossPresent = false }));
    }

    [Fact]
    public void BossKill_blocked_when_boss_disabled()
    {
        // IdleEnabled=false isolates this from the UNRELATED idle trigger — see the comment on
        // BossKill_does_not_fire_without_an_open_segment.
        var e = new AutoArchiveEngine { BossEnabled = false, IdleEnabled = false };
        Assert.Null(e.Evaluate(Live()));
        Assert.False(e.TryBeginBossSegmentCut());
        Assert.Null(e.Evaluate(Live(nowMs: 260_000) with { BossDead = true, BossGone = true, BossPresent = false }));
    }

    // ── OWNER RULING 2026-08-14: boss-set ADMISSION is always-on; the toggle gates CUTS only ──────────
    //
    // Why these two tests exist. Before the ruling, Boss phase = OFF meant _stageBosses stayed EMPTY, so
    // Plugin.BossDetection.cs's BossStatus() short-circuited on `Count == 0` and always handed the engine
    // (present, gone, dead) = (false, false, false). Un-gating admission (Plugin.AutoArchive.cs's
    // ShouldConsiderBossAdmission lost its bossEnabled term) means a toggle-off run now feeds the engine
    // REAL boss readings for the first time. That is a genuinely new input combination — `BossEnabled ==
    // false` together with `BossDead/BossGone/BossPresent == true` was previously unreachable in
    // production — and the ruling requires it to change NOTHING (protected archive-flow invariant 8:
    // sub-toggles change cuts/timing only, never detection, verdict, or run id).
    //
    // It holds structurally: TryBeginBossSegmentCut refuses to set _bossSegmentActive while !BossEnabled,
    // UpdateLatches arms _bossKillWanted only under _bossSegmentActive and zeroes the gone-timeout streak
    // whenever !_bossSegmentActive, and Evaluate's fire is additionally gated `BossEnabled &&
    // _bossKillWanted`. The settle window watches the general damage clock (_lastDamageMs), which carries
    // no boss term at all. These tests pin that chain end-to-end rather than by inspection.

    [Fact]
    public void Boss_readings_are_inert_while_boss_disabled()
    {
        // DIFFERENTIAL pin (agent-process-rules § 23: a test that cannot fail is worse than no test —
        // this one compares against a control, so it fails the moment a boss reading leaks a decision).
        // Two engines, identical config with Boss phase OFF, driven through the SAME long tick sequence.
        // The only difference: `live` gets the real boss readings admission now produces, `control` gets
        // the all-false tuple the toggle-off path produced BEFORE the ruling. Every returned reason must
        // match, tick for tick — that IS "byte-identical behavior with the toggle off".
        //
        // The sequence deliberately spans a full boss fight AND long enough for the gone-timeout streak
        // (BossGoneTimeoutMs) to mature, since that streak is the one boss-derived latch with a time
        // dimension and so the likeliest place for a leak to hide.
        var live    = new AutoArchiveEngine { BossEnabled = false, IdleEnabled = false, CooldownMs = 10_000 };
        var control = new AutoArchiveEngine { BossEnabled = false, IdleEnabled = false, CooldownMs = 10_000 };

        Assert.Null(live.Evaluate(Live()));      // adopt flow version on both
        Assert.Null(control.Evaluate(Live()));

        // Neither engine may open a segment — the inline cut's own BossEnabled gate (invariant 8).
        Assert.False(live.TryBeginBossSegmentCut());
        Assert.False(control.TryBeginBossSegmentCut());

        // engaged → alive → gone (held well past BossGoneTimeoutMs) → confirmed dead.
        var bossReadings = new List<(bool present, bool gone, bool dead)>
        {
            (true,  false, false),   // pull
            (true,  false, false),   // mid-fight
            (false, true,  false),   // AOI blink / scripted-kill vanish — starts the gone streak
            (false, true,  false),
            (false, true,  false),
            (false, true,  true),    // confirmed death pulse
            (false, true,  false),   // pulse gone; a latched want (if any) would still fire here
        };

        long now = 200_000;
        for (var i = 0; i < bossReadings.Count; i++)
        {
            var (present, gone, dead) = bossReadings[i];
            // Step past BossGoneTimeoutMs across the streak so the timeout would mature if it could arm.
            now += AutoArchiveEngine.BossGoneTimeoutMs;
            var withBoss = Live(nowMs: now) with
            {
                BossPresent = present, BossGone = gone, BossDead = dead,
                // Keep the damage clock fresh so the (disabled) idle trigger can never confound the diff.
                LastDamageMs = now - 1_000,
            };
            var without = withBoss with { BossPresent = false, BossGone = false, BossDead = false };

            var liveReason    = live.Evaluate(in withBoss);
            var controlReason = control.Evaluate(in without);

            Assert.Equal(controlReason, liveReason);          // tick-for-tick identical decisions
            Assert.NotEqual(ArchiveReason.BossKill, liveReason);   // and never a boss cut, explicitly
            // The segment latch the cut path keys on must stay closed on both.
            Assert.False(live.BossSegmentActive);
            Assert.False(control.BossSegmentActive);
        }
    }

    [Fact]
    public void A_kill_seen_while_boss_disabled_does_not_fire_when_the_toggle_is_turned_on()
    {
        // The one carry-over risk the ruling introduces: admission is now running during a toggle-off
        // fight, so if a boss death could LATCH a want while disabled, flipping Boss phase on mid-run
        // would fire a stale BossKill against a fight that was never cut — banking a spurious archive
        // and, worse, splitting a replay window (protected invariant 6, the replay-contiguity P0).
        // It cannot: _bossKillWanted arms only under _bossSegmentActive, which TryBeginBossSegmentCut
        // refuses to set while disabled. Pinned here because the structural argument lives in two files.
        var e = new AutoArchiveEngine { BossEnabled = false, IdleEnabled = false };
        Assert.Null(e.Evaluate(Live()));

        // A full kill observed while the toggle is OFF (only reachable now that admission is always-on).
        var dead = Live(nowMs: 260_000) with { BossPresent = false, BossGone = true, BossDead = true };
        Assert.Null(e.Evaluate(in dead));

        // Owner flips "Boss phase" on mid-run. No segment was ever opened, so there is nothing to bank.
        e.BossEnabled = true;
        Assert.Null(e.Evaluate(Live(nowMs: 270_000) with { BossPresent = false, BossGone = true }));
        Assert.False(e.BossSegmentActive);

        // And the NEXT genuine boss fight still cuts normally — the toggle-off period left no residue.
        Assert.True(e.TryBeginBossSegmentCut());
    }

    [Fact]
    public void An_intervening_archive_consumes_the_pending_bosskill()
    {
        // The race OnArchived already guards for _stagePending: the boss dies and arms the want, but
        // another archive — a manual hotkey press, a wipe — closes the segment before the deferred
        // BossKill fires. The want must not survive to bank a stale archive against a segment that is
        // already closed, and already banked by that intervening archive.
        var e = new AutoArchiveEngine { CooldownMs = 10_000, IdleEnabled = false };
        Assert.Null(e.Evaluate(Live()));
        Assert.True(e.TryBeginBossSegmentCut());
        e.OnArchived(200_000, ArchiveReason.BossPhase);   // trash bank: cooldown armed, segment stays open
        var dead = Live(nowMs: 205_000) with { BossDead = true, BossGone = true, BossPresent = false };
        Assert.Null(e.Evaluate(in dead));                 // cooldown suppresses the fire; want is latched
        e.OnArchived(206_000, ArchiveReason.Manual);      // owner presses Archive — the segment closes here
        Assert.Null(e.Evaluate(Live(nowMs: 216_000) with { BossPresent = false }));   // no stale BossKill
    }

    [Fact]
    public void Transient_eviction_never_ends_the_segment()
    {
        // A mid-fight AOI/vitals blink archives nothing, so it must not re-open the cut. Replaces the
        // retired recut-flag pair (TryBeginBossSegmentCut_no_rearm_on_transient_eviction_recut_off /
        // _rearms_on_eviction_when_recut_on) — the flag is gone in Task 4, the invariant is not.
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));
        Assert.True(e.TryBeginBossSegmentCut());
        Assert.Null(e.Evaluate(Live(nowMs: 210_000) with { BossGone = true, BossDead = false, BossPresent = false }));
        Assert.False(e.TryBeginBossSegmentCut());   // fight continues — no second cut
    }

    [Fact]
    public void Any_nonboss_archive_closes_the_segment_so_a_wipe_retry_recuts()
    {
        // Owner scenario: wipe on the boss, then retry the SAME boss. The wipe archive closes the
        // segment; the retry's first hit must be allowed to cut again (with keep-before applied by the
        // caller). This used to require BossRecutOnRedetect=true; it is now unconditional.
        var e = new AutoArchiveEngine { CooldownMs = 0 };
        Assert.Null(e.Evaluate(Live()));
        Assert.True(e.TryBeginBossSegmentCut());
        e.OnArchived(230_000, ArchiveReason.Wipe);
        Assert.True(e.TryBeginBossSegmentCut());
    }

    [Fact]
    public void A_bossphase_archive_does_not_close_the_segment_it_just_opened()
    {
        var e = new AutoArchiveEngine { CooldownMs = 0 };
        Assert.Null(e.Evaluate(Live()));
        Assert.True(e.TryBeginBossSegmentCut());
        e.OnArchived(200_000, ArchiveReason.BossPhase);    // the trash bank that STARTS the fight
        Assert.False(e.TryBeginBossSegmentCut());   // segment still open — one fight, one cut
    }

    [Fact]
    public void BossKill_want_is_consumed_by_the_fire_so_a_later_tick_cannot_refire()
    {
        // Isolates the self-clear in Evaluate's BossKill branch. BossDead is a ONE-TICK pulse
        // (BossStatus clears the tracked id the moment it sees the death), so the tick after the fire
        // carries BossDead=false. If the branch stopped clearing the want, that next tick would bank a
        // second archive for the same fight. OnArchived is deliberately NOT called here — it also
        // clears the want, which would mask the very regression this test exists to catch.
        var e = new AutoArchiveEngine { CooldownMs = 0, IdleEnabled = false };
        Assert.Null(e.Evaluate(Live()));
        Assert.True(e.TryBeginBossSegmentCut());
        var dead = Live(nowMs: 205_000) with { BossDead = true, BossGone = true, BossPresent = false };
        Assert.Equal(ArchiveReason.BossKill, e.Evaluate(in dead));
        // Pulse gone, no archive reported yet: the want must already be consumed.
        Assert.Null(e.Evaluate(Live(nowMs: 206_000) with { BossPresent = false }));
    }

    [Fact]
    public void A_confirmed_death_alone_does_not_close_the_segment_latch()
    {
        // Only an archive closes a segment (OnArchived). A raw BossDead reading must not — that direct
        // re-arm WAS the defect: it let the next boss-tagged event cut again, one 0 ms archive per tick.
        var e = new AutoArchiveEngine { CooldownMs = 0, IdleEnabled = false };
        Assert.Null(e.Evaluate(Live()));
        Assert.True(e.TryBeginBossSegmentCut());
        var dead = Live(nowMs: 205_000) with { BossDead = true, BossGone = true, BossPresent = false };
        Assert.Equal(ArchiveReason.BossKill, e.Evaluate(in dead));
        // The BossKill has not been reported via OnArchived yet, so the segment is still open.
        Assert.False(e.TryBeginBossSegmentCut());
        // Once the archive lands, the segment closes and a genuinely new boss may cut.
        e.OnArchived(207_000, ArchiveReason.BossKill);
        Assert.True(e.TryBeginBossSegmentCut());
    }

    // ---- end-to-end sequence pin (Task 9): the owner's raid acceptance narrative ----

    [Fact]
    public void Raid_sequence_produces_one_archive_per_boss_and_no_slivers()
    {
        // Owner's acceptance narrative: one raid map, pull boss B, wipe, retry B, kill it. The engine
        // side of the six-archive sequence — trash cut, wipe, run-back cut, BossKill — with the post-kill
        // corpse readings producing NOTHING (the reported bug).
        // IdleEnabled = false is REQUIRED here, not cosmetic: Live() pins LastDamageMs at 160_000, so
        // every tick past 220_000 also satisfies the Idle trigger (60 s timeout, 30 s content floor).
        // The post-kill loop below runs to 298_000 and would report Idle instead of the null it asserts.
        var e = new AutoArchiveEngine { CooldownMs = 5_000, WipeGraceMs = 0, IdleEnabled = false };
        var fired = new List<ArchiveReason>();

        Assert.Null(e.Evaluate(Live(nowMs: 100_000)));            // adopt flow version

        // 1. trash -> boss B: the inline cut opens the fight (caller banks the trash as BossPhase).
        Assert.True(e.TryBeginBossSegmentCut());
        e.OnArchived(110_000, ArchiveReason.BossPhase);
        fired.Add(ArchiveReason.BossPhase);

        // 2. wipe on B: the party dies, the wipe archive banks attempt 1 and closes the segment.
        var wipe = Live(nowMs: 160_000) with { DeadCount = 4 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in wipe));
        e.OnArchived(160_000, ArchiveReason.Wipe);
        fired.Add(ArchiveReason.Wipe);

        // 3. run-back: B is alive and unkilled, the latch is closed -> the retry cuts again.
        Assert.True(e.TryBeginBossSegmentCut());
        e.OnArchived(200_000, ArchiveReason.BossPhase);
        fired.Add(ArchiveReason.BossPhase);

        // 4. B dies: BossKill fires once (the caller defers it through the settle window).
        var dead = Live(nowMs: 260_000) with { BossDead = true, BossGone = true, BossPresent = false };
        Assert.Equal(ArchiveReason.BossKill, e.Evaluate(in dead));
        e.OnArchived(262_000, ArchiveReason.BossKill);
        fired.Add(ArchiveReason.BossKill);

        // 5. corpse DoTs / add cleanup for the next 30 s: the reported bug produced one 0 ms archive
        //    per tick here. Now: nothing at all.
        for (long t = 268_000; t <= 298_000; t += 1_000)
            Assert.Null(e.Evaluate(Live(nowMs: t) with { BossDead = true, BossGone = true, BossPresent = false }));

        Assert.Equal(
            new[] { ArchiveReason.BossPhase, ArchiveReason.Wipe, ArchiveReason.BossPhase, ArchiveReason.BossKill },
            fired);
    }

    // ---- pre-emption: a fresh boss engagement while a DIFFERENT (non-boss-segment) archive is still
    // pending its settle wait (finding 2, review round 2026-07-27) ----

    [Fact]
    public void Preempting_a_pending_archive_does_not_close_the_segment_it_just_opened()
    {
        // The real repro: no boss segment is open yet (mirrors an earlier TRASH-only Wipe/Idle/Stage
        // archive still waiting out its settle window when the first boss hit of a fresh pull arrives
        // — that is exactly when ShouldConsiderInlineBossCut's !bossSegmentActive gate lets the plugin
        // reach the cut at all). MaybeCutForBossPhase opens the NEW segment via
        // TryBeginBossSegmentCutAcrossPreemption, THEN commits the OLD pending via ManualArchive, whose
        // OnArchived call reports a reason that is NEVER BossPhase (a pending reason is never
        // BossPhase). Before the fix, that unconditional "any non-BossPhase reason closes the segment"
        // rule clobbered the segment just opened for the NEW fight — leaving it with nothing open, so
        // its boss's eventual death could never fire BossKill (the fight would only ever bank at
        // run-end). The guard makes OnArchived skip that one close when it immediately follows a
        // preemption reopen.
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));
        Assert.True(e.TryBeginBossSegmentCutAcrossPreemption());
        e.OnArchived(200_000, ArchiveReason.Wipe);   // commits the OLD pending — reason != BossPhase
        Assert.False(e.TryBeginBossSegmentCut());    // the NEW segment survived — still open, one cut
    }

    [Fact]
    public void The_preemption_guard_is_one_shot()
    {
        // The guard must suppress ONLY the one OnArchived call that immediately follows the reopen —
        // never a later, unrelated close. A guard that stuck permanently true would silently disable
        // the segment-close mechanism for the rest of the run (every future archive would leave the
        // segment open), which is a worse defect than the one this fix closes.
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));
        Assert.True(e.TryBeginBossSegmentCutAcrossPreemption());
        e.OnArchived(200_000, ArchiveReason.Wipe);      // guarded — segment survives
        Assert.False(e.TryBeginBossSegmentCut());
        e.OnArchived(210_000, ArchiveReason.BossKill);  // NOT guarded — this really does end the fight
        Assert.True(e.TryBeginBossSegmentCut());        // segment closed for real this time
    }

    // ---- gone-timeout (P0 fix, owner raid 2026-07-28, runId=632530154488332288): a boss that never
    // reports HP<=0 must still end its fight ----
    //
    // Owner's stage 1 has TWO bosses that must both be brought to 1% simultaneously; they then die by a
    // SCRIPTED event, so HP never reads <=0 for either — BossDead never rises. Pre-fix there was no
    // OTHER way to close _bossSegmentActive (Task 2, 2026-07-26, deliberately removed the raw
    // BossGone/BossDead re-arm to stop the corpse-cut loop; Task 4 narrowed the tracked-boss clear
    // further) — so the segment latched open for the ENTIRE REST OF THE RUN and
    // ShouldConsiderInlineBossCut's `!bossSegmentActive` gate barred every later boss cut. The owner's
    // log showed exactly one giant archive (walk-in through run end) instead of one per boss/stage.

    [Fact]
    public void Raid_boss_that_never_reports_death_still_ends_its_segment_and_unblocks_the_next_stage()
    {
        // THE regression pin for the P0. Unmistakable about what it protects: (1) a boss segment that
        // is open, (2) a boss that goes CONTINUOUSLY unobserved (BossGone=true, BossDead=false — the
        // scripted-kill shape, HP never <=0) for the gone-timeout window, (3) the fight ends (BossKill
        // fires) WITHOUT a confirmed death, and (4) the very next boss (stage 2) can cut a fresh
        // segment afterward — proving the wedge is actually cleared, not just that one archive fired.
        // IdleEnabled=false isolates from the fixture trap (Live()'s LastDamageMs=160_000 also
        // satisfies the 60s Idle trigger at nowMs>=220_000, which would mask this assertion).
        var e = new AutoArchiveEngine { IdleEnabled = false };
        Assert.Null(e.Evaluate(Live(nowMs: 100_000)));                     // adopt flow version

        // Stage 1: pull the two-boss encounter — the inline cut opens the segment (caller's job; here
        // just open it directly, mirroring every other BossKill test in this file).
        Assert.True(e.TryBeginBossSegmentCut());

        // The boss goes gone (evicted from vitals — scripted death removes the entity) and STAYS gone,
        // never confirmed dead. Tick every second, same cadence TickAutoArchiveTriggers uses in
        // production, up to (but not past) the gone-timeout threshold: still no fire.
        long t = 150_000;
        for (; t < 150_000 + AutoArchiveEngine.BossGoneTimeoutMs; t += 1_000)
            Assert.Null(e.Evaluate(Live(nowMs: t) with { BossGone = true, BossDead = false, BossPresent = false }));

        // Threshold reached: the fight ends via the SAME ArchiveReason.BossKill a confirmed death would
        // use — no new enum value, no new history trig string (spec §2.1).
        var timedOut = Live(nowMs: 150_000 + AutoArchiveEngine.BossGoneTimeoutMs)
            with { BossGone = true, BossDead = false, BossPresent = false };
        Assert.Equal(ArchiveReason.BossKill, e.Evaluate(in timedOut));
        Assert.True(e.BossKillWasTimeout);   // distinguishable cause for the ungated [archive] line

        // The caller's settle wait elapses and the archive commits — closing the segment.
        e.OnArchived(t + 2_000, ArchiveReason.BossKill);

        // Stage 2: the next boss must be able to cut a fresh segment — the actual owner-visible fix.
        Assert.True(e.TryBeginBossSegmentCut());
    }

    [Fact]
    public void Gone_timeout_fires_at_the_threshold_not_one_ms_before()
    {
        // Duration is the discriminator (load-bearing design constraint) — pins the boundary exactly,
        // same shape as AutoArchiveSettleDelayTests' Not_due_one_ms_before_the_quiet_window_closes /
        // Due_exactly_at_two_seconds_of_no_combat pair, and complements
        // Transient_eviction_never_ends_the_segment's much-shorter (sub-second) blink case.
        var e = new AutoArchiveEngine { IdleEnabled = false };
        Assert.Null(e.Evaluate(Live(nowMs: 100_000)));
        Assert.True(e.TryBeginBossSegmentCut());
        long goneStart = 150_000;
        Assert.Null(e.Evaluate(Live(nowMs: goneStart) with { BossGone = true, BossDead = false, BossPresent = false }));
        var oneMsShort = Live(nowMs: goneStart + AutoArchiveEngine.BossGoneTimeoutMs - 1)
            with { BossGone = true, BossDead = false, BossPresent = false };
        Assert.Null(e.Evaluate(in oneMsShort));
        var atThreshold = Live(nowMs: goneStart + AutoArchiveEngine.BossGoneTimeoutMs)
            with { BossGone = true, BossDead = false, BossPresent = false };
        Assert.Equal(ArchiveReason.BossKill, e.Evaluate(in atThreshold));
    }

    [Fact]
    public void Gone_timeout_resets_when_the_boss_is_seen_alive_again_before_the_threshold()
    {
        // A boss that blinks gone and then comes back alive (still within the fight, still un-killed)
        // must not accumulate toward the timeout across the gap — matches
        // Transient_eviction_never_ends_the_segment's invariant, extended across a longer window.
        var e = new AutoArchiveEngine { IdleEnabled = false };
        Assert.Null(e.Evaluate(Live(nowMs: 100_000)));
        Assert.True(e.TryBeginBossSegmentCut());

        var goneStart = 150_000;
        Assert.Null(e.Evaluate(Live(nowMs: goneStart) with { BossGone = true, BossDead = false, BossPresent = false }));
        // Seen alive again well before the threshold — the streak resets.
        Assert.Null(e.Evaluate(Live(nowMs: goneStart + 1_000) with { BossGone = false, BossDead = false, BossPresent = true }));
        // Gone again — even past what WOULD have been the original streak's deadline, this is a FRESH
        // streak that has not yet run the full timeout.
        var stillWithinFreshStreak = goneStart + AutoArchiveEngine.BossGoneTimeoutMs;
        Assert.Null(e.Evaluate(Live(nowMs: stillWithinFreshStreak) with { BossGone = true, BossDead = false, BossPresent = false }));
    }

    [Fact]
    public void Gone_timeout_only_counts_while_a_segment_is_open()
    {
        // "The timeout only counts while a boss segment is open" (load-bearing design constraint) — no
        // segment means nothing to end; must never fire (and must never leave a stray armed want either).
        var e = new AutoArchiveEngine { IdleEnabled = false };
        Assert.Null(e.Evaluate(Live(nowMs: 100_000)));   // no TryBeginBossSegmentCut — no segment open
        for (long t = 150_000; t <= 150_000 + AutoArchiveEngine.BossGoneTimeoutMs + 5_000; t += 1_000)
            Assert.Null(e.Evaluate(Live(nowMs: t) with { BossGone = true, BossDead = false, BossPresent = false }));
    }

    [Fact]
    public void Gone_timeout_streak_clears_on_leaving_the_instanced_run()
    {
        // Leaving the run (open world between dungeons) must drop an in-progress gone streak — a fresh
        // run's boss starts with a clean slate, same as every other latch UpdateLatches resets there.
        var e = new AutoArchiveEngine { IdleEnabled = false };
        Assert.Null(e.Evaluate(Live(nowMs: 100_000)));
        Assert.True(e.TryBeginBossSegmentCut());
        Assert.Null(e.Evaluate(Live(nowMs: 150_000) with { BossGone = true, BossDead = false, BossPresent = false }));
        // Leave the run before the threshold elapses — segment + streak both drop.
        Assert.Null(e.Evaluate(Live(nowMs: 151_000) with { InstancedRun = false, BossPresent = false }));
        // Re-enter and re-open a segment: even at what would have been the original streak's deadline,
        // nothing fires — the streak did not survive the run boundary.
        Assert.True(e.TryBeginBossSegmentCut());
        var atOldDeadline = Live(nowMs: 150_000 + AutoArchiveEngine.BossGoneTimeoutMs)
            with { BossGone = true, BossDead = false, BossPresent = false };
        Assert.Null(e.Evaluate(in atOldDeadline));
    }

    [Fact]
    public void BossKillWasTimeout_is_false_for_a_confirmed_death()
    {
        // The cause flag must correctly report "death", not "timeout", for the ordinary path — the
        // ungated [archive] line's cause= field must never mislabel a real death as a timeout.
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));
        Assert.True(e.TryBeginBossSegmentCut());
        var dead = Live(nowMs: 260_000) with { BossDead = true, BossGone = true, BossPresent = false };
        Assert.Equal(ArchiveReason.BossKill, e.Evaluate(in dead));
        Assert.False(e.BossKillWasTimeout);
    }
}
