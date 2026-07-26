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
        Assert.True(e.TryBeginBossSegmentCut(200_000));   // a boss segment is now open
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
        Assert.True(e.TryBeginBossSegmentCut(200_000));
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
        Assert.True(e.TryBeginBossSegmentCut(200_000));   // segment opens
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
        Assert.False(e.TryBeginBossSegmentCut(200_000));
        Assert.Null(e.Evaluate(Live(nowMs: 260_000) with { BossDead = true, BossGone = true, BossPresent = false }));
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
        Assert.True(e.TryBeginBossSegmentCut(200_000));
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
        Assert.True(e.TryBeginBossSegmentCut(200_000));
        Assert.Null(e.Evaluate(Live(nowMs: 210_000) with { BossGone = true, BossDead = false, BossPresent = false }));
        Assert.False(e.TryBeginBossSegmentCut(210_000));   // fight continues — no second cut
    }

    [Fact]
    public void Any_nonboss_archive_closes_the_segment_so_a_wipe_retry_recuts()
    {
        // Owner scenario: wipe on the boss, then retry the SAME boss. The wipe archive closes the
        // segment; the retry's first hit must be allowed to cut again (with keep-before applied by the
        // caller). This used to require BossRecutOnRedetect=true; it is now unconditional.
        var e = new AutoArchiveEngine { CooldownMs = 0 };
        Assert.Null(e.Evaluate(Live()));
        Assert.True(e.TryBeginBossSegmentCut(200_000));
        e.OnArchived(230_000, ArchiveReason.Wipe);
        Assert.True(e.TryBeginBossSegmentCut(240_000));
    }

    [Fact]
    public void A_bossphase_archive_does_not_close_the_segment_it_just_opened()
    {
        var e = new AutoArchiveEngine { CooldownMs = 0 };
        Assert.Null(e.Evaluate(Live()));
        Assert.True(e.TryBeginBossSegmentCut(200_000));
        e.OnArchived(200_000, ArchiveReason.BossPhase);    // the trash bank that STARTS the fight
        Assert.False(e.TryBeginBossSegmentCut(201_000));   // segment still open — one fight, one cut
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
        Assert.True(e.TryBeginBossSegmentCut(200_000));
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
        Assert.True(e.TryBeginBossSegmentCut(200_000));
        var dead = Live(nowMs: 205_000) with { BossDead = true, BossGone = true, BossPresent = false };
        Assert.Equal(ArchiveReason.BossKill, e.Evaluate(in dead));
        // The BossKill has not been reported via OnArchived yet, so the segment is still open.
        Assert.False(e.TryBeginBossSegmentCut(206_000));
        // Once the archive lands, the segment closes and a genuinely new boss may cut.
        e.OnArchived(207_000, ArchiveReason.BossKill);
        Assert.True(e.TryBeginBossSegmentCut(208_000));
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
        Assert.True(e.TryBeginBossSegmentCut(110_000));
        e.OnArchived(110_000, ArchiveReason.BossPhase);
        fired.Add(ArchiveReason.BossPhase);

        // 2. wipe on B: the party dies, the wipe archive banks attempt 1 and closes the segment.
        var wipe = Live(nowMs: 160_000) with { DeadCount = 4 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in wipe));
        e.OnArchived(160_000, ArchiveReason.Wipe);
        fired.Add(ArchiveReason.Wipe);

        // 3. run-back: B is alive and unkilled, the latch is closed -> the retry cuts again.
        Assert.True(e.TryBeginBossSegmentCut(200_000));
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
}
