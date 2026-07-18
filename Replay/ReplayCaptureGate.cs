namespace Stellar.CombatMeter.Replay;

/// <summary>
/// Pure decisions for WHEN the replay records (spec 2026-07-19 walk-in/lobby capture).
/// Three states: Off (town/open world), Provisional (instanced-candidate scene, run-id
/// not yet latched — captures the walk-in / raid lobby), Committed (run-id latched —
/// today's behavior; the provisional buffer is adopted as the walk-in). All methods are
/// pure so the transition matrix is unit-tested (ReplayCaptureGateTests).
/// </summary>
internal static class ReplayCaptureGate
{
    /// <summary>
    /// SceneKind values (game scene table, via IGameData.World.GetScene) classified as
    /// instanced-candidate content. Source: recon/scene-kind-notes.md (devkit) — dungeon
    /// interiors/approaches + raid lobbies/rooms; towns and open-world kinds excluded so
    /// the open-world tracks=0 invariant (FPS-leak fix) holds.
    /// </summary>
    private static readonly int[] CandidateSceneKinds = { 2 };  // SceneType=2: all instanced content (recon/scene-kind-notes.md § 4)

    internal static bool IsCandidateScene(int sceneKind)
    {
        for (int i = 0; i < CandidateSceneKinds.Length; i++)
            if (CandidateSceneKinds[i] == sceneKind) return true;
        return false;
    }

    /// <summary>Replay Active predicate: committed run OR provisional candidate scene.</summary>
    internal static bool ShouldCapture(long runId, bool sceneIsCandidate)
        => runId != 0 || sceneIsCandidate;

    /// <summary>
    /// Reset on a run-id CHANGE? 0→snowflake adopts the provisional buffer (the walk-in);
    /// snowflake→different-snowflake wipes (Bug-2 crash-re-enter fix); snowflake→0 keeps
    /// (the dungeon→town archive window reads the buffer at archive time).
    /// </summary>
    internal static bool ShouldResetOnRunIdChange(long previousRunId, long newRunId)
        => previousRunId != 0 && newRunId != 0;

    /// <summary>
    /// Reset at a scene boundary (OnSceneChanged)? Keep ONLY the provisional
    /// candidate→candidate hop (raid lobby → boss room before the run starts). Everything
    /// else resets — entering a candidate from town starts fresh (no prior-run leftovers can
    /// leak into the adoption path), leaving to town discards, and committed scene changes
    /// keep today's per-segment archive semantics (93:53 carryover protection).
    /// </summary>
    internal static bool ShouldResetOnSceneChange(long runId, bool outgoingCandidate, bool incomingCandidate)
        => !(runId == 0 && outgoingCandidate && incomingCandidate);
}
