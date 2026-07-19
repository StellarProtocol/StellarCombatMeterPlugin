using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Pure gates for WHEN the replay records (spec 2026-07-19): provisional capture in
// instanced-candidate scenes before the run-id latches, adoption on the 0->snowflake
// latch, and the two reset paths that keep the Bug-2 / 93:53 carryover protections.
public class ReplayCaptureGateTests
{
    private const long Run = 1_234_567_890_123_456_789; // > 2^53 snowflake
    private const long OtherRun = 1_234_567_890_999_999_999;

    // --- Active predicate ---

    [Fact] public void Committed_run_captures_regardless_of_scene()
        => Assert.True(ReplayCaptureGate.ShouldCapture(Run, sceneIsCandidate: false));

    [Fact] public void Candidate_scene_captures_provisionally_before_latch()
        => Assert.True(ReplayCaptureGate.ShouldCapture(0, sceneIsCandidate: true));

    [Fact] public void Town_without_run_does_not_capture()
        => Assert.False(ReplayCaptureGate.ShouldCapture(0, sceneIsCandidate: false));

    // --- run-id transition: adopt the provisional buffer, keep Bug-2 wipe ---

    [Fact] public void Latch_from_zero_adopts_buffer_no_reset()
        => Assert.False(ReplayCaptureGate.ShouldResetOnRunIdChange(0, Run));

    [Fact] public void Run_to_different_run_still_wipes()   // crash -> re-enter (Bug 2)
        => Assert.True(ReplayCaptureGate.ShouldResetOnRunIdChange(Run, OtherRun));

    [Fact] public void Run_to_zero_keeps_buffer_for_archive()  // dungeon -> town archive window
        => Assert.False(ReplayCaptureGate.ShouldResetOnRunIdChange(Run, 0));

    // --- scene-boundary reset (the Plugin.History OnSceneChanged path) ---

    [Fact] public void Provisional_candidate_to_candidate_hop_keeps_buffer()  // raid lobby -> boss room
        => Assert.False(ReplayCaptureGate.ShouldResetOnSceneChange(0, outgoingCandidate: true, incomingCandidate: true));

    [Fact] public void Entering_candidate_from_town_resets_first()  // Off -> Provisional fresh start
        => Assert.True(ReplayCaptureGate.ShouldResetOnSceneChange(0, outgoingCandidate: false, incomingCandidate: true));

    [Fact] public void Leaving_to_town_without_latch_discards()
        => Assert.True(ReplayCaptureGate.ShouldResetOnSceneChange(0, outgoingCandidate: true, incomingCandidate: false));

    [Fact] public void Committed_scene_change_keeps_todays_per_segment_reset()
        => Assert.True(ReplayCaptureGate.ShouldResetOnSceneChange(Run, outgoingCandidate: true, incomingCandidate: true));

    // --- loading-screen settle re-arm (tick gap = the framework tick was gated off) ---

    [Fact] public void Long_tick_gap_rearms_settle()          // loading screen resume
        => Assert.True(ReplayCaptureGate.ShouldRearmSettleAfterTickGap(nowMs: 10_000, lastTickMs: 2_000));

    [Fact] public void Normal_frame_interval_does_not_rearm()
        => Assert.False(ReplayCaptureGate.ShouldRearmSettleAfterTickGap(nowMs: 10_033, lastTickMs: 10_000));

    [Fact] public void First_ever_tick_rearms()               // lastTickMs=0 at boot — harmless 2s settle
        => Assert.True(ReplayCaptureGate.ShouldRearmSettleAfterTickGap(nowMs: 10_000, lastTickMs: 0));

    [Fact] public void Backwards_clock_does_not_rearm()       // never wedge on a clock step
        => Assert.False(ReplayCaptureGate.ShouldRearmSettleAfterTickGap(nowMs: 5_000, lastTickMs: 10_000));

    [Fact] public void Gap_exactly_at_threshold_rearms()
        => Assert.True(ReplayCaptureGate.ShouldRearmSettleAfterTickGap(nowMs: 3_000, lastTickMs: 2_000));

    // --- classification ---

    [Fact] public void Unknown_kind_is_not_candidate()
        => Assert.False(ReplayCaptureGate.IsCandidateScene(int.MinValue));

    // Real SceneType values from recon/scene-kind-notes.md § 2 (devkit):
    [Fact] public void Instanced_content_kind_is_candidate()      // SceneType=2: dungeons/raids
        => Assert.True(ReplayCaptureGate.IsCandidateScene(2));

    [Fact] public void Shared_open_world_kind_is_not_candidate()  // SceneType=1: town + field
        => Assert.False(ReplayCaptureGate.IsCandidateScene(1));

    [Fact] public void Login_and_memory_kind_is_not_candidate()   // SceneType=0
        => Assert.False(ReplayCaptureGate.IsCandidateScene(0));
}
