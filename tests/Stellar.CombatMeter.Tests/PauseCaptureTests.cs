using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.AutoArchive;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// OWNER RULING 2026-08-14 — <b>capture/tracking is ALWAYS-ON; toggles, flags and PAUSE gate only
/// DECISIONS</b> (cuts, uploads, displayed numbers). "Pause = numbers stop, tracking continues."
///
/// Before this, <c>OnCombatEvent</c> opened with a bare <c>if (_paused) return;</c> that took the
/// CAPTURE channels down with the numbers: a boss engaged while the meter was paused was never admitted
/// into <c>_stageBosses</c>, so it never reached <c>bosses[]</c>, got no per-boss bucket and no HP track
/// — and since a raid CLEAR is derived from the killed SET
/// (docs/recon/raid-clear-and-multiboss.md), a pause across a pull could silently cost the run its
/// clear. Elite capture and replay entity registration went dark for the same reason.
///
/// These pin the SPLIT, not the plumbing: <c>Plugin</c> is IL2CPP-service-bound and cannot be
/// instantiated headless (repo convention — see <c>ReplayCaptureTests</c>), so what is testable is the
/// pure seam each half is dispatched through plus the set-level consequence of admitting while paused.
/// Never weaken: an assertion flipping here means capture went back behind a gate.
/// </summary>
public class PauseCaptureTests
{
    private static EntityId E(long v) => new(v);

    // ── The split itself ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Capture_runs_while_paused_and_accrual_does_not()
    {
        var (capture, accrue) = Plugin.ResolveCombatEventWork(paused: true);
        Assert.True(capture);    // boss admission / elite candidates / replay entities — ALWAYS on
        Assert.False(accrue);    // inline cut, combat-start latches, stats/timelines/buckets — stopped
    }

    [Fact]
    public void Unpaused_runs_both_halves()
    {
        var (capture, accrue) = Plugin.ResolveCombatEventWork(paused: false);
        Assert.True(capture);
        Assert.True(accrue);
    }

    // ── "no stats accrue while paused" ───────────────────────────────────────────────────────────────
    //
    // The accrual half of OnCombatEvent (EnsureCombatStarted, _agg.AddDamage, CaptureTaken, per-source
    // stats/timelines and BOTH bucket stores) sits behind `if (!accrue) return;`, so this single false is
    // what stops every displayed number moving during a pause. EnsureCombatStarted staying on that side
    // is deliberate and unchanged by the ruling: a pause spanning an encounter's first event must not
    // start the combat clock, latch _lastRunId or reset _clearedThisRun.
    [Fact]
    public void Paused_stops_every_accumulator_with_one_decision()
        => Assert.False(Plugin.ResolveCombatEventWork(paused: true).accrue);

    // ── "no cut fires while paused" ──────────────────────────────────────────────────────────────────
    //
    // The inline boss cut is a DECISION — firing it while paused would bank the frozen numbers — so it
    // lives past the same gate. Even with every one of its own terms satisfied (master on, Boss phase on,
    // no segment yet, in an instanced run), MaybeCutForBossPhase is simply never reached while paused.
    [Fact]
    public void No_cut_fires_while_paused_even_when_every_cut_term_is_satisfied()
    {
        Assert.True(Plugin.ShouldConsiderInlineBossCut(
            masterEnabled: true, bossEnabled: true, bossSegmentActive: false, inRun: true));   // would cut…
        Assert.False(Plugin.ResolveCombatEventWork(paused: true).accrue);                      // …but is unreachable
    }

    // ── "fought-with marker advances while paused" (review 2026-08-22) ──────────────────────────────
    //
    // Finding: _combatEventMarker++ sat AFTER the `accrue` veto, so a fight fought entirely while the
    // meter was PAUSED never advanced it — LoadoutCapture.Capture's append-vs-replace decision then
    // misclassified that fought-with setup as an unfought draft and REPLACED it (the exact silent-loss
    // class this arc fixes). Fix: the increment is now gated on ShouldAdvanceFoughtWithMarker(capture),
    // never on accrue. Never weaken: this pin is what keeps the marker pause-immune.
    [Fact]
    public void FoughtWithMarker_advances_while_paused_even_though_accrue_does_not()
    {
        var (capture, accrue) = Plugin.ResolveCombatEventWork(paused: true);
        Assert.False(accrue);                                    // numbers stop…
        Assert.True(Plugin.ShouldAdvanceFoughtWithMarker(capture)); // …but the fought-with marker still moves
    }

    [Fact]
    public void FoughtWithMarker_guard_is_tied_to_capture_not_accrue()
    {
        // Unpaused: both true, so the guard being tied to `capture` rather than `accrue` is invisible —
        // the differential above is what actually pins the split.
        var (capture, accrue) = Plugin.ResolveCombatEventWork(paused: false);
        Assert.True(accrue);
        Assert.True(Plugin.ShouldAdvanceFoughtWithMarker(capture));
    }

    // ── "boss engaged while paused → admitted, and it reaches bosses[]" ──────────────────────────────
    //
    // Admission's guard has ONE term (inRun) and never had a paused one — fix 4 moved its CALL SITE out
    // of MaybeCutForBossPhase and above the pause gate, which is why no signature changed. The two
    // assertions below are the two halves of the owner-facing claim: the guard says yes while paused,
    // and a member admitted during that paused stretch is what the archive-time resolver hands to
    // bosses[] (PreferLiveStageBosses is the pure half of ResolveCurrentStageBosses).
    [Fact]
    public void A_boss_engaged_while_paused_is_admitted_and_lands_in_bosses()
    {
        // The capture half runs while paused, and admission's only gate is "in an instanced run".
        Assert.True(Plugin.ResolveCombatEventWork(paused: true).capture);
        Assert.True(Plugin.ShouldConsiderBossAdmission(inRun: true));

        // What that admission does downstream — the boss is a real member of the stage set…
        var stage = new StageBossSet();
        Assert.True(stage.Admit(E(4242), 102800));
        stage.SetLiveness(E(4242), new StageBossSet.BossLiveness { Present = true, Dead = false });

        // …and the archive-time resolver reports it, so it ships in bosses[] with its config id.
        var resolved = Plugin.PreferLiveStageBosses(
            stage.MembersSnapshot(), System.Array.Empty<(EntityId Id, int ConfigId, bool Killed)>());
        Assert.Single(resolved);
        Assert.Equal(102800, resolved[0].ConfigId);

        // And the kill that follows is still polled while paused (fix 3) — no `paused` term exists.
        Assert.True(Plugin.ShouldPollBossStatus(archivePending: false));
    }

    // ── Raw-event capture is unconditional too (fix 2, same ruling) ─────────────────────────────────
    //
    // The buffer feed used to be `_contentKinds.IsEmpty || <LIVE kind>.stats == Auto`, so a content kind
    // whose stats cell was off/manual buffered NOTHING and a later hand-push had nothing to send — raw
    // events cannot be reconstructed after the fact (the owner's "0 events in 0 chunk(s)" shape). The
    // SEND is still refused by the very same policy at archive time (MaybeUploadLog / TierAllowsUpload —
    // pinned by UploadPolicyResolutionTests / TierGateTests / LogUploadTests, all unchanged).
    [Fact]
    public void Raw_event_capture_ignores_the_stats_cell_entirely()
    {
        var allOff = new UploadPolicyTable();
        foreach (var kind in UploadPolicyTable.Kinds)
        foreach (var artifact in UploadPolicyTable.Artifacts)
            allOff[kind, artifact] = UploadPolicyState.Off;

        // Every cell off, on the kind the player is actually standing in → capture STILL runs…
        Assert.True(Plugin.EventCaptureEnabled(allOff, ContentKind.Other));
        Assert.True(Plugin.EventCaptureEnabled(allOff, ContentKind.Dungeon));

        // …while the SEND for that same cell is refused, unchanged (the gate that does belong to policy).
        Assert.False(UploadPolicy.Allows(UploadPolicyState.Off, UploadTrigger.Auto));
        Assert.False(UploadPolicy.Allows(UploadPolicyState.Off, UploadTrigger.Manual));

        // `manual` is the other non-auto state the old gate withheld capture for — it needs samples on
        // hand precisely so the user's hand-push has something to send.
        var manualStats = new UploadPolicyTable();
        manualStats[ContentKind.Raid, UploadArtifact.Stats] = UploadPolicyState.Manual;
        Assert.True(Plugin.EventCaptureEnabled(manualStats, ContentKind.Raid));
        Assert.False(UploadPolicy.Allows(UploadPolicyState.Manual, UploadTrigger.Auto));   // auto send still refused
    }

    // ── THE WEDGE PIN: polling while paused must not EAT the one-tick BossKill pulse ─────────────────
    //
    // Review Critical against commit 8b0d136. Fix 3 dropped the `paused` term from ShouldPollBossStatus so
    // kill state keeps updating through a pause — correct, and the owner's ask — but the poll's DESTRUCTIVE
    // half went with it. BossStatus DRAINS _stageBosses on the tick its aggregate first reads all-gone,
    // after which it returns the empty-set (false,false,false) tuple for good; the engine consumes BossDead
    // as a ONE-TICK PULSE and cannot tick at all while paused (TickAutoArchiveTriggers early-returns on
    // _paused). So a boss dying mid-pause raised agg.dead exactly once, LatchStageBosses+DrainIfAllGone
    // emptied the set on that same tick, and nobody ever saw the pulse: on resume _bossKillWanted never
    // arms, the BossGone streak never accumulates, _bossSegmentActive stays latched, and
    // ShouldConsiderInlineBossCut's !bossSegmentActive gate then bars EVERY later stage from cutting — the
    // § 13 run-wide P0 wedge (one giant archive instead of one per boss, owner run sea/aDSR2VkdmT), the
    // exact shape AutoArchiveEngine.BossGoneTimeoutMs was written to fix. The commit kept `archivePending`
    // for this IDENTICAL mechanism; ShouldDrainStageBosses is the paused half of the same term.
    //
    // DIFFERENTIAL (agent-process-rules § 23 — a test that cannot fail is worse than no test): both engines
    // run the SAME tick script; the only difference is whether the drain goes through the new guard or
    // through the pre-fix shape (ShouldClearTrackedBoss alone). `fixed` fires BossKill exactly once on
    // resume; `wedged` fires NOTHING, ever, and is still holding an open boss segment at the end. Delete
    // the guard and the fixed half turns red.

    private static AutoArchiveInputs Live(long nowMs) => new()
    {
        NowMs = nowMs, CombatActive = true, CombatStartMs = 100_000, LastDamageMs = 160_000,
        HasStats = true, RosterSize = 4, DeadCount = 0, UnknownCount = 0,
        InstancedRun = true, FlowStateVersion = 1,
    };

    /// <summary>The real pure pieces composed in the real <c>Plugin.OnUpdate</c> order — TickBossStatus
    /// (guarded read, then guarded drain) and then TickAutoArchiveTriggers (pause early-return, then the
    /// engine). Only the IL2CPP vitals read is injected, exactly as <c>StageBossSet</c> takes liveness.
    /// <paramref name="preFixDrain"/> swaps in the 8b0d136 drain condition to produce the control.</summary>
    private sealed class TickHarness
    {
        internal readonly StageBossSet Stage = new();
        internal readonly AutoArchiveEngine Engine = new() { IdleEnabled = false };
        private (bool present, bool gone, bool dead) _status;   // Plugin's cached _bossStatus
        private readonly bool _preFixDrain;

        internal TickHarness(bool preFixDrain = false)
        {
            _preFixDrain = preFixDrain;
            Assert.Null(Engine.Evaluate(Live(200_000)));   // adopt the flow version silently
            Assert.True(Engine.TryBeginBossSegmentCut());  // a boss segment is open — the fight is running
        }

        internal ArchiveReason? Tick(EntityId boss, bool present, bool dead, bool paused, long nowMs)
        {
            if (Plugin.ShouldPollBossStatus(archivePending: false))
            {
                Stage.SetLiveness(boss, new StageBossSet.BossLiveness { Present = present, Dead = dead });
                var agg = Stage.Aggregate();
                bool clearAllowed = Plugin.ShouldClearTrackedBoss(agg.dead, Engine.BossSegmentActive);
                if (agg.gone && (_preFixDrain ? clearAllowed
                                              : Plugin.ShouldDrainStageBosses(clearAllowed, paused)))
                    Stage.DrainIfAllGone();
                _status = agg;
            }
            if (paused) return null;   // TickAutoArchiveTriggers' own `if (_paused) return;`
            return Engine.Evaluate(Live(nowMs) with
            {
                BossPresent = _status.present, BossGone = _status.gone, BossDead = _status.dead,
            });
        }
    }

    [Fact]
    public void A_boss_that_dies_while_paused_still_banks_its_BossKill_on_resume()
    {
        var boss  = E(102800);
        var fixd  = new TickHarness();
        var wedged = new TickHarness(preFixDrain: true);   // the 8b0d136 shape, for the differential

        foreach (var h in new[] { fixd, wedged })
        {
            Assert.True(h.Stage.Admit(boss, 102800));
            Assert.Null(h.Tick(boss, present: true,  dead: false, paused: false, nowMs: 210_000));  // pull

            // ── PAUSED: the boss dies. Kill state MUST still update (owner ruling — that is why the poll
            //    keeps running), and the stage must stay readable so the pulse survives to the resume tick.
            Assert.Null(h.Tick(boss, present: false, dead: true,  paused: true,  nowMs: 220_000));
            Assert.Null(h.Tick(boss, present: false, dead: false, paused: true,  nowMs: 230_000));  // vitals evicted
        }

        // Kill state updated under the fix — the member is still there and reads KILLED, so bosses[] /
        // bossKilled / the derived raid clear all get the truth (docs/recon/raid-clear-and-multiboss.md).
        Assert.Equal(1, fixd.Stage.Count);
        Assert.True(fixd.Stage.MemberAt(0).killed);
        // The pre-fix shape threw the whole stage away mid-pause — nothing left to report or to pulse.
        Assert.Equal(0, wedged.Stage.Count);

        // ── RESUME. The fixed harness delivers the pulse on the first unpaused tick and banks ONCE.
        Assert.Equal(ArchiveReason.BossKill, fixd.Tick(boss, false, false, paused: false, nowMs: 240_000));
        fixd.Engine.OnArchived(240_000, ArchiveReason.BossKill);
        Assert.Equal(0, fixd.Stage.Count);          // drained now that a consumer actually read it
        Assert.False(fixd.Engine.BossSegmentActive); // fight closed → later stages can cut again

        // ── The control never recovers: no BossKill, and no gone-timeout rescue either (the emptied set
        //    reports BossGone=false, so the streak never even starts). Run well past BossGoneTimeoutMs.
        for (long t = 240_000; t <= 260_000; t += 1_000)
            Assert.Null(wedged.Tick(boss, false, false, paused: false, nowMs: t));
        Assert.True(wedged.Engine.BossSegmentActive);   // STILL latched open — this is the § 13 wedge
    }

    [Fact]
    public void The_drain_guard_vetoes_only_while_paused()
    {
        // Unpaused, the guard is a pass-through: ShouldClearTrackedBoss keeps deciding, alone.
        Assert.True(Plugin.ShouldDrainStageBosses(clearAllowed: true,  paused: false));
        Assert.False(Plugin.ShouldDrainStageBosses(clearAllowed: false, paused: false));
        // Paused, the drain is refused even when the clear rule allows it — the pulse must outlive the
        // pause. Never weaken: this single false is what keeps a mid-pause kill from wedging the run.
        Assert.False(Plugin.ShouldDrainStageBosses(clearAllowed: true,  paused: true));
        Assert.False(Plugin.ShouldDrainStageBosses(clearAllowed: false, paused: true));
        // And the READ half stays always-on — the two halves are one fix, not a re-gating of the poll.
        Assert.True(Plugin.ShouldPollBossStatus(archivePending: false));
    }

    // ── Minor 1: the summon NOVELTY mark is a tracker, so it runs through pause too ──────────────────
    //
    // SeenSummonSet answers "have I seen this summon ENTITY this run" — the guard that stops an AOI blink
    // of a long-lived companion being read as a fresh cast. It used to be marked from inside
    // TryRecordImagineCastFromAppear, i.e. past OnCombatEvent's pause gate, so a companion that spawned
    // while the meter was paused was never marked; its first re-appear after the unpause then read as
    // NOVEL and recorded a PHANTOM cast. ObserveSummonNovelty (Plugin.CaptureAlwaysOn.cs) now marks
    // through pause, and hands isSelf/novel to the record path so MarkSeen still runs exactly once per
    // appear. Losing the paused cast is the CORRECT outcome — it happened while numbers were stopped.
    //
    // SCOPE, stated honestly: this pins the CONSEQUENCE (marked-during-pause ⇒ repeat, unmarked ⇒ phantom
    // Record) over the real SeenSummonSet and the real DecideAppearCast. The wiring itself — that
    // ObserveSummonNovelty is reached before OnCombatEvent's `accrue` gate — is IL2CPP-bound (it reads
    // CombatSnapshot.LocalEntityId) and headless-untestable, the same documented gap BossStatus carries.
    [Fact]
    public void A_summon_first_seen_while_paused_is_not_a_new_cast_after_the_unpause()
    {
        var summon = E(9001);

        // With the always-on mark: the paused appear consumes the novelty, so the post-unpause re-appear
        // of the SAME entity is correctly a repeat — no phantom cast.
        var marked = new SeenSummonSet();
        Assert.True(marked.MarkSeen(summon));    // during the pause (always-on)
        Assert.False(marked.MarkSeen(summon));   // the re-appear after unpausing
        Assert.Equal(Plugin.AppearCastGate.RepeatSummon,
            Plugin.DecideAppearCast(summonerIsSelf: false, summonNovel: false, ownerIsKnownCombatant: true));

        // DIFFERENTIAL: with the mark still behind the pause gate, that same re-appear reads as novel and
        // the channel records a cast that never happened.
        var unmarked = new SeenSummonSet();
        Assert.True(unmarked.MarkSeen(summon));  // the FIRST call is the re-appear — the pause ate the spawn
        Assert.Equal(Plugin.AppearCastGate.Record,
            Plugin.DecideAppearCast(summonerIsSelf: false, summonNovel: true, ownerIsKnownCombatant: true));
    }
}
