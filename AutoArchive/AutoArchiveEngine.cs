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
    public bool InstancedRun { get; init; }      // IDungeonState.CurrentRunId != 0
    public int FlowStateVersion { get; init; }   // IDungeonState.FlowStateVersion
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
    internal const long CooldownMs   = 10_000;   // shared across every trigger AND manual/scene archives
    internal const long MinContentMs = 30_000;   // idle content guard: >= 30 s of actual combat span

    public bool WipeEnabled   = true;
    public bool BossEnabled   = true;
    public bool IdleEnabled   = true;
    public bool StageEnabled  = true;
    public long IdleTimeoutMs = 60_000;

    private long _lastArchiveMs;

    // Wipe is edge-triggered (round 2 fix). allDead (roster-all-dead) is MOMENTARY — it clears the
    // instant anyone revives — while OutcomeFailed (server LastOutcome==Failed) is STICKY at run
    // level — it stays true until a brand-new run. A single boolean latch cannot reconcile a
    // momentary signal and a sticky one: round 1's coupled AND re-arm (`!allDead &&
    // !s.OutcomeFailed`) could never clear once OutcomeFailed stuck true for the rest of the run,
    // wedging every later independent wipe (pinned by
    // <see cref="AutoArchiveEngineTests.Wipe_second_independent_wipe_in_same_run_fires"/>). Rising-
    // edge detection needs no manual re-arm: a fall-then-rise is a fresh edge on its own, and a
    // signal that never falls simply never fires again. This gives fire-once-per-episode for
    // allDead AND fire-once-per-run for OutcomeFailed, with no stuck state. Overlap between the two
    // (they typically rise a tick apart for the same real-world wipe) is deduped by the shared
    // cooldown below, not by latch coupling — a second edge landing inside the cooldown armed by
    // the first is dropped, not deferred (pinned by
    // <see cref="AutoArchiveEngineTests.Wipe_overlap_alldead_then_outcomefailed_fires_once"/> and
    // its mirror).
    private bool _prevAllDead;           // previous tick's allDead reading
    private bool _prevOutcomeFailed;     // previous tick's OutcomeFailed reading

    private bool _bossSegmentActive;    // a boss segment is running; re-arms on boss-gone / non-boss archive
    private bool _bossPending;          // a boss sighting the cooldown gate swallowed, banked for when it lifts;
                                         // cleared by OnArchived on any non-boss archive (see its doc)
    private int  _lastFlowVersion = -1; // -1 = never observed (first sight adopts silently)
    private bool _stagePending;         // a flow transition happened and hasn't been consumed yet;
                                         // cleared by OnArchived (ANY archive consumes it — see its doc)

    /// <summary>Evaluate one tick. Returns the trigger to fire (caller runs ManualArchive(reason),
    /// which reports back via <see cref="OnArchived"/>), or null. Latch bookkeeping — including the
    /// wipe edge detectors — runs every tick even when nothing can fire (cooldown-suppressed or
    /// otherwise), so a disabled toggle / empty meter / cooldown window never banks a stale edge or
    /// loses a real one.</summary>
    public ArchiveReason? Evaluate(in AutoArchiveInputs s)
    {
        bool allDead = s.RosterSize > 0 && s.UnknownCount == 0 && s.DeadCount == s.RosterSize;
        // Computed against the PREVIOUS tick's _prev* values, before UpdateLatches below
        // overwrites them — a rising edge on either signal, this tick only.
        bool wipeEdge = (allDead && !_prevAllDead) || (s.OutcomeFailed && !_prevOutcomeFailed);
        UpdateLatches(in s, allDead);

        if (!s.HasStats) return null;   // ManualArchive would no-op anyway — don't consume the cooldown
        if (_lastArchiveMs != 0 && s.NowMs - _lastArchiveMs < CooldownMs) return null;

        if (WipeEnabled && wipeEdge)                              { return ArchiveReason.Wipe; }
        if (StageEnabled && _stagePending)                        { _stagePending = false;  return ArchiveReason.StageChange; }
        if (BossEnabled && !_bossSegmentActive && (s.BossPresent || _bossPending))
        {
            // Only mark a segment active when firing off a boss that is genuinely present RIGHT
            // NOW. A stale banked _bossPending firing after the boss already left (!s.BossPresent)
            // archives the pre-boss segment but starts no live segment — there's nothing to close
            // later, so marking one active here would wedge the NEXT real boss engagement behind
            // this !_bossSegmentActive gate forever (round 2 fix; pinned by
            // <see cref="AutoArchiveEngineTests.Boss_stale_banked_fire_does_not_wedge_next_real_boss"/>).
            _bossSegmentActive = s.BossPresent;
            _bossPending = false;
            return ArchiveReason.BossPhase;
        }
        if (IdleEnabled && IdleExpired(in s))                     { return ArchiveReason.Idle; }
        return null;
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
    /// needs no re-arm bookkeeping here at all — it is edge-triggered (see <see cref="_prevAllDead"/>
    /// doc) and re-arms itself the instant either signal falls.</summary>
    public void OnArchived(long nowMs, ArchiveReason reason)
    {
        _lastArchiveMs = nowMs;
        if (reason != ArchiveReason.BossPhase) { _bossSegmentActive = false; _bossPending = false; }
        _stagePending = false;
    }

    // Re-arm / adoption bookkeeping that must run before the fire gates on EVERY tick — including
    // ticks the cooldown is about to suppress, so no edge / banked sighting / transition is lost.
    private void UpdateLatches(in AutoArchiveInputs s, bool allDead)
    {
        _prevAllDead = allDead;
        _prevOutcomeFailed = s.OutcomeFailed;

        if (s.BossGone) _bossSegmentActive = false;    // boss died/despawned — next boss is a new segment
        // Bank a boss sighting even if the cooldown gate below is about to swallow the fire this
        // tick, so a boss that starts AND ends entirely inside one cooldown window still gets cut
        // once the cooldown lifts, instead of silently merging into the surrounding trash segment.
        if (BossEnabled && s.BossPresent && !_bossSegmentActive) _bossPending = true;

        if (_lastFlowVersion != s.FlowStateVersion)
        {
            // Strictly-increasing = a real transition (bank ONE pending). First-ever observation
            // (-1) and a version DECREASE (service reset on a new run) adopt silently.
            _stagePending = _lastFlowVersion >= 0 && s.FlowStateVersion > _lastFlowVersion;
            _lastFlowVersion = s.FlowStateVersion;
        }
        if (!s.InstancedRun || !StageEnabled) _stagePending = false;
    }

    // Idle: no player damage for IdleTimeoutMs, guarded by minimum content (>= MinContentMs of
    // combat span AND >= 1 player damage event — LastDamageMs is only ever set by a player-source
    // hit) so field farming can't churn the 50-entry history FIFO with trivial segments.
    // Self re-arms via the archive itself: ManualArchive -> Clear() -> CombatActive false.
    private bool IdleExpired(in AutoArchiveInputs s)
    {
        if (!s.CombatActive || s.LastDamageMs == 0) return false;
        if (s.NowMs - s.LastDamageMs < IdleTimeoutMs) return false;
        return s.LastDamageMs - s.CombatStartMs >= MinContentMs;
    }
}
