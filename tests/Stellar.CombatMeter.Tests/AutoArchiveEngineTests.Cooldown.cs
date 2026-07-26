using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// The inline boss-cut gate (TryBeginBossSegmentCut once-per-fight + run-boundary re-arm) and the
// shared gates that span every trigger (cooldown, no-stats, master/per-trigger toggles). Split out
// of AutoArchiveEngineTests.cs (2026-07-26, review round) — see that file's banner for the full
// partial map. Live()/Armed() live there.
public partial class AutoArchiveEngineTests
{
    // ---- inline boss-phase cut gate: TryBeginBossSegmentCut ----
    // The boss cut is INLINE (Plugin.Capture.cs, fires at the first boss hit, before accumulation) and
    // is the SOLE boss-cut path — the engine's old Evaluate boss branch was removed (recut-fix,
    // 2026-07-21; see Evaluate_never_returns_bossphase). These tests pin the once-per-fight +
    // re-arm-per-run protections the removed Evaluate-based boss tests used to cover, now driven
    // through the real production gate: TryBeginBossSegmentCut() + UpdateLatches (via Evaluate
    // ticks) + OnArchived. The behaviors preserved from the deleted tests:
    //   • one-fight-one-cut       → TryBeginBossSegmentCut_fires_once_then_gates_until_rearm
    //   • re-arm on run boundary  → TryBeginBossSegmentCut_rearms_on_run_boundary
    // The removed MinBossSegment / _bossPending (cooldown-bank) tests pinned mechanics that no longer
    // exist in the deterministic inline model (no min-segment floor, no cooldown-swallow-then-rebank).
    //
    // 2026-07-26 (Task 2): a raw BossDead/BossGone reading no longer closes the segment by itself — only
    // an actual archive (via OnArchived) does — so the confirmed-death re-arm and BossRecutOnRedetect
    // branch tests that used to live in this block are retired; their coverage now lives in the BossKill
    // block above:
    //   • re-arm on confirmed death (recut off)  → BossKill_fires_on_confirmed_death_while_segment_open
    //                                                + Any_nonboss_archive_closes_the_segment_so_a_wipe_retry_recuts
    //   • NO re-arm on transient eviction (off)   → Transient_eviction_never_ends_the_segment
    //   • re-arm on any "gone" (recut on)         → retired outright — BossRecutOnRedetect is retired in Task 4
    //   • non-boss archive re-arm (recut on)      → Any_nonboss_archive_closes_the_segment_so_a_wipe_retry_recuts
    //                                                + A_bossphase_archive_does_not_close_the_segment_it_just_opened
    //   • non-boss archive NO re-arm (recut off)  → same two tests above — the close is now unconditional
    //
    // 2026-07-27 (finding 1, review round): TryBeginBossSegmentCut's nowMs parameter and its
    // _lastArchiveMs/CooldownMs consultation are REMOVED (deliberate contract withdrawal, not a bug
    // fix regression). Retired below:
    //   • Inline_boss_cut_respects_the_shared_cooldown
    //   • Inline_boss_cut_is_allowed_before_any_archive_has_happened
    // Both pinned a check that had a worse failure mode than the spam it guarded against: while the
    // cooldown held, the cut just didn't happen and the new boss's damage kept piling into the still-
    // open PREVIOUS segment; once the cooldown lifted, the delayed cut fired with priorCombat now true
    // and banked the fight's own opening seconds as a "boss" TRASH archive (with a 60s Min gap, the
    // first MINUTE of the fight). Replaced by ONE test pinning the opposite, now-correct contract:
    // Inline_boss_cut_is_never_blocked_by_a_recent_archive.

    [Fact]
    public void Recut_flag_is_gone_from_the_engine_surface()
        // Pins the retirement: the knob must not come back as a live field. Re-adding it would restore
        // a path that can cut mid-fight (the 2026-07-26 defect class).
        => Assert.Null(typeof(AutoArchiveEngine).GetField("BossRecutOnRedetect"));

    [Fact]
    public void TryBeginBossSegmentCut_fires_once_then_gates_until_rearm()
    {
        var e = new AutoArchiveEngine();
        Assert.True(e.TryBeginBossSegmentCut());    // first boss this fight → cut permitted, marks segment active
        Assert.False(e.TryBeginBossSegmentCut());   // segment active → one fight, one cut
    }

    [Fact]
    public void TryBeginBossSegmentCut_blocked_when_boss_disabled()
    {
        var e = new AutoArchiveEngine { BossEnabled = false };
        Assert.False(e.TryBeginBossSegmentCut());
    }

    [Fact]
    public void TryBeginBossSegmentCut_rearms_on_run_boundary()
    {
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));                   // adopt flow version
        Assert.True(e.TryBeginBossSegmentCut());    // first cut this run
        Assert.False(e.TryBeginBossSegmentCut());   // gated within the run
        // Leaving the instanced run re-arms the segment latch (UpdateLatches, every tick).
        Assert.Null(e.Evaluate(Live() with { InstancedRun = false, BossPresent = false }));
        Assert.True(e.TryBeginBossSegmentCut());    // next run's boss cuts fresh
    }

    [Fact]
    public void Inline_boss_cut_is_never_blocked_by_a_recent_archive()
    {
        // REPLACES Inline_boss_cut_respects_the_shared_cooldown / Inline_boss_cut_is_allowed_before_
        // any_archive_has_happened (finding 1, review round 2026-07-27 — see the banner comment above
        // for the full withdrawal rationale). Mirrors the retired _respects_the_shared_cooldown test's
        // exact shape (same OnArchived timing this used to gate on) with the assertion flipped: the
        // spam the old cooldown check guarded against is already structurally impossible without it —
        // a killed boss can never be re-adopted (KilledBossTracker.MarkKilled / IsKilled) and
        // TryBeginBossSegmentCut cannot fire again while a segment is open (_bossSegmentActive) — so
        // cut-spam would require ARCHIVE-spam, and Min gap still blocks that everywhere it always did
        // (Evaluate's own fire gate, exercised by Cooldown_spans_all_triggers_including_manual_archives
        // below). A boss pull must therefore cut immediately no matter how recently the previous
        // archive landed (there is no more _lastArchiveMs consultation here at all, so "before any
        // archive has happened" is no longer a distinct case either — nothing is ever consulted).
        var e = new AutoArchiveEngine { CooldownMs = 10_000 };
        Assert.Null(e.Evaluate(Live()));            // adopt flow version
        e.OnArchived(200_000, ArchiveReason.Wipe);  // arms the shared cooldown
        Assert.True(e.TryBeginBossSegmentCut());    // 5 s in (would've been "inside the cooldown") — cuts anyway
    }

    // ---- shared gates ----

    [Fact]
    public void Cooldown_spans_all_triggers_including_manual_archives()
    {
        // Restored to ORIGINAL (round 1) timing/assertions per round-3 integrity rule: a scene
        // archive arms the cooldown; allDead rises INSIDE that window (suppressed); the SAME
        // allDead level, still true, fires the instant the window lifts — no revive, no fresh
        // OutcomeFailed edge needed. This is exactly the "allDead rising during an unrelated
        // cooldown then staying true fires on lift" behavior round 3 restores (see
        // <see cref="Wipe_alldead_rises_during_unrelated_cooldown_then_fires_on_lift"/>): the level
        // condition persists through the suppressed tick because `_wipeArchived` is only latched
        // true at the moment of an actual fire, never while cooldown-suppressed.
        var e = Armed(Live());
        e.WipeGraceMs = 0;   // the fire below lands only 2ms after allDead turns true (well under the 2000ms default grace) — isolate from revive-grace
        e.OnArchived(Live().NowMs, ArchiveReason.SceneChange);    // scene archive arms the cooldown
        var s = Live() with { DeadCount = 4, NowMs = Live().NowMs + AutoArchiveEngine.DefaultCooldownMs - 1 };
        Assert.Null(e.Evaluate(in s));                            // wipe suppressed inside the window
        var later = s with { NowMs = s.NowMs + 2 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in later));
    }

    [Fact]
    public void Cooldown_is_configurable()
    {
        var e = Armed(Live());
        e.WipeGraceMs = 0;   // isolate CooldownMs configurability from the unrelated revive-grace
                              // debounce — both fires below land on the SAME tick allDead turns true
        e.CooldownMs = 30_000;
        var dead = Live() with { DeadCount = 4, NowMs = 210_000 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in dead));
        e.OnArchived(dead.NowMs, ArchiveReason.Wipe);
        var revived = dead with { DeadCount = 0, NowMs = dead.NowMs + 1000 };  // re-arm the episode
        Assert.Null(e.Evaluate(in revived));
        var deadAgain = revived with { DeadCount = 4, NowMs = dead.NowMs + 20_000 }; // <30s cooldown
        Assert.Null(e.Evaluate(in deadAgain));
        var later = deadAgain with { NowMs = dead.NowMs + 30_001 };            // past 30s
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in later));
    }

    [Fact]
    public void No_stats_means_no_fire()
    {
        var e = Armed(Live());
        var s = Live() with { HasStats = false, DeadCount = 4 };
        Assert.Null(e.Evaluate(in s));
    }

    [Fact]
    public void Toggles_off_never_fire()
    {
        var e = Armed(Live());
        e.WipeEnabled = false; e.BossEnabled = false; e.IdleEnabled = false; e.StageEnabled = false;
        var s = Live() with
        {
            DeadCount = 4, BossPresent = true, FlowStateVersion = 2, CurrentFlowState = DungeonFlowState.End,
            NowMs = 160_000 + 300_001,
        };
        Assert.Null(e.Evaluate(in s));
    }

    [Fact]
    public void Master_disabled_never_fires()
    {
        // Fix 1 (review round): the master on/off gate used to live ONLY in Plugin.AutoArchive.cs
        // (untestable plugin field) — moved onto the engine (Enabled) so the policy itself is pinned
        // here. Placed after the wipe/UpdateLatches bookkeeping in Evaluate, so re-enabling with the
        // SAME still-true input fires immediately — no stale edge was lost while disabled.
        var e = Armed(Live());
        e.WipeGraceMs = 0;   // would-fire on the SAME tick allDead turns true — isolate from revive-grace
        e.Enabled = false;
        var s = Live() with { DeadCount = 4 };
        Assert.Null(e.Evaluate(in s));                          // master gate suppresses the would-fire wipe
        e.Enabled = true;
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in s));      // re-enabled — same input now fires
    }
}
