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
    public bool BossPresent { get; init; }       // a boss-tagged entity is currently resolved + alive
    public bool BossGone { get; init; }          // the previously resolved boss died / despawned / evicted
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
    public long MinBossSegmentMs = 10_000;   // don't cut a boss segment shorter than this (0 = off)
    public bool BossRecutOnRedetect;   // false = one fight, one cut (transient eviction / intervening archive never re-arms the boss segment). true = legacy re-detect re-cut.
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

    private bool _bossSegmentActive;    // a boss segment is running; re-arms on confirmed death (or,
                                         // legacy BossRecutOnRedetect=true, any boss-gone/non-boss
                                         // archive) — or unconditionally on leaving the instanced run
                                         // (run/scene boundary), so the trigger re-arms per run
    private bool _bossPending;          // a boss sighting the cooldown gate swallowed, banked for when it lifts;
                                         // cleared by OnArchived on any non-boss archive (see its doc)
    private int  _lastFlowVersion = -1; // -1 = never observed (first sight adopts silently)
    private bool _stagePending;         // a flow transition happened and hasn't been consumed yet;
                                         // cleared by OnArchived (ANY archive consumes it — see its doc)

    /// <summary>Evaluate one tick. Returns the trigger to fire (caller runs ManualArchive(reason),
    /// which reports back via <see cref="OnArchived"/>), or null. Latch bookkeeping — including the
    /// wipe episode latch's recovery clear and the OutcomeFailed edge stamp — runs every tick even
    /// when nothing can fire (cooldown-suppressed or otherwise), so a disabled toggle / empty meter
    /// / cooldown window never banks a stale edge or loses a real one.</summary>
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
        if (StageEnabled && _stagePending)                        { _stagePending = false;  return ArchiveReason.StageChange; }
        if (BossEnabled && !_bossSegmentActive && (s.BossPresent || _bossPending))
        {
            // A stale banked _bossPending fire (boss already left) must not mark a segment active —
            // there's nothing live to close later (round 2 fix; see
            // Boss_stale_banked_fire_does_not_wedge_next_real_boss). Min-segment floor (see
            // BossSegmentTooShort) gates both the live and the pending fire path on segment length.
            if (BossSegmentTooShort(in s)) { /* too short — fall through, try again next tick */ }
            else
            {
                _bossSegmentActive = s.BossPresent;
                _bossPending = false;
                return ArchiveReason.BossPhase;
            }
        }
        if (IdleEnabled && IdleExpired(in s))                     { return ArchiveReason.Idle; }
        return null;
    }

    // Min-boss-segment floor (see the fire-branch comment in Evaluate): true when a boss cut this
    // tick — live or banked-pending — would close a segment shorter than MinBossSegmentMs.
    private bool BossSegmentTooShort(in AutoArchiveInputs s) =>
        MinBossSegmentMs > 0 && s.NowMs - s.CombatStartMs < MinBossSegmentMs;

    /// <summary>Inline boss-phase cut gate (2026-07-21, configurable-autoarchive Task 7). The boss cut
    /// no longer fires from <see cref="Evaluate"/>'s deferred path in production — it happens INLINE in
    /// <c>Plugin.Capture.cs</c> at the first boss combat event, BEFORE that hit is accumulated, so the
    /// first boss hit lands in the fresh boss segment and the cut is never delayed to the 15 s settle
    /// cap mid-fight (the owner's chopped-fight bug). This method is the gate that inline cut consults:
    /// it reuses the SAME once-per-fight <see cref="_bossSegmentActive"/> latch the deferred path used —
    /// maintained by <see cref="UpdateLatches"/> every tick (run-boundary + confirmed-death re-arm,
    /// transient-eviction ignored when <see cref="BossRecutOnRedetect"/> is false) — so a transient
    /// vitals blink / intervening non-boss archive never re-cuts and each new instanced run re-arms.
    /// Returns true and marks the segment active EXACTLY ONCE per fight; false when boss auto-archive is
    /// off or a boss segment is already active. Setting <see cref="_bossSegmentActive"/> here also
    /// supersedes <see cref="Evaluate"/>'s boss branch on the next tick (it gates on !_bossSegmentActive),
    /// which is why the engine never double-cuts once the inline path has run.</summary>
    public bool TryBeginBossSegmentCut()
    {
        if (!BossEnabled || _bossSegmentActive) return false;
        _bossSegmentActive = true;
        _bossPending = false;   // a banked sighting is now moot — the inline cut supersedes it
        return true;
    }

    /// <summary>Every archive — ANY path, including manual, hotkey, and scene change — reports here:
    /// arms the shared cooldown, and a non-boss archive ends the running boss segment (so the next
    /// boss sighting cuts a fresh pre-boss segment). A boss-phase archive STARTS its segment, unless
    /// it fired off a stale banked sighting with no boss actually present (see <see cref="Evaluate"/>
    /// — that case leaves no segment active to end). This re-arm-on-any-OTHER-archive reading is a
    /// deliberate spec-intent interpretation — a literal "any archive re-arms" would make the boss-
    /// phase archive that STARTS a segment immediately re-fire on the still-present boss every
    /// cooldown — controller-approved 2026-07-17, pinned by
    /// <see cref="AutoArchiveEngineTests.NonBoss_archive_ends_the_boss_segment"/>. Also consumes any
    /// pending stage transition (see <see cref="_stagePending"/>): the shared-cooldown spec intent
    /// ("prevents double-archives when triggers overlap") means an overlapping transition that lost
    /// the race to another trigger must not resurface as a stale StageChange archive later. Wipe
    /// needs no bookkeeping here at all — <c>_wipeArchived</c>'s recovery clear and
    /// <c>_prevOutcomeFailed</c>'s edge stamp both live in <see cref="Evaluate"/> and run every
    /// tick regardless of which path (if any) actually archived.</summary>
    public void OnArchived(long nowMs, ArchiveReason reason)
    {
        _lastArchiveMs = nowMs;
        // Legacy re-arm on any non-boss archive is part of the re-detect model; with re-cut OFF a
        // manual/wipe/idle archive mid-boss must NOT restart boss detection (round the owner's run).
        // _bossPending's clear is NOT gated by the flag: a banked pre-fire sighting that another
        // trigger already superseded must never resurface later, regardless of recut mode — the
        // same "supersede" rule _stagePending already follows unconditionally below.
        if (reason != ArchiveReason.BossPhase)
        {
            if (BossRecutOnRedetect) _bossSegmentActive = false;
            _bossPending = false;
        }
        _stagePending = false;
    }

    // Re-arm / adoption bookkeeping that must run before the fire gates on EVERY tick — including
    // ticks the cooldown is about to suppress, so no banked sighting / transition is lost. (The
    // wipe latch's own recovery clear + edge stamp live directly in Evaluate — see its body.)
    private void UpdateLatches(in AutoArchiveInputs s)
    {
        // Leaving the instanced run (open world between dungeons) ends any boss segment, so the NEXT
        // run's boss gets a fresh cut. This is the "scene/run change ends it" half of the fix — a
        // mid-fight cache blink keeps InstancedRun true, so it never re-cuts during one fight.
        if (!s.InstancedRun) { _bossSegmentActive = false; _bossPending = false; }
        // Boss segment ends only on a CONFIRMED death (or, legacy, any "gone" incl. transient
        // eviction). Default: a cache blink mid-fight must NOT re-arm — one fight, one cut.
        if (BossRecutOnRedetect ? s.BossGone : s.BossDead) _bossSegmentActive = false;
        // Bank a boss sighting even if the cooldown gate below is about to swallow the fire this
        // tick, so a boss that starts AND ends entirely inside one cooldown window still gets cut
        // once the cooldown lifts, instead of silently merging into the surrounding trash segment.
        // Gated on InstancedRun (Fix 2, review round): a stale BossPresent reading while OUT of an
        // instanced run must not bank a pending cut — otherwise re-entering a run before the next
        // out-of-run tick resets it (the reset above only fires while STILL out of run) would fire a
        // phantom BossPhase archive off a sighting that was never part of any real run.
        if (BossEnabled && s.InstancedRun && s.BossPresent && !_bossSegmentActive) _bossPending = true;

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
