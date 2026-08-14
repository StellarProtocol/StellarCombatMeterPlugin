using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

// ALWAYS-ON CAPTURE — the half of OnCombatEvent that runs THROUGH pause.
//
// OWNER RULING 2026-08-14, verbatim shape: "capture/tracking is ALWAYS-ON by default; toggles, flags
// and pause gate only DECISIONS (cuts, uploads, displayed numbers). Pause = numbers stop, tracking
// continues." Before this file existed, `if (_paused) return;` sat at the very top of OnCombatEvent
// (Plugin.Capture.cs:17) and took the CAPTURE channels down with the numbers: a boss engaged while the
// meter was paused was never admitted into _stageBosses, so it never appeared in bosses[], never got a
// per-boss bucket or HP track, and — because the raid clear verdict is derived from the killed SET
// (docs/recon/raid-clear-and-multiboss.md) — a pause across a boss pull could silently cost the run its
// clear. Elites (capture-only channel, ruling 2026-08-13) and replay entity registration went dark for
// the same reason, and a replay that stops noting entities mid-run is the replay-clip P0 shape.
//
// The split, and why each side sits where it does:
//   CAPTURE (here, always) — boss ADMISSION, elite candidates, replay entity noting. All three only
//     RECORD what the wire already showed; none of them can bank an archive, move a number the user
//     reads, or change a verdict. Boss admission's downstream consumers are all transitively gated on
//     _bossSegmentActive (traced in full on ObserveAutoArchiveBoss, Plugin.BossDetection.cs), which
//     only the CUT can set — so admitting while paused decides nothing.
//   DECISIONS + ACCUMULATION (Plugin.Capture.cs, past the pause gate) — the inline boss CUT
//     (MaybeCutForBossPhase), EnsureCombatStarted's combat/run-id/clear latches, _agg, per-source
//     stats, timelines and the per-boss/elite buckets. A cut firing while paused would BANK the frozen
//     numbers, so it stays gated; accrual stays gated because that IS "numbers stop".
//
// EnsureCombatStarted semantics are deliberately UNCHANGED for the paused case: it still runs only past
// the pause gate, so a pause that spans an encounter's first event does not start the combat clock,
// does not latch _lastRunId and does not reset _clearedThisRun — exactly as before this fix. Boss/elite
// HP tracks are anchored on the REPLAY clock (_replay.CombatStartMs, Plugin.Replay.cs), not
// _combatStartMs, so they keep sampling through a pause regardless.
public sealed partial class Plugin
{
    /// <summary>Pure split of one combat event's work under PAUSE (owner ruling 2026-08-14). Returns
    /// which halves of <c>OnCombatEvent</c> may run: <c>capture</c> — boss admission / elite candidates /
    /// replay entity noting — is <b>unconditionally true</b>, while <c>accrue</c> — the inline boss cut,
    /// the combat-start latches and every stats/timeline/bucket accumulator — is true only when NOT
    /// paused. <paramref name="paused"/> is kept on the capture side of the tuple ON PURPOSE: it is what
    /// makes "pause never stops capture" a compiled, test-visible claim rather than a comment, so
    /// re-adding a pause gate to the capture half turns this seam's pin red instead of silently
    /// re-breaking tracking. Pinned headless (Plugin cannot be instantiated in tests — repo pattern, see
    /// <see cref="ObserveBurstHit"/>).</summary>
    internal static (bool capture, bool accrue) ResolveCombatEventWork(bool paused) => (true, !paused);

    /// <summary>Pure guard: should this event be offered to <see cref="ObserveAutoArchiveBoss"/> for
    /// ADMISSION into <c>_stageBosses</c>? <b>ONE term only — <paramref name="inRun"/>.</b>
    /// <para><b>OWNER RULING 2026-08-14</b> ("boss tracking is supposed to be a default feature"):
    /// admission is ALWAYS-ON during an instanced run, INDEPENDENT of the "Boss phase" sub-toggle —
    /// like elite capture (Plugin.EliteDetection.cs, ruling 2026-08-13).
    /// Extends protected invariant 5 ("detection is always-on; the toggle gates only per-boss CUTS")
    /// from the retired single <c>bossId</c> latch to the multi-boss SET: with Boss phase OFF a fight
    /// still admits members, fills <c>bosses[]</c>/buckets/HP tracks and records <c>bossId</c>.</para>
    /// <para>All three dropped parameters are <b>GONE, not defaulted</b> — each omission IS its fix
    /// (<c>bossSegmentActive</c>, 2026-08-12, the co-boss fix; <c>bossEnabled</c>, 2026-08-14, the
    /// toggle ruling; and it never took a <c>paused</c> term, which is why moving the call site OUT of
    /// <c>MaybeCutForBossPhase</c> and above the pause gate — fix 4, same ruling — needed no signature
    /// change). Compare <see cref="ShouldConsiderInlineBossCut"/>, which KEEPS its toggles and gained
    /// the master one: the CUT is a decision (invariant 8). <b>WHY UN-GATING IS SAFE with the toggle
    /// off is traced in full on <c>ObserveAutoArchiveBoss</c> (Plugin.BossDetection.cs) — read it
    /// before re-gating.</b></para>
    /// Unit-tested headless; set-level effect pinned by <c>StageBossSetTests</c>.</summary>
    internal static bool ShouldConsiderBossAdmission(bool inRun) => inRun;

    /// <summary>The always-on capture half of <c>OnCombatEvent</c>, run for EVERY <c>DamageDealt</c>
    /// — paused or not, master/sub toggles on or off. Ordering notes:
    /// <list type="bullet">
    /// <item>It runs AFTER <c>ResolveArmedBoundaryBelt</c> and never before it: a boundary commit resets
    /// the run-scoped trackers (and LATCHES the outgoing stage's bosses on the way out), so admitting
    /// first would attribute this event's boss to the run that is ending.</item>
    /// <item>It runs BEFORE <c>MaybeCutForBossPhase</c>, preserving the admission→cut order the inline
    /// cut depends on (the cut's <c>EventInvolvesAnyStageBoss</c> test needs THIS event's boss to be in
    /// the set already).</item>
    /// <item>Elite candidates + replay entities moved UP from below the accumulators (they used to sit
    /// between <c>CaptureTaken</c> and <c>AccumulateDamage</c>). One deliberate consequence: an elite
    /// attacker's FIRST incoming hit now routes to the elite bucket instead of
    /// <c>TargetBucketStats.OtherKey</c>, because <c>CaptureTaken</c>'s <c>ResolveTargetBucket</c> now
    /// sees the admission from this same event — the symmetric behaviour boss admission always had.
    /// Σbuckets == totals is unaffected (routing changes which key, never whether it accrues).</item>
    /// </list>
    /// Hot path: alloc-free once <c>_bossCheck</c> is warm; each channel carries its own
    /// already-tracked fast path.</summary>
    private void ObserveAlwaysOnCapture(CombatEvent.DamageDealt d)
    {
        // Boss ADMISSION — always-on in an instanced run. Not gated on bossSegmentActive (Critical fix
        // 2026-08-12: a co-boss engaged mid-fight is still admitted), not on the Boss-phase toggle
        // (ruling 2026-08-14), not on the master toggle, and — since fix 4 — not on pause.
        if (ShouldConsiderBossAdmission(IsInstancedRun())) ObserveAutoArchiveBoss(d.SourceId, d.TargetId);

        // Elite capture (ELITE CAPTURE channel, owner ruling 2026-08-13, Plugin.EliteDetection.cs):
        // toggle-independent and now pause-independent, feeding ONLY the capture-only _eliteSet — never
        // AutoArchive/BossStatus/verdict/bossId paths, which remains the difference that matters.
        ObserveEliteCandidates(d.SourceId, d.TargetId);

        // Replay: note BOTH source and target (the player-only early-out lives further down the
        // accumulate half), so boss/add ids enter the entity set for position tracking. Pause must never
        // stop this — an unsampled stretch of a run is an unrecoverable replay gap (P0: dungeon entry →
        // run end).
        NoteReplayEntity(d.SourceId, d.TargetId);
    }
}
