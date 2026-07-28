using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.AutoArchive;

/// <summary>Why an encounter segment was archived. Persisted on the history entry (JSON key "trig").</summary>
internal enum ArchiveReason
{
    /// <summary>User pressed the Archive button / hotkey.</summary>
    Manual = 0,
    /// <summary>Scene transition (the pre-existing OnSceneChanged path).</summary>
    SceneChange = 1,
    /// <summary>Every rostered member (incl. self) read dead, or the run outcome flipped to Failed.</summary>
    Wipe = 2,
    /// <summary>A boss-tagged combat entity was sighted while no boss segment was active (pre-boss trash cut).</summary>
    BossPhase = 3,
    /// <summary>No player damage for the configured timeout, with the minimum-content guard satisfied.</summary>
    Idle = 4,
    /// <summary>EDungeonState flow transition inside an instanced run.</summary>
    StageChange = 5,
    /// <summary>A CONFIRMED boss death ended the fight. Deferred through the settle window so the
    /// post-kill tail (trailing DoTs, the killing-blow tick) lands inside the fight's archive rather
    /// than in a sliver after it (2026-07-26 fix; owner runs sea/696115723671437312,
    /// sea/420833196448415744).</summary>
    BossKill = 6,
}

/// <summary>Facts snapshot for one engine tick — assembled by Plugin.AutoArchive.cs (~10 Hz). Record
/// struct so tests can build variants with <c>with</c> expressions.</summary>
internal readonly record struct AutoArchiveInputs
{
    public long NowMs { get; init; }             // server clock (CombatSnapshot.ServerNowMs)
    public bool CombatActive { get; init; }      // Plugin._combatActive
    public long CombatStartMs { get; init; }     // Plugin._combatStartMs
    public long LastDamageMs { get; init; }      // Plugin._lastDamageMs (player-source damage only)
    public bool HasStats { get; init; }          // _stats.Count > 0 (mirror of ManualArchive's no-op guard)
    public int RosterSize { get; init; }         // members counted by the wipe scan (incl. self)
    public int DeadCount { get; init; }
    public int UnknownCount { get; init; }       // members with NO usable HP observation — block wipe
    public bool OutcomeFailed { get; init; }     // IDungeonState.LastOutcome == Failed
    /// <summary>DIAGNOSTIC-ONLY (Minor F, review round 2026-07-27): a boss-tagged entity is currently
    /// resolved + alive. No engine decision reads this — <see cref="Evaluate"/> and
    /// <see cref="UpdateLatches"/> consult only <see cref="BossDead"/>. Kept on the snapshot as an
    /// observability breadcrumb for callers/logging that want to see boss presence without re-deriving
    /// it; do not wire new trigger logic off it without updating this doc.</summary>
    public bool BossPresent { get; init; }
    /// <summary>The previously resolved boss died, despawned, or was evicted from the vitals cache — a
    /// confirmed death (<see cref="BossDead"/>) OR a transient/sustained cache eviction. WAS
    /// diagnostic-only (Minor F, review round 2026-07-27); that framing is now STALE (P0 fix,
    /// 2026-07-28): <see cref="UpdateLatches"/> reads this to time a gone STREAK, and a boss that stays
    /// continuously gone-but-not-confirmed-dead for <see cref="BossGoneTimeoutMs"/> ends the fight the
    /// same way a confirmed death does (see that const's doc for the full mechanism/rationale). Do not
    /// re-narrow this back to logging-only without also removing that consumer.</summary>
    public bool BossGone { get; init; }
    /// <summary>A CONFIRMED boss death — HP observed &lt;=0 — as opposed to a transient cache
    /// eviction, which <see cref="BossGone"/> also covers.</summary>
    public bool BossDead { get; init; }
    public bool InstancedRun { get; init; }      // IDungeonState.CurrentRunId != 0
    public int FlowStateVersion { get; init; }   // IDungeonState.FlowStateVersion
    public DungeonFlowState CurrentFlowState { get; init; }  // IDungeonState.CurrentFlowState — the run
                                                             // lifecycle value the version counter points at
}

/// <summary>
/// Pure auto-archive trigger state machine (Part B of the 2026-07-17 sync/auto-archive spec).
/// No service references — unit-tests headless (the ReplayCapture / ObserveBurstHit precedent;
/// Plugin cannot be instantiated in tests). One decision per tick; a shared cooldown spans ALL
/// archive paths — manual and scene-change archives report in via <see cref="OnArchived"/> — so
/// overlapping triggers (e.g. wipe + stage change) cannot double-archive.
/// </summary>
internal sealed class AutoArchiveEngine
{
    internal const long DefaultCooldownMs = 10_000;   // shared cooldown default (tests reference this)
    internal const long MinContentMs = 30_000;   // idle content guard: >= 30 s of actual combat span

    // Gone-timeout threshold (P0 fix, owner raid 2026-07-28, runId=632530154488332288): stage 1 of that
    // raid has two bosses scripted to die by a triggered event once both are brought to 1% — HP never
    // reads <=0, so BossDead never rises, and this branch's own Task 2 (2026-07-26) removed the ONLY
    // other thing that used to close _bossSegmentActive (a raw BossGone/BossDead re-arm — removed
    // because it looped on a still-known-dead corpse, the sliver-spam bug this whole branch exists to
    // fix). Without a second way to end the fight, the segment latched open for the REST OF THE RUN and
    // ShouldConsiderInlineBossCut's `!bossSegmentActive` gate barred every later boss (stage 2, stage 3)
    // from ever cutting — the owner got one giant walk-in-to-run-end archive instead of one per stage.
    //
    // DURATION is what tells a genuine despawn apart from a transient blink: the framework's
    // AOI-disappear wire event removes the vitals row near-instantly once an entity actually leaves the
    // scene (CombatEntityTracker.OnEntityDisappeared) — contrast the framework's separate 5-MINUTE
    // idle-sweep TTL (CombatService.IdleEntityTtlMs), which is irrelevant at this timescale. A
    // transient blink (a spurious disappear+reappear pair on a network hiccup) clears within a tick or
    // two — sub-second, see Transient_eviction_never_ends_the_segment — while a genuine despawn never
    // clears. Picked mid-range of the owner-specified 3-5 s window: comfortably above any observed
    // blink, comfortably below the time it takes a raid party to reach the next stage's boss, so the
    // segment is already closed well before that boss's opener would otherwise wait on it.
    internal const long BossGoneTimeoutMs = 4_000;

    // Master enable (Fix 1, review round): the on/off gate used to live ONLY in Plugin.AutoArchive.cs
    // (a plugin field with no unit coverage) — moved here so the policy is testable in isolation. Sits
    // AFTER the wipe recovery/edge-stamp + UpdateLatches bookkeeping in Evaluate, so latch/flow-adoption
    // state keeps advancing while disabled and re-enabling never sees a stale edge; only FIRING is
    // suppressed. See Master_disabled_never_fires.
    public bool Enabled = true;
    public long CooldownMs = DefaultCooldownMs;   // shared across every trigger AND manual/scene archives; configurable at runtime via prefs
    public bool WipeEnabled   = true;
    public bool BossEnabled   = true;
    public bool IdleEnabled   = true;
    public bool StageEnabled  = true;
    public long IdleTimeoutMs = 60_000;
    public long WipeGraceMs = 2000;    // allDead must PERSIST this long before it counts toward a wipe, so a
                                       // momentary solo down->revive doesn't cut the run. OutcomeFailed
                                       // (server-authoritative) bypasses this grace entirely.
    public bool WipeIgnoreSolo;        // when true, an all-dead roster of size 1 (solo) never wipes — only a
                                       // party wipe (RosterSize > 1) counts. OutcomeFailed still bypasses this.

    private long _lastArchiveMs;

    // Wipe is a single episode-archived latch (round 3 fix — the "boss pattern" applied to wipe;
    // <see cref="_bossSegmentActive"/> below is the same shape: a level-gated "already handled this
    // episode" flag, not an edge detector). allDead (roster-all-dead) is MOMENTARY — it clears the
    // instant anyone revives — while OutcomeFailed (server LastOutcome==Failed) is STICKY at run
    // level — it stays true until a brand-new run. Round 1's coupled AND re-arm wedged every later
    // independent wipe once OutcomeFailed stuck true for the rest of the run (pinned by
    // <see cref="AutoArchiveEngineTests.Wipe_second_independent_wipe_in_same_run_fires"/>). Round
    // 2's pure rising-edge rewrite fixed that but had two defects of its own: (1) an allDead edge
    // that rises DURING an unrelated cooldown gets stamped into "previous tick" before the fire can
    // happen, so once the cooldown lifts there is no NEW edge and the wipe is lost forever even
    // though allDead never stopped being true (pinned by
    // <see cref="AutoArchiveEngineTests.Wipe_alldead_rises_during_unrelated_cooldown_then_fires_on_lift"/>);
    // (2) allDead firing once, then — much later, past its cooldown, with the party never having
    // recovered — OutcomeFailed's independent rising edge fires a SECOND time for what is still the
    // same episode (pinned by
    // <see cref="AutoArchiveEngineTests.Wipe_double_signal_wide_gap_party_stays_dead_fires_once"/>).
    // Fix: stop asking "did a signal just rise" and instead ask "has THIS episode already been
    // archived" (`_wipeArchived`). allDead is read as a LEVEL every tick, not an edge, so a fire
    // that a cooldown swallows is never lost — the level condition simply persists and fires the
    // instant the cooldown lifts. `_wipeArchived` is set true ONLY at the moment a fire actually
    // returns (after the cooldown gate) — never while suppressed — so a cooldown-blocked tick
    // leaves the episode still eligible next tick. OutcomeFailed alone still needs a one-shot edge
    // (`_prevOutcomeFailed`, stamped every tick) — without it, its stickiness would re-fire every
    // tick once true. Recovery (`!allDead` clears `_wipeArchived`) runs unconditionally every tick:
    // the instant at least one member is alive the episode is over, and the next allDead-or-
    // OutcomeFailed edge starts a fresh one. ACCEPTED RESIDUAL (documented, not fixed): an
    // OutcomeFailed-only wipe (no allDead ever) whose edge rises during an unrelated cooldown is
    // still lost — the every-tick `_prevOutcomeFailed` stamp consumes the edge while suppressed,
    // and a sticky signal can't produce a second edge later to retry. This needs a no-allDead wipe
    // landing within `CooldownMs` of an unrelated archive; allDead is the primary signal and is
    // level-gated so it never suffers this loss (pinned by
    // <see cref="AutoArchiveEngineTests.Wipe_outcomefailed_only_edge_lost_inside_unrelated_cooldown_is_accepted_residual"/>).
    private bool _wipeArchived;          // this wipe episode has already been archived
    private bool _prevOutcomeFailed;     // previous tick's OutcomeFailed reading (one-shot edge only)
    private long _allDeadSinceMs;        // 0 = not currently all-dead; else the ms the current all-dead
                                          // episode began (revive-grace debounce)

    // A boss segment is running. The INLINE cut (Plugin.Capture.cs MaybeCutForBossPhase, via
    // TryBeginBossSegmentCut) is the sole thing that SETS this true. As of 2026-07-26 (Task 2) it is
    // cleared to false ONLY by OnArchived (any reason except BossPhase, which is the trash bank that
    // OPENS a segment) or by leaving the instanced run (run/scene boundary) — NOT by a raw BossDead/
    // BossGone reading. A confirmed death instead sets _bossKillWanted, which Evaluate turns into a
    // BossKill archive; it is that archive's OnArchived call, after the caller's settle window, that
    // actually closes the segment. This is what stops the old re-arm→cut→re-arm loop (a dead boss kept
    // getting re-adopted and re-cut once per tick). The inline gate consults this via BossSegmentActive
    // so a genuinely new fight (after an archive closed the segment) cuts again.
    private bool _bossSegmentActive;

    // A confirmed boss death was observed while a boss segment was open, and that fight has not been
    // banked yet. LATCHED rather than edge-consumed: BossStatus clears _autoArchiveBossId the instant
    // it sees the death (Plugin.BossDetection.cs, moved there from Plugin.AutoArchive.cs by the Minor E
    // extraction, review round 2026-07-27 second pass), so BossDead is a ONE-TICK pulse, and
    // TickAutoArchiveTriggers skips Evaluate entirely while another archive is pending — an edge would
    // be lost in both cases and the fight would never bank. Same shape as _wipeArchived: a level the
    // fire gates read, set only from bookkeeping and cleared only on an actual fire / run exit.
    //
    // Gone-timeout (P0 fix, 2026-07-28) is the SECOND way this gets armed — see BossGoneTimeoutMs's doc
    // for the full mechanism. A boss that is continuously unobserved (gone but never confirmed dead) for
    // that long behaves exactly like a confirmed death from here on: same latch, same settle-and-bank
    // path, same segment close via OnArchived. _bossKillWasTimeout (below) records WHICH of the two
    // armed it, purely for observability (the ungated [archive] line) — the fire/consume mechanics
    // themselves are identical for both causes.
    private bool _bossKillWanted;
    // Cause of the most recently armed/fired _bossKillWanted: true = gone-timeout, false = confirmed
    // death. Meaningful only alongside _bossKillWanted / immediately after Evaluate returns BossKill —
    // stable across the caller's settle wait (nothing touches it again before the deferred archive
    // commits, since TickAutoArchiveTriggers skips Evaluate entirely while a reason is pending). Read by
    // Plugin.Diagnostics.cs's LogArchiveOutcome to show which cause fired on the ungated [archive] line.
    private bool _bossKillWasTimeout;
    // 0 = not currently in a "boss continuously gone" streak; else the ms UpdateLatches first observed
    // s.BossGone (and NOT s.BossDead) while a segment was open. Reset the instant the boss is seen
    // alive again, the segment closes, or the run is left — see UpdateLatches. Same LEVEL-tracked
    // debounce shape as _allDeadSinceMs/WipeGraceMs above, just for a different signal.
    private long _bossGoneSinceMs;
    // One-shot guard armed by TryBeginBossSegmentCutAcrossPreemption (finding 2, review round
    // 2026-07-27) and consumed by the very next OnArchived call, so that call's unconditional segment
    // close cannot undo a reopen that predates it. See both members' docs for the full defect.
    //
    // Important D (review round 2026-07-27, second pass): "consumed by the very next OnArchived call"
    // is load-bearing on the guard's ONLY caller, Plugin.Capture.cs's MaybeCutForBossPhase, actually
    // reaching OnArchived at all. ManualArchive (Plugin.History.cs) has a skip-empty early return
    // (`if (_stats.Count == 0) { ...; return; }`) that sits BEFORE its call into OnArchived — if the
    // guarded commit took that path, OnArchived would never run and the guard would leak onto whatever
    // archive fires next, wrongly sparing ITS segment close. That path is unreachable here: the guard is
    // armed only when TryBeginBossSegmentCutAcrossPreemption is called with a pending archive reason to
    // commit (ShouldPreemptPendingForBoss), and a reason only ever becomes pending when the engine's own
    // Evaluate returned it — which requires `s.HasStats` (`_stats.Count > 0` at that moment; see
    // Evaluate's `if (!s.HasStats) return null;` gate). Nothing between arming the pending reason and
    // this guarded commit can drop _stats back to empty without going through an archive of its own —
    // and ManualArchive nulls `_pendingArchiveReason` unconditionally on entry, so any such intervening
    // archive would already have cleared the very reason this commit is about to fire, making it a no-op
    // rather than a guarded call. So the guarded commit always finds `_stats.Count > 0` and always runs
    // the full ManualArchive body through to OnArchived. Not independently unit-tested — it is a
    // property of Plugin (which cannot be instantiated headless; see BossStatus's ACCEPTED RESIDUAL doc
    // for the same limitation), not of this pure engine.
    private bool _preemptGuard;
    private int  _lastFlowVersion = -1; // -1 = never observed (first sight adopts silently)
    private bool _stagePending;         // a flow transition happened and hasn't been consumed yet;
                                         // cleared by OnArchived (ANY archive consumes it — see its doc)

    /// <summary>Evaluate one tick. Returns the trigger to fire (caller runs ManualArchive(reason),
    /// which reports back via <see cref="OnArchived"/>), or null. Latch bookkeeping — including the
    /// wipe episode latch's recovery clear and the OutcomeFailed edge stamp — runs every tick even
    /// when nothing can fire (cooldown-suppressed or otherwise), so a disabled toggle / empty meter
    /// / cooldown window never banks a stale edge or loses a real one.</summary>
    /// <remarks>
    /// NOTE (recut-fix, 2026-07-21): this method NEVER returns BossPhase. ALL boss cuts route through
    /// the INLINE capped path (Plugin.Capture.cs MaybeCutForBossPhase → TryBeginBossSegmentCut →
    /// ManualArchive(BossPhase, replayUpperCapServerMs)). The old Evaluate boss branch fired an
    /// UNCAPPED archive at the engine-tick "now" and, on a re-detect where _bossSegmentActive was
    /// re-armed but the boss was still known (inline gate skipped), placed the keep-before boundary
    /// at "now" instead of firstHit − keepBefore (owner run sea/U051Yv8lf2, 0:55 vs 0:48). The branch
    /// (+ its MinBossSegmentMs floor and _bossPending cooldown-bank, both meaningless in the
    /// deterministic inline model) is removed. The engine keeps ONLY the _bossSegmentActive latch
    /// (closed in OnArchived / UpdateLatches, see their docs) that the inline gate consults. Pinned by
    /// Evaluate_never_returns_bossphase.
    /// </remarks>
    public ArchiveReason? Evaluate(in AutoArchiveInputs s)
    {
        bool allDead = s.RosterSize > 0 && s.UnknownCount == 0 && s.DeadCount == s.RosterSize;
        // outcomeEdge is a one-shot edge (OutcomeFailed is sticky); allDead below is read as a
        // level, not an edge — see the field-doc comment above for why the two need different
        // treatment.
        bool outcomeEdge = s.OutcomeFailed && !_prevOutcomeFailed;
        // Revive-grace debounce: allDead must PERSIST >= WipeGraceMs before it counts, so a momentary
        // solo down->revive doesn't cut the run. OutcomeFailed (server-authoritative) bypasses grace.
        if (!allDead) _allDeadSinceMs = 0;
        else if (_allDeadSinceMs == 0) _allDeadSinceMs = s.NowMs;
        bool soloSkip = WipeIgnoreSolo && s.RosterSize == 1;
        bool allDeadHeld = allDead && !soloSkip && s.NowMs - _allDeadSinceMs >= WipeGraceMs;
        bool wipeWanted = !_wipeArchived && (allDeadHeld || outcomeEdge);
        if (!allDead) _wipeArchived = false;   // recovery re-arm: >=1 alive member => episode over
        _prevOutcomeFailed = s.OutcomeFailed;  // stamp every tick regardless of fire/cooldown
        UpdateLatches(in s);

        if (!Enabled) return null;      // master gate — bookkeeping above already ran; only firing is suppressed
        if (!s.HasStats) return null;   // ManualArchive would no-op anyway — don't consume the cooldown
        if (_lastArchiveMs != 0 && s.NowMs - _lastArchiveMs < CooldownMs) return null;

        if (WipeEnabled && wipeWanted)
        {
            // Only latch the episode as archived HERE, after the cooldown gate above has already
            // passed — never while a fire is being suppressed. This is what makes allDead
            // LEVEL-gated: a wipe that rises during an unrelated cooldown is not lost, because
            // `_wipeArchived` stays false and `wipeWanted` re-evaluates true again next tick, right
            // up until a tick actually gets past the cooldown gate and fires.
            _wipeArchived = true;
            return ArchiveReason.Wipe;
        }
        if (BossEnabled && _bossKillWanted)
        {
            // Latch cleared only here, after the cooldown gate above — a suppressed tick keeps the want
            // alive (the _wipeArchived discipline), so a death inside a cooldown is deferred, not lost.
            _bossKillWanted = false;
            return ArchiveReason.BossKill;
        }
        if (StageEnabled && _stagePending)                        { _stagePending = false;  return ArchiveReason.StageChange; }
        // Never BossPhase — see the <remarks> on this method's doc comment.
        if (IdleEnabled && IdleExpired(in s))                     { return ArchiveReason.Idle; }
        return null;
    }

    /// <summary>Read the boss-segment latch — the inline cut's gate (Plugin.Capture.cs). True while a
    /// boss fight segment is running; cleared only by <see cref="OnArchived"/> (any reason except
    /// BossPhase) or by leaving the instanced run (2026-07-26, Task 2) — a raw death/eviction reading no
    /// longer closes it directly.</summary>
    public bool BossSegmentActive => _bossSegmentActive;

    /// <summary>True when the currently-armed (or just-fired) <see cref="ArchiveReason.BossKill"/> want
    /// was caused by the gone-timeout (<see cref="BossGoneTimeoutMs"/>) rather than a confirmed death.
    /// Read by the caller at (or any time after) the moment <see cref="Evaluate"/> returns
    /// <see cref="ArchiveReason.BossKill"/> — see <see cref="_bossKillWasTimeout"/>'s doc for why it is
    /// stable across the settle wait. Used only for observability (the ungated [archive] line's cause=
    /// field, Plugin.Diagnostics.cs); no decision anywhere reads it.</summary>
    public bool BossKillWasTimeout => _bossKillWasTimeout;

    /// <summary>Inline boss-phase cut gate (2026-07-21). The boss cut happens INLINE in
    /// <c>Plugin.Capture.cs</c> at the first boss combat event, BEFORE that hit is accumulated, so the
    /// first boss hit lands in the fresh boss segment and the cut is never delayed to the settle cap
    /// mid-fight (the owner's chopped-fight bug) — and it routes through <c>ManualArchive(BossPhase,
    /// replayUpperCapServerMs)</c>, so the keep-before replay boundary is honoured. This is the sole
    /// thing that SETS <see cref="_bossSegmentActive"/> (the engine no longer fires BossPhase). It is the
    /// once-per-fight latch: as of 2026-07-26 (Task 2) it closes ONLY via <see cref="OnArchived"/> (any
    /// reason except BossPhase, which is the trash bank that opens a segment) or via leaving the
    /// instanced run — a confirmed death sets <see cref="_bossKillWanted"/> instead, and it is the
    /// resulting <see cref="ArchiveReason.BossKill"/> archive's OnArchived call that actually closes the
    /// segment, once the caller's settle window has let the trailing damage land. A transient vitals
    /// blink (gone but not confirmed dead) never closes it at all — one fight, one cut — and each new
    /// instanced run, or any archive that ends the fight, re-arms it. Returns true and marks the segment
    /// active when no segment is active; false when boss auto-archive is off or a segment is already
    /// running.
    /// <para><b>No cooldown check (finding 1, review round 2026-07-27 — REVERTED):</b> a 2026-07-26
    /// revision made this consult <c>_lastArchiveMs</c>/<c>CooldownMs</c> against a re-adoption spam
    /// loop. That check has a worse failure mode than the one it guarded against: while the cooldown
    /// held, this method just returned false and the new boss's damage kept accumulating in the
    /// still-open PREVIOUS segment; once the cooldown lifted, the (now-delayed) cut fired with
    /// <c>priorCombat</c> now true and banked the fight's own opening seconds as a "boss" TRASH
    /// archive — with a 60 s Min gap, the first MINUTE of the fight. The spam that check guarded
    /// against is already structurally impossible without it: a killed boss can never be re-adopted
    /// (<see cref="KilledBossTracker"/>) and this method cannot fire again while a segment is open
    /// (<c>_bossSegmentActive</c>) — so cut-spam would require ARCHIVE-spam, and Min gap still blocks
    /// that everywhere it always did (<see cref="Evaluate"/>'s own fire gate). A boss pull must
    /// therefore cut immediately no matter how recently the previous archive landed.</para>
    /// </summary>
    public bool TryBeginBossSegmentCut()
    {
        if (!BossEnabled || _bossSegmentActive) return false;
        _bossSegmentActive = true;
        return true;
    }

    /// <summary>Open a fresh boss segment for a NEW engagement that is pre-empting a still-pending
    /// archive (owner ruling 2026-07-26, §2.7: a fresh boss pull forces a still-waiting deferred
    /// archive to commit immediately rather than leak into the new fight). Same gate as <see
    /// cref="TryBeginBossSegmentCut"/> (BossEnabled + once-per-fight), PLUS a one-shot guard consumed
    /// by the very next <see cref="OnArchived"/> call, so that call — which reports the OLD pending
    /// archive being committed to make room, never this new segment — cannot undo the open this method
    /// just performed.
    /// <para><b>Finding 2 (review round 2026-07-27):</b> the naive sequence — open the new segment,
    /// then commit the old pending — let <see cref="OnArchived"/>'s unconditional "any non-BossPhase
    /// reason closes the segment" rule fire against the WRONG segment (the one just opened for the NEW
    /// fight, not the one the archive being committed actually belongs to), leaving the new fight with
    /// NO open segment and therefore no BossKill when its boss eventually died — the fight would only
    /// ever bank at run-end. Reordering alone (commit first, open second) does not read as more robust
    /// than an explicit guard: it would make correctness depend on OnArchived's side effects never
    /// reaching further than intended as more call sites are added later. The engine instead owns the
    /// transition end-to-end via this one-shot guard, so the caller cannot get the sequencing wrong.</para>
    /// <para>Call this INSTEAD OF <see cref="TryBeginBossSegmentCut"/> only when a pending archive is
    /// about to be pre-empted; the caller must still commit that pending archive (which calls
    /// OnArchived) immediately afterward.</para>
    /// </summary>
    public bool TryBeginBossSegmentCutAcrossPreemption()
    {
        if (!TryBeginBossSegmentCut()) return false;
        _preemptGuard = true;
        return true;
    }

    /// <summary>Every archive — ANY path, including manual, hotkey, scene change, and the boss cuts
    /// themselves (inline BossPhase, and now BossKill) — reports here: arms the shared cooldown and
    /// closes the running boss segment (2026-07-26, Task 2 — see the unconditional-except-BossPhase rule
    /// below), so the NEXT boss sighting cuts a fresh segment. Also consumes any pending stage transition
    /// (see <see cref="_stagePending"/>) and any pending boss-kill want (see <see cref="_bossKillWanted"/>):
    /// an overlapping transition/want that lost the race to another trigger must not resurface as a stale
    /// archive later. Wipe needs no bookkeeping here — <c>_wipeArchived</c>'s recovery clear and
    /// <c>_prevOutcomeFailed</c>'s edge stamp both live in <see cref="Evaluate"/>.</summary>
    public void OnArchived(long nowMs, ArchiveReason reason)
    {
        _lastArchiveMs = nowMs;
        if (_preemptGuard)
        {
            // This archive is the OLD pending being committed to make room for a segment the caller
            // already reopened (TryBeginBossSegmentCutAcrossPreemption, finding 2) — that reopen
            // predates this call and must survive it. One-shot: consumed here, never suppresses a
            // later, unrelated close.
            _preemptGuard = false;
        }
        // ANY archive closes the boss segment — an archive means the segment ended. The lone exception
        // is the BossPhase trash bank, which is the archive that OPENS a segment; closing on it would
        // let the still-present boss immediately re-cut (controller-approved reading, 2026-07-17).
        else if (reason != ArchiveReason.BossPhase) _bossSegmentActive = false;
        _stagePending = false;
        // Same race _stagePending already guards against (2026-07-26, review round): the boss dies and
        // arms _bossKillWanted, but before the deferred BossKill fires (a window as wide as the shared
        // cooldown), another archive — a manual hotkey press, a wipe, a stage change — wins the race and
        // closes the segment. That archive already banked the fight; a BossKill firing afterwards would
        // have nothing left to bank — exactly the stale, spurious archive this task exists to eliminate.
        // Pinned by An_intervening_archive_consumes_the_pending_bosskill.
        _bossKillWanted = false;
    }

    // Re-arm / adoption bookkeeping that must run before the fire gates on EVERY tick — including
    // ticks the cooldown is about to suppress, so no banked sighting / transition is lost. (The
    // wipe latch's own recovery clear + edge stamp live directly in Evaluate — see its body.)
    /// <remarks>
    /// Gone-timeout (P0 fix, owner raid 2026-07-28 — see <see cref="BossGoneTimeoutMs"/>'s doc for the
    /// full mechanism/rationale): the streak is LEVEL-tracked exactly like <c>_allDeadSinceMs</c>/
    /// <c>WipeGraceMs</c> above — <c>s.BossGone</c> (a confirmed death OR a cache eviction) read every
    /// tick a segment is open. The <c>!s.BossDead</c> guard routes a real death straight past the streak
    /// to the arm below instead of ALSO starting a timeout streak on the very same tick (a death implies
    /// <c>BossGone</c>). The streak resets the instant the boss is seen alive again or the segment
    /// closes. On timeout the method arms <see cref="_bossKillWanted"/> exactly like a confirmed death —
    /// same latch, same settle-and-bank path — but deliberately does NOT mark the boss killed
    /// (<see cref="_killedBosses"/> is untouched here; that mark lives in Plugin.BossDetection.cs's
    /// <c>BossStatus</c>, gated on a CONFIRMED death only), so a boss that reappears alive after the
    /// timeout fired stays adoptable and cuttable (<c>ShouldClearTrackedBoss</c> already clears the
    /// tracked id once the segment closes with no confirmed death — see its own doc, Critical A).
    /// </remarks>
    private void UpdateLatches(in AutoArchiveInputs s)
    {
        // Leaving the instanced run (open world between dungeons) ends any boss segment, so the NEXT
        // run's boss gets a fresh cut, and drops any unbanked kill want with it. _preemptGuard is
        // always consumed synchronously by the very next OnArchived (same call stack as the reopen
        // that armed it), so this reset is defensive insurance, not a reachable path in production.
        if (!s.InstancedRun)
        {
            _bossSegmentActive = false; _bossKillWanted = false; _bossKillWasTimeout = false;
            _preemptGuard = false; _bossGoneSinceMs = 0;
        }

        // Gone-timeout streak bookkeeping — see the <remarks> on this method's doc comment.
        if (!_bossSegmentActive) _bossGoneSinceMs = 0;
        else if (s.BossGone && !s.BossDead) { if (_bossGoneSinceMs == 0) _bossGoneSinceMs = s.NowMs; }
        else _bossGoneSinceMs = 0;
        bool bossGoneTimedOut = _bossGoneSinceMs != 0 && s.NowMs - _bossGoneSinceMs >= BossGoneTimeoutMs;

        // A confirmed death ENDS THE FIGHT but does not itself end the segment: the BossKill archive
        // does, via OnArchived, after the caller's settle window. A raw death/eviction reading directly
        // re-arming the segment here is what caused the post-kill cut loop — the dead boss kept getting
        // re-adopted by CheckBossCandidate, re-armed, and re-cut once per tick (0 ms archives ~1 s
        // apart). That re-arm (and the BossRecutOnRedetect toggle that used to gate it) is retired for
        // good (2026-07-26, Task 4) — no raw gone/dead reading closes the segment any more. The
        // gone-timeout below is the SECOND way a fight ends — see the <remarks> on this method's doc.
        if (s.BossDead && _bossSegmentActive) { _bossKillWanted = true; _bossKillWasTimeout = false; }
        else if (bossGoneTimedOut)             { _bossKillWanted = true; _bossKillWasTimeout = true;  }

        if (_lastFlowVersion != s.FlowStateVersion)
        {
            // Strictly-increasing = a real transition; first-ever observation (-1) and a version
            // DECREASE (service reset on a new run) adopt silently. Owner ruling 2026-07-20: arm ONLY
            // when that transition lands in a run-END state (End/Settlement/Vote). Entry-side
            // transitions (into Active/Ready/Playing, or any other value) never arm — a player poking
            // a boss bumps the flow to Playing, and cutting an archive of just the opener there is
            // wrong. Combined with the carry rules, a pre-pull opener simply stays accumulated and
            // lands inside the next real segment.
            bool realTransition = _lastFlowVersion >= 0 && s.FlowStateVersion > _lastFlowVersion;
            _stagePending = realTransition && IsRunEndState(s.CurrentFlowState);
            _lastFlowVersion = s.FlowStateVersion;
        }
        if (!s.InstancedRun || !StageEnabled) _stagePending = false;
    }

    // Run-END flow states: only a transition INTO one of these arms the stage trigger (owner ruling
    // 2026-07-20). Values mirror zproto EDungeonState; the enum tolerates unknown future wire values
    // (cast) so this is an explicit allow-list, not a "not-an-entry-state" negation.
    private static bool IsRunEndState(DungeonFlowState state) =>
        state is DungeonFlowState.End or DungeonFlowState.Settlement or DungeonFlowState.Vote;

    // Idle: no player damage for IdleTimeoutMs, guarded by minimum content (>= MinContentMs of
    // combat span AND >= 1 player damage event — LastDamageMs is only ever set by a player-source
    // hit) so field farming can't churn the 50-entry history FIFO with trivial segments.
    // Self re-arms via a BANKED archive: ManualArchive -> Clear() -> CombatActive false. (A
    // suppressed all-zero archive keeps CombatActive true — it wipes nothing by owner ruling.)
    private bool IdleExpired(in AutoArchiveInputs s)
    {
        if (!s.CombatActive || s.LastDamageMs == 0) return false;
        if (s.NowMs - s.LastDamageMs < IdleTimeoutMs) return false;
        return s.LastDamageMs - s.CombatStartMs >= MinContentMs;
    }
}
