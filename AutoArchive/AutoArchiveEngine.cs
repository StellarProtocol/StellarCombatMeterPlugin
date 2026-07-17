using System;

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
    private bool _wipeLatched;          // fired-for-this-wipe; re-arms when anyone reads alive
    private bool _outcomeLatched;       // fired-for-this-Failed-edge; re-arms when outcome resets
    private bool _bossSegmentActive;    // a boss segment is running; re-arms on boss-gone / non-boss archive
    private int  _lastFlowVersion = -1; // -1 = never observed (first sight adopts silently)
    private bool _stagePending;         // a flow transition happened and hasn't been consumed yet;
                                         // cleared by OnArchived (ANY archive consumes it — see its doc)

    /// <summary>Evaluate one tick. Returns the trigger to fire (caller runs ManualArchive(reason),
    /// which reports back via <see cref="OnArchived"/>), or null. Latch bookkeeping runs every tick
    /// even when nothing can fire, so a disabled toggle / empty meter never banks a stale edge.</summary>
    public ArchiveReason? Evaluate(in AutoArchiveInputs s)
    {
        bool allDead = s.RosterSize > 0 && s.UnknownCount == 0 && s.DeadCount == s.RosterSize;
        UpdateLatches(in s, allDead);

        if (!s.HasStats) return null;   // ManualArchive would no-op anyway — don't consume the cooldown
        if (_lastArchiveMs != 0 && s.NowMs - _lastArchiveMs < CooldownMs) return null;

        if (WipeEnabled && allDead && !_wipeLatched)              { _wipeLatched = true;    return ArchiveReason.Wipe; }
        if (WipeEnabled && s.OutcomeFailed && !_outcomeLatched)   { _outcomeLatched = true; return ArchiveReason.Wipe; }
        if (StageEnabled && _stagePending)                        { _stagePending = false;  return ArchiveReason.StageChange; }
        if (BossEnabled && s.BossPresent && !_bossSegmentActive)  { _bossSegmentActive = true; return ArchiveReason.BossPhase; }
        if (IdleEnabled && IdleExpired(in s))                     { return ArchiveReason.Idle; }
        return null;
    }

    /// <summary>Every archive — ANY path, including manual, hotkey, and scene change — reports here:
    /// arms the shared cooldown, and a non-boss archive ends the running boss segment (so the next
    /// boss sighting cuts a fresh pre-boss segment). A boss-phase archive STARTS its segment. This
    /// re-arm-on-any-OTHER-archive reading is a deliberate spec-intent interpretation — a literal
    /// "any archive re-arms" would make the boss-phase archive that STARTS a segment immediately
    /// re-fire on the still-present boss every cooldown — controller-approved 2026-07-17, pinned by
    /// <see cref="AutoArchiveEngineTests.NonBoss_archive_ends_the_boss_segment"/>. Also consumes any
    /// pending stage transition (see <see cref="_stagePending"/>): the shared-cooldown spec intent
    /// ("prevents double-archives when triggers overlap") means an overlapping transition that lost
    /// the race to another trigger must not resurface as a stale StageChange archive later.</summary>
    public void OnArchived(long nowMs, ArchiveReason reason)
    {
        _lastArchiveMs = nowMs;
        if (reason != ArchiveReason.BossPhase) _bossSegmentActive = false;
        _stagePending = false;
    }

    // Re-arm / adoption bookkeeping that must run before the fire gates on EVERY tick.
    private void UpdateLatches(in AutoArchiveInputs s, bool allDead)
    {
        if (!allDead) _wipeLatched = false;            // someone is alive again — wipe re-arms
        if (!s.OutcomeFailed) _outcomeLatched = false; // a new run reset the sticky outcome
        if (s.BossGone) _bossSegmentActive = false;    // boss died/despawned — next boss is a new segment

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
