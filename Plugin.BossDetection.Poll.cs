namespace Stellar.CombatMeter;

// The per-frame BOSS KILL-STATE POLL — its cached aggregate, the two pure guards that decide whether a
// tick may READ and whether it may DRAIN, and the tick entry point itself. Split out of
// Plugin.BossDetection.cs (2026-08-14, review Critical): that file sat at 488 LoC and this fix's guard
// would have pushed it past the 500-LoC major threshold — "split before adding more" (CLAUDE.md § SOLID).
// The section already carried its own banner there and moves VERBATIM apart from that banner; the sibling
// file keeps identity (WHICH entity is a boss), lifetime (BossStatus's vitals loop) and admission.
//
// The two guards are one fix in two halves and must be read together (owner ruling 2026-08-14 + the
// review Critical that followed it the same day): ShouldPollBossStatus decides whether kill state UPDATES
// this tick — always-on, master-toggle- and pause-independent — while ShouldDrainStageBosses decides
// whether the stage may be CLOSED, which only a tick the auto-archive engine can consume may do.

public sealed partial class Plugin
{
    // Latest per-frame boss liveness aggregate, produced by TickBossStatus below (the SINGLE call site of
    // BossStatus) and consumed by BuildAutoArchiveInputs (Plugin.AutoArchive.cs). Its default is the same
    // (false,false,false) tuple BossStatus itself returns for an empty set, so the first tick — and any
    // tick the guard below skips — reads exactly as a direct call would have.
    private (bool present, bool gone, bool dead) _bossStatus;

    /// <summary>Pure guard for the per-frame boss kill-state poll (<see cref="TickBossStatus"/>).
    /// <para><b>OWNER RULING 2026-08-14:</b> boss KILL-STATE tracking is a DEFAULT FEATURE — this poll
    /// runs even with the <b>MASTER</b> Auto-archive toggle OFF. There is therefore <b>no
    /// auto-archive-enabled parameter here</b>, and re-adding one would not compile at the call site —
    /// the same "the omission IS the fix" shape as <see cref="ShouldConsiderBossAdmission"/>. This
    /// COMPLETES that day's admission ruling (commit dce10a1): admission already filled
    /// <c>_stageBosses</c> with the toggle off, but the poll that turns membership into killed state
    /// (<c>StageBossSet.SetLiveness</c> → sticky <c>Killed</c> → <c>bosses[]</c>/<c>bossKilled</c>,
    /// <c>_killedBosses</c> marks, <c>_memberLastHpFrac</c>, and <c>TickStageBossHpTracks</c>'
    /// <c>MarkDead</c> stamp) was reachable ONLY from inside <c>TickAutoArchiveTriggers</c>, past its
    /// <c>!_autoArchive.Enabled</c> early return — so the set filled and never updated. That was the
    /// CAVEAT on protected archive-flow invariant 5; this removes it.</para>
    /// <para><b>PAUSE-PROOF (fix 3, same ruling, later the same day).</b> The <c>paused</c> term is
    /// <b>GONE, not defaulted</b> — the omission IS the fix. It was justified by "<c>OnCombatEvent</c>
    /// early-returns on the same flag, so nothing is being captured to keep liveness for", and fix 4
    /// removed that premise: boss ADMISSION, elite candidates and replay entity noting now run THROUGH
    /// pause (<c>ObserveAlwaysOnCapture</c>, Plugin.CaptureAlwaysOn.cs), and boss/elite HP tracks always
    /// did. Keeping it meant a boss KILLED while the meter was paused stayed <c>killed:false</c> forever
    /// — the residual the archive-flow doc recorded on invariant 5 — which on a raid costs the derived
    /// CLEAR verdict, computed from the killed SET (docs/recon/raid-clear-and-multiboss.md).</para>
    /// <para><b>Polling while paused is only safe ALONGSIDE <see cref="ShouldDrainStageBosses"/> (review
    /// Critical, same day).</b> The engine cannot tick while paused (<c>TickAutoArchiveTriggers</c> keeps
    /// its own <c>_paused</c> early return), so the DESTRUCTIVE half of this poll — the drain — must be
    /// suppressed there or the one-tick <c>BossDead</c> pulse is consumed by nobody and the run wedges.
    /// Read that guard's doc before touching either; they are one fix in two halves.</para>
    /// <para>The ONE term that remains is a skip the pre-existing engine path already had, kept so a
    /// master-ON run's poll schedule stays identical tick for tick:
    /// <list type="bullet">
    /// <item><paramref name="archivePending"/> — a deferred archive is waiting out its settle window.
    /// <b>Load-bearing, not cosmetic.</b> Here the WHOLE poll stops, so the frozen <c>_bossStatus</c>
    /// field itself is what carries the pulse across the window — nothing needs to keep updating
    /// mid-settle, which is why this case and the paused case use DIFFERENT mechanisms. Polling through
    /// it would drain the set against nobody and LOSE the <c>BossKill</c> for that fight; it would also
    /// (a) stop routing the post-kill DoT tail to its boss bucket
    /// (<c>StageBossSet.TryGetConfigId</c> resolves a KILLED member only until the stage drains) and
    /// (b) reopen the emptied set to a fresh admission that <c>ResolveCurrentStageBosses</c> would then
    /// PREFER over the killed members, regressing final-review Critical 1. A pending reason is only ever
    /// set with the master toggle ON (<c>TickAutoArchiveTriggers</c> nulls it the moment the toggle goes
    /// off), so this term costs the ruling nothing.</item>
    /// </list></para>
    /// Unit-tested headless (AutoArchiveContentGuardTests).</summary>
    internal static bool ShouldPollBossStatus(bool archivePending) => !archivePending;

    /// <summary>Pure guard for the DESTRUCTIVE half of <see cref="BossStatus"/> — the stage drain — and
    /// the companion half of <see cref="ShouldPollBossStatus"/> dropping its <c>paused</c> term (review
    /// Critical, 2026-08-14). <paramref name="clearAllowed"/> is
    /// <see cref="ShouldClearTrackedBoss"/>'s verdict, unchanged; <paramref name="paused"/> vetoes it.
    /// <para><b>Why the veto.</b> The drain empties <c>_stageBosses</c>, after which
    /// <see cref="BossStatus"/> returns the empty-set <c>(false,false,false)</c> tuple for good, and the
    /// engine consumes <c>BossDead</c> as a ONE-TICK PULSE. Drain on a tick the engine cannot consume and
    /// the pulse is consumed by NOBODY: <c>_bossKillWanted</c> never arms, the <c>BossGone</c> streak
    /// never accumulates, <c>_bossSegmentActive</c> stays latched and
    /// <c>ShouldConsiderInlineBossCut</c>'s <c>!bossSegmentActive</c> gate then bars EVERY later stage
    /// from cutting — the run-wide wedge of agent-process-rules § 13, the exact shape
    /// <see cref="AutoArchive.AutoArchiveEngine.BossGoneTimeoutMs"/> was written to fix.</para>
    /// <para><b>Why suppress the DRAIN and not the poll.</b> Both owner rulings then hold at once: kill
    /// state keeps updating every paused tick (sticky <c>Killed</c>, <c>_killedBosses</c>,
    /// <c>_memberLastHpFrac</c>), while the RETAINED set keeps re-deriving the same
    /// <c>(false, gone, dead)</c> aggregate until the engine resumes and reads it on the very next
    /// <c>TickBossStatus</c> — which runs BEFORE <c>TickAutoArchiveTriggers</c> in <c>OnUpdate</c>, so
    /// the resume tick both drains and delivers. Retaining also keeps the post-kill DoT tail on its boss
    /// bucket and keeps <c>ResolveCurrentStageBosses</c> reading the real killed members, exactly as the
    /// <c>archivePending</c> skip does. Re-opening the set is NOT a risk here:
    /// <c>StageBossSet.Admit</c> only admits while the set is empty or some member is still
    /// <c>Present</c>, so an all-gone stage is closed to new members drained or not.</para>
    /// <b>Do not "simplify" the <paramref name="paused"/> veto away</b> — nothing else re-arms the fight.
    /// Unit-tested headless (PauseCaptureTests, differentially against the un-vetoed shape).</summary>
    internal static bool ShouldDrainStageBosses(bool clearAllowed, bool paused) => clearAllowed && !paused;

    /// <summary>The single per-frame boss kill-state poll. Called UNCONDITIONALLY from
    /// <c>Plugin.OnUpdate</c>'s ~10 Hz throttled region — beside <c>TrackClearLatch</c> and immediately
    /// BEFORE <c>TickAutoArchiveTriggers</c>, which is exactly where <see cref="BossStatus"/> ran from
    /// before (inline in <c>BuildAutoArchiveInputs</c>), so its ordering against <c>PollRunBoundary</c>
    /// and <c>TrackClearLatch</c> is unchanged and there is no double-tick when the master toggle is on.
    /// <b>DO NOT move this back inside <c>TickAutoArchiveTriggers</c> or behind the
    /// <c>_autoArchive.Enabled</c> gate</b> — the same standing instruction <c>TrackClearLatch</c>
    /// carries, for the same reason (owner ruling 2026-08-14). Capture-side only: nothing this reaches
    /// can start an archive — <see cref="BossStatus"/> touches vitals, <c>_memberLastHpFrac</c>,
    /// <c>_killedBosses</c>, <c>_stageBosses</c> and <see cref="LatchStageBosses"/>, and calls no
    /// <c>ManualArchive</c>/engine path — so with the master toggle off the engine still never ticks.
    /// The call-order guarantee itself is headless-untestable; this comment IS the guard.</summary>
    private void TickBossStatus()
    {
        if (!ShouldPollBossStatus(_pendingArchiveReason is not null)) return;
        _bossStatus = BossStatus();
    }
}
