using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// RAID KILL-MISS ROOT CAUSE — poll suppression vs the ungated HP sampler (2026-08-15, run
/// sea/CvCyokazcx). The first 2.1.1 raid (map 13011) killed boss 102901 — the plugin's OWN 2 Hz HP
/// track recorded <c>5,3,2,1,0</c> — yet all three clients uploaded <c>killed=false</c> and the worker
/// derived <c>partial</c>. Mechanism: <see cref="Plugin.TickBossStatus"/> skips the WHOLE poll while an
/// archive is pending (<see cref="Plugin.ShouldPollBossStatus"/>), and a settle window stayed open
/// through the kill — so <c>_memberLastHpFrac</c> froze ABOVE the 15 % scripted floor and the poll never
/// read <c>Hp&lt;=0</c>, while the replay HP sampler (Plugin.Replay.cs, ungated) banked the proof.
///
/// OWNER-APPROVED FIX (docs/recon/raid-clear-and-multiboss.md § "Raid kill-miss root cause"): split the
/// poll. A read-only OBSERVATION pass runs EVERY tick — through settle windows and pause — keeping
/// <c>_memberLastHpFrac</c> fresh and sticky-marking Killed ONLY on a real <c>Hp&lt;=0</c> read (NO
/// low-HP inference: wipes happen at 0.01 % with the boss alive). The engine-facing half
/// (SetLiveness/Aggregate/drain/the one-tick BossDead pulse) keeps its <c>archivePending</c> suppression
/// untouched (that protects the pulse — <see cref="PauseCaptureTests"/>). The scripted-vanish rule is
/// fixed as a side effect: its failure was the STALE fraction, not the rule.
///
/// These pin the SPLIT the way the repo pins IL2CPP-adjacent glue: <c>Plugin</c> cannot be instantiated
/// headless, so the testable surface is the pure seams each half is dispatched through
/// (<see cref="Plugin.IsRealBossDeath"/>, <see cref="Plugin.IsScriptedRaidVanishKill"/>,
/// <see cref="StageBossSet.MarkKilled"/>) plus a differential composition harness that runs the two
/// halves in real <see cref="Plugin.TickBossStatus"/> order. Never weaken: an assertion flipping here
/// means a raid kill went back to reading <c>killed=false</c>.
/// </summary>
public class RaidKillMissTests
{
    private static EntityId E(long v) => new(v);

    // Vitals fixtures. Fraction = Hp / MaxHp; a MaxHp-only reading is "alive, HP unknown" (NOT dead).
    private static EntityVitals At(long hp, long maxHp) => new(hp, maxHp, IsKnown: true) { HasHpObservation = true };
    private static EntityVitals MaxHpOnly(long maxHp) => new(0, maxHp, IsKnown: true) { HasHpObservation = false };
    private static readonly EntityVitals Vanished = EntityVitals.Unknown;   // evicted — not known at all

    // ── PIN 2 (part a): observation marks Killed ONLY on a real Hp<=0 read — NO low-HP inference ──────
    //
    // Owner explicitly rejected "<=2 % = dead": a wipe leaves the boss alive at 0.01 %. IsRealBossDeath is
    // the observation pass's sticky-mark gate AND the engine half's `dead` term (extracted so both agree).

    [Fact]
    public void IsRealBossDeath_true_only_at_a_real_zero_hp_reading()
    {
        Assert.True(Plugin.IsRealBossDeath(At(0, 10_000)));        // real Hp<=0 → dead
        Assert.True(Plugin.IsRealBossDeath(At(-1, 10_000)));       // negative overshoot is still <=0
    }

    [Fact]
    public void IsRealBossDeath_false_for_a_wipe_at_a_hundredth_of_a_percent()
    {
        Assert.False(Plugin.IsRealBossDeath(At(1, 10_000)));       // 0.01 % — the owner's wipe case, ALIVE
        Assert.False(Plugin.IsRealBossDeath(At(200, 10_000)));     // 2 % — the inference the owner rejected
        Assert.False(Plugin.IsRealBossDeath(At(5_000, 10_000)));   // 50 %
    }

    [Fact]
    public void IsRealBossDeath_false_when_hp_is_unobserved_or_maxhp_only()
    {
        Assert.False(Plugin.IsRealBossDeath(MaxHpOnly(10_000)));   // alive, HP unknown — must not read as dead
        Assert.False(Plugin.IsRealBossDeath(Vanished));            // never observed / evicted
        Assert.False(Plugin.IsRealBossDeath(At(0, 0)));            // MaxHp 0 → no valid fraction
    }

    // ── PIN 3: the scripted-vanish rule — fresh fraction kills, STALE fraction does not (the bug) ─────
    //
    // Scripted raid bosses are brought to ~1 % then killed by an event (HP never reads 0, entity
    // vanishes). The rule is unchanged; the fix is that _memberLastHpFrac is now kept FRESH through the
    // settle window by the observation pass, so `lastFrac` is the real ~1 % at vanish instead of the
    // frozen pre-window value.

    [Fact]
    public void IsScriptedRaidVanishKill_true_for_an_evicted_raid_boss_last_seen_at_or_under_the_floor()
    {
        Assert.True(Plugin.IsScriptedRaidVanishKill(evicted: true, isRaid: true, lastFrac: 0.01f));   // fresh 1 %
        Assert.True(Plugin.IsScriptedRaidVanishKill(evicted: true, isRaid: true, lastFrac: 0.15f));   // the floor itself
        Assert.True(Plugin.IsScriptedRaidVanishKill(evicted: true, isRaid: true, lastFrac: 0f));      // exactly 0 recorded
    }

    [Fact]
    public void IsScriptedRaidVanishKill_false_on_a_STALE_fraction_above_the_floor()
    {
        // THE BUG: the poll was suppressed through the settle window, so _memberLastHpFrac froze at the
        // pre-window reading (above 15 %). Evicted with a stale-high fraction must NOT count as a kill —
        // which is exactly why the fix keeps the fraction fresh, it does not loosen the floor.
        Assert.False(Plugin.IsScriptedRaidVanishKill(evicted: true, isRaid: true, lastFrac: 0.50f));
        Assert.False(Plugin.IsScriptedRaidVanishKill(evicted: true, isRaid: true, lastFrac: 0.16f));
        Assert.False(Plugin.IsScriptedRaidVanishKill(evicted: true, isRaid: true, lastFrac: -1f));    // never recorded
    }

    [Fact]
    public void IsScriptedRaidVanishKill_false_when_still_present_or_not_a_raid()
    {
        // A boss alive at 0.01 % (still in AOI, not evicted) is the wipe case — never a scripted kill.
        Assert.False(Plugin.IsScriptedRaidVanishKill(evicted: false, isRaid: true, lastFrac: 0.01f));
        // Dungeons keep pure Hp<=0 semantics — a dungeon boss walked away from at low HP is not killed.
        Assert.False(Plugin.IsScriptedRaidVanishKill(evicted: true, isRaid: false, lastFrac: 0.01f));
    }

    // ── PIN (new API): StageBossSet.MarkKilled — sticky Killed WITHOUT touching Present ───────────────
    //
    // The observation counterpart to SetLiveness (which owns Present, the engine's aggregate input). The
    // observation pass may set Killed while the engine half is suppressed mid-settle, so it must not
    // disturb Present — the engine re-reads that itself on the resume tick.

    [Fact]
    public void MarkKilled_sets_killed_sticky_and_leaves_present_untouched()
    {
        var s = new StageBossSet();
        s.Admit(E(10), 102901);
        s.SetLiveness(E(10), new StageBossSet.BossLiveness { Present = true, Dead = false });   // alive, in AOI

        s.MarkKilled(E(10));

        var m = s.MemberAt(0);
        Assert.True(m.killed);                 // Killed marked…
        // …but Present is UNTOUCHED, so the member is still in AOI → gone stays false. (dead=true is
        // correct: the set is now all-killed. The point of this pin is the middle bool — a MarkKilled that
        // wrongly cleared Present would report gone=true here and drain the member mid-settle.)
        Assert.Equal((true, false, true), s.Aggregate());
    }

    [Fact]
    public void MarkKilled_is_a_noop_for_an_unknown_id()
    {
        var s = new StageBossSet();
        s.Admit(E(10), 102901);
        s.MarkKilled(E(99));                    // never admitted
        Assert.False(s.MemberAt(0).killed);
    }

    // ── PIN 1 & PIN 3 (composition): the split-poll fix, as a differential over real OnUpdate order ───
    //
    // TickHarness runs the two halves in the exact Plugin.TickBossStatus order — the observation pass
    // (present only under the fix), then the engine-facing half behind ShouldPollBossStatus — composing
    // the REAL pure pieces (StageBossSet, AutoArchiveEngine, Plugin's static guards). Only the IL2CPP
    // vitals read is injected. The control (`observe: false`) is the pre-fix shape: the WHOLE poll is
    // suppressed while a reason is pending, so _memberLastHpFrac freezes and nothing marks Killed.

    private static AutoArchiveInputs Inputs(long nowMs) => new()
    {
        NowMs = nowMs, CombatActive = true, CombatStartMs = 100_000, LastDamageMs = nowMs,
        HasStats = true, RosterSize = 20, DeadCount = 0, UnknownCount = 0,
        InstancedRun = true, FlowStateVersion = 1,
    };

    private sealed class TickHarness
    {
        internal readonly StageBossSet Stage = new();
        internal readonly AutoArchiveEngine Engine = new() { IdleEnabled = false };
        private readonly Dictionary<long, float> _frac = new();   // models Plugin._memberLastHpFrac
        private (bool present, bool gone, bool dead) _status;      // models Plugin._bossStatus
        private readonly bool _observe;

        internal TickHarness(bool observe)
        {
            _observe = observe;
            Assert.Null(Engine.Evaluate(Inputs(200_000)));   // adopt the flow version silently
            Assert.True(Engine.TryBeginBossSegmentCut());    // a boss segment is open — the fight is running
        }

        /// <summary>One OnUpdate tick. <paramref name="v"/> is the injected GetVitals(boss) read;
        /// <paramref name="pending"/> = a deferred archive's settle window is open (isRaid is fixed true —
        /// this is a raid harness).</summary>
        internal ArchiveReason? Tick(EntityId boss, EntityVitals v, bool pending, long nowMs)
        {
            // OBSERVATION pass (ObserveBossKillState) — always-on under the fix, ABSENT in the control.
            if (_observe)
            {
                if (v.HasHpObservation && v.MaxHp > 0) _frac[boss.Value] = (float)v.Hp / v.MaxHp;
                if (Plugin.IsRealBossDeath(v)) Stage.MarkKilled(boss);
            }
            // ENGINE-FACING half (BossStatus) — suppressed while a reason is pending, fix AND control.
            if (Plugin.ShouldPollBossStatus(archivePending: pending))
            {
                if (v.HasHpObservation && v.MaxHp > 0) _frac[boss.Value] = (float)v.Hp / v.MaxHp;
                bool dead = Plugin.IsRealBossDeath(v);
                bool evicted = !v.IsKnown;
                float lastFrac = _frac.TryGetValue(boss.Value, out var f) ? f : -1f;
                bool scripted = Plugin.IsScriptedRaidVanishKill(evicted, isRaid: true, lastFrac);
                Stage.SetLiveness(boss, new StageBossSet.BossLiveness
                {
                    Present = !dead && !evicted,
                    Dead = dead || scripted,
                });
                var agg = Stage.Aggregate();
                if (agg.gone && Plugin.ShouldDrainStageBosses(
                        Plugin.ShouldClearTrackedBoss(agg.dead, Engine.BossSegmentActive), paused: false))
                    Stage.DrainIfAllGone();
                _status = agg;
            }
            if (pending) return null;   // the engine is waiting out the settle window — no fresh cut fires
            return Engine.Evaluate(Inputs(nowMs) with
            {
                BossPresent = _status.present, BossGone = _status.gone, BossDead = _status.dead,
            });
        }
    }

    // PIN 1: a REAL Hp<=0 death observed while an archive is pending → killed=true DURING the pending
    // window (observation), and exactly one BossKill on resume. The control leaves killed=false — the
    // uploaded `bosses[].killed=false` that made the worker derive `partial`.
    [Fact]
    public void A_real_death_during_a_pending_settle_window_still_marks_killed_and_banks_once_on_resume()
    {
        var boss   = E(102901);
        var fixd   = new TickHarness(observe: true);
        var wedged = new TickHarness(observe: false);

        foreach (var h in new[] { fixd, wedged })
        {
            Assert.True(h.Stage.Admit(boss, 102901));
            Assert.Null(h.Tick(boss, At(9_000, 10_000), pending: false, nowMs: 210_000));   // 90 %, fighting
            // A prior archive's settle window opens and the boss dies INSIDE it (reads a real 0).
            Assert.Null(h.Tick(boss, At(0, 10_000),     pending: true,  nowMs: 220_000));
            Assert.Null(h.Tick(boss, Vanished,          pending: true,  nowMs: 230_000));   // corpse evicted
        }

        // Under the fix the observation pass recorded the kill the instant it was seen, mid-settle, WITHOUT
        // draining the set (engine half suppressed) — bosses[]/bossKilled/the derived clear get the truth.
        Assert.Equal(1, fixd.Stage.Count);
        Assert.True(fixd.Stage.MemberAt(0).killed);
        // The pre-fix poll was fully suppressed through the window — the kill was simply never recorded.
        Assert.Equal(1, wedged.Stage.Count);
        Assert.False(wedged.Stage.MemberAt(0).killed);

        // Resume: the fix delivers the pulse on the first un-pending tick and banks EXACTLY once.
        Assert.Equal(ArchiveReason.BossKill, fixd.Tick(boss, Vanished, pending: false, nowMs: 240_000));
        fixd.Engine.OnArchived(240_000, ArchiveReason.BossKill);
        Assert.Null(fixd.Tick(boss, Vanished, pending: false, nowMs: 250_000));   // drained → no double-bank

        // The control never records the kill: on resume the boss is evicted with a frozen 90 % fraction,
        // so neither the real-death path (never observed) nor the scripted rule (fraction above the floor)
        // fires — killed stays false, the exact uploaded bug.
        Assert.Null(wedged.Tick(boss, Vanished, pending: false, nowMs: 240_000));
        Assert.False(GetKilled(wedged, boss));
    }

    // PIN 3: the scripted vanish (5,3,1 → vanish, HP never reads 0) that IS the sea/CvCyokazcx failure.
    // The fix keeps _memberLastHpFrac fresh so the scripted-vanish rule fires on resume; the control's
    // fraction froze above the floor and misses it.
    [Fact]
    public void A_scripted_vanish_during_a_pending_window_kills_under_the_fix_and_is_missed_by_the_control()
    {
        var boss   = E(102901);
        var fixd   = new TickHarness(observe: true);
        var missed = new TickHarness(observe: false);

        foreach (var h in new[] { fixd, missed })
        {
            Assert.True(h.Stage.Admit(boss, 102901));
            // Pre-window reading well above the 15 % floor — this is what froze in the bug.
            Assert.Null(h.Tick(boss, At(5_000, 10_000), pending: false, nowMs: 210_000));   // 50 %
            // The settle window opens and the boss is scripted down to ~1 % then vanishes — HP never 0.
            Assert.Null(h.Tick(boss, At(500, 10_000), pending: true, nowMs: 216_000));      // 5 %
            Assert.Null(h.Tick(boss, At(300, 10_000), pending: true, nowMs: 218_000));      // 3 %
            Assert.Null(h.Tick(boss, At(100, 10_000), pending: true, nowMs: 220_000));      // 1 %
            Assert.Null(h.Tick(boss, Vanished,        pending: true, nowMs: 222_000));      // scripted-killed, gone
        }

        // Resume. The fix's fresh fraction (~1 %) makes the evicted boss a scripted kill → BossKill once.
        Assert.Equal(ArchiveReason.BossKill, fixd.Tick(boss, Vanished, pending: false, nowMs: 230_000));

        // The control's fraction froze at 50 % → the evicted boss is NOT a scripted kill → killed=false,
        // no BossKill. This is the exact sea/CvCyokazcx upload.
        Assert.Null(missed.Tick(boss, Vanished, pending: false, nowMs: 230_000));
        Assert.False(GetKilled(missed, boss));
    }

    // Reads the sticky killed flag for a member that may or may not have drained (drained → false, which
    // the caller pairs with the BossKill return that proves the kill).
    private static bool GetKilled(TickHarness h, EntityId boss)
    {
        for (var i = 0; i < h.Stage.Count; i++)
            if (h.Stage.MemberAt(i).id == boss) return h.Stage.MemberAt(i).killed;
        return false;
    }
}
