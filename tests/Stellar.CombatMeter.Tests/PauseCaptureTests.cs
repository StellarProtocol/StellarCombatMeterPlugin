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
}
