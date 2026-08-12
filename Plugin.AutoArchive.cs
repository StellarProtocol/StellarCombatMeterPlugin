using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.AutoArchive;

namespace Stellar.CombatMeter;

// Auto-archive trigger wiring (Part B, 2026-07-17 sync/auto-archive spec): assembles the per-tick
// fact snapshot for the pure AutoArchiveEngine (~10 Hz, from OnUpdate's throttled region) and
// fires ManualArchive(reason) when the engine decides a segment ended. ALL policy (arm/fire/
// re-arm, cooldown, content guard) lives in the engine — this partial only reads services.
//
// Boss IDENTIFICATION (which entity is the tracked boss, its lifetime across a fight, and whether a
// given event "involves" it) moved to the sibling Plugin.BossDetection.cs (Minor E, review round
// 2026-07-27, second pass) to keep this file under the 500-LoC size gate. This file keeps the inline
// cut's ORCHESTRATION (when to cut) — MaybeCutForBossPhase and its decision helpers.
public sealed partial class Plugin
{
    private readonly AutoArchiveEngine _autoArchive = new();

    // Idle-settle delay (2026-07-18): an AUTO trigger fires the INSTANT the engine decides a segment
    // ended (a floor clear bumps EDungeonState, a wipe reads all-dead, etc.), but the mobs' corpses
    // are still present and trailing damage (DoTs, the killing-blow tick) is still landing — so
    // committing the snapshot immediately loses those last hits from the archived record. Rather than
    // wait a fixed interval, hold an AUTO archive until combat has gone QUIET: no damage event for this
    // long, watching _lastDamageMs — the GENERAL damage clock, for EVERY deferrable reason including
    // BossKill (owner ruling 2026-07-28: heals still never count — settle cares about DPS only — but
    // the window is not narrowed to any one entity).
    //
    // RETIRED (2026-07-28, defect 2 of the bosskill-settle branch's raid-testing fixes): a 2026-07-26
    // fix additionally narrowed a BossKill pending to a boss-targeted clock (SettleClockMs(reason,
    // lastDamageMs, lastBossDamageMs), backed by Plugin.BossDetection.cs's now-deleted _settleBossId /
    // IsSettleBossDamage), reasoning that add cleanup elsewhere shouldn't hold the boss archive open.
    // That reading was wrong: the owner reported residual damage at the head of the FOLLOWING archive
    // ("there's mini dps that left to early of 2,4,6") — quietMs looked satisfied (>= settle) because
    // the boss-only clock had gone quiet, but adds/DoTs elsewhere kept landing and spilled into the next
    // segment's head. Corrected ruling: the boss's death only STARTS the settle timer; the window itself
    // watches ALL damage, the same clock as every other reason. SettleClockMs collapsed to an identity
    // once the per-reason branching went away, so it (and its 3 call sites) were deleted rather than
    // kept as vestigial indirection — both call sites below now read _lastDamageMs directly.
    //
    // If it's already been this quiet when the trigger fires, the archive commits immediately. There
    // is a comfortable window: after a floor clear the game shows "Enter the next floor in 5s". A
    // MANUAL (button/hotkey) archive and the SceneChange archive (which must beat the entity teardown)
    // stay IMMEDIATE.
    //
    // Configurable (2026-07-21, Task 4): was a hardcoded const; now a prefs-fed field so the settle
    // window can be tuned per-install. DefaultArchiveSettleMs keeps the original default value visible
    // as a named symbol (the AutoArchiveEngine.DefaultCooldownMs precedent) for the sanity-range test.
    internal const long DefaultArchiveSettleMs = 2_000;
    private long _archiveSettleMs = DefaultArchiveSettleMs;

    // Backstop: sustained combat that never goes quiet (and no scene change to supersede the pending)
    // would defer the archive forever. Commit anyway once this long has elapsed since the trigger
    // armed. A scene change already supersedes the pending, so this is only a rare-case safety net.
    internal const long ArchiveIdleCapMs = 15_000;

    // The single pending deferred-archive slot. Set when the engine returns a deferrable reason;
    // committed once combat has been quiet for _archiveSettleMs (or the cap elapses); cleared by
    // ManualArchive on ANY commit (so a manual/scene archive during the wait supersedes it — never a
    // stale double-fire). While set, TickAutoArchiveTriggers holds off evaluating new triggers so the
    // engine can't re-fire. _pendingArchiveArmedMs is the server clock when the trigger armed (cap base).
    private AutoArchive.ArchiveReason? _pendingArchiveReason;
    private long                       _pendingArchiveArmedMs;

    // ---- settings accessors (the cached-pref accessor pattern: cached in the engine, persisted on
    // set; loaded once by InitAutoArchive from the ctor. The AutoUpload property this used to cite is
    // retired — its per-content replacement lives in Plugin.UploadPolicy.cs) ----
    private const string PrefAaWipe         = "autoArchive.wipe";
    private const string PrefAaBoss         = "autoArchive.bossPhase";
    private const string PrefAaIdle         = "autoArchive.idle";
    private const string PrefAaIdleTimeoutS = "autoArchive.idleTimeoutS";
    private const string PrefAaStage        = "autoArchive.stageChange";

    // Per-stage selection (2026-07-30). The legacy master bool above is UNCHANGED and still the quick
    // on/off; these say WHICH run-end stages cut an archive. No migration needed: an existing install keeps
    // its master value and picks up the End-only default below, which turns the old "1-3 archives depending
    // on the Min-gap cooldown" into exactly one.
    private static string PrefAaStageState(DungeonFlowState state)
        => "autoArchive.stage." + state.ToString().ToLowerInvariant();

    // Task 4 additions (2026-07-21): master enable + the Task 1-3 engine knobs (wipe grace/ignore-solo,
    // shared cooldown, min-boss-segment floor) + the settle delay.
    private const string PrefAaEnabled        = "autoArchive.enabled";
    private const string PrefAaCooldownS      = "autoArchive.cooldownS";
    private const string PrefAaSettleS        = "autoArchive.settleS";
    private const string PrefAaWipeGraceS     = "autoArchive.wipeGraceS";
    private const string PrefAaWipeIgnoreSolo = "autoArchive.wipeIgnoreSolo";

    // Boss-phase "keep before" (2026-07-21, Task 7): how much of the pre-hit run-up rides with the boss
    // segment when the inline boss cut fires (Plugin.Capture.cs MaybeCutForBossPhase). Default 0 = cut
    // exactly at the first boss hit. When > 0 the boss segment's combat clock is backdated to
    // (first-boss-hit − keepBefore) and — on a trash→boss cut — the trash replay window is capped at the
    // same instant so the run-up MOVEMENT rides with the boss window instead of the trash one (the
    // boundary MOVES earlier; windows stay contiguous → the full-run concatenation is unbroken). DPS
    // stats for the boss segment still start at the first boss hit (the accumulated trash is archived
    // whole, never split mid-window). Plugin-local field (no engine behaviour), persisted like the
    // engine knob accessors above.
    private const string PrefAaKeepBeforeS    = "autoArchive.bossKeepBeforeS";
    private long _autoArchiveKeepBeforeMs;
    internal long BossKeepBeforeMs => _autoArchiveKeepBeforeMs;

    // Master enable (Fix 1, review round): the gate now lives on the pure engine (_autoArchive.Enabled)
    // — the single source of truth — so the policy is unit-testable (Master_disabled_never_fires).
    // This accessor is a thin persisted wrapper; TickAutoArchiveTriggers still short-circuits on it
    // below _paused as a perf optimization (skip building the input snapshot entirely when off), but
    // the engine enforces the policy itself even if called directly.
    internal bool AutoArchiveEnabled
    {
        get => _autoArchive.Enabled;
        set { _autoArchive.Enabled = value; _prefs.Set(PrefAaEnabled, value); _prefs.Save(); }
    }

    internal bool AutoArchiveWipe
    {
        get => _autoArchive.WipeEnabled;
        set { _autoArchive.WipeEnabled = value; _prefs.Set(PrefAaWipe, value); _prefs.Save(); }
    }

    internal bool AutoArchiveBoss
    {
        get => _autoArchive.BossEnabled;
        set { _autoArchive.BossEnabled = value; _prefs.Set(PrefAaBoss, value); _prefs.Save(); }
    }

    internal bool AutoArchiveIdle
    {
        get => _autoArchive.IdleEnabled;
        set { _autoArchive.IdleEnabled = value; _prefs.Set(PrefAaIdle, value); _prefs.Save(); }
    }

    internal bool AutoArchiveStage
    {
        get => _autoArchive.StageEnabled;
        set { _autoArchive.StageEnabled = value; _prefs.Set(PrefAaStage, value); _prefs.Save(); }
    }

    /// <summary>Whether a transition into this run-end stage cuts an archive. Independent of the master
    /// <see cref="AutoArchiveStage"/> toggle: a run must pass both.</summary>
    internal bool AutoArchiveStageState(DungeonFlowState state) => _autoArchive.IsStageSelected(state);

    internal void SetAutoArchiveStageState(DungeonFlowState state, bool selected)
    {
        _autoArchive.SetStageSelected(state, selected);
        _prefs.Set(PrefAaStageState(state), selected);
        _prefs.Save();
    }

    internal int AutoArchiveIdleTimeoutS
    {
        get => (int)(_autoArchive.IdleTimeoutMs / 1000);
        set { _autoArchive.IdleTimeoutMs = value * 1000L; _prefs.Set(PrefAaIdleTimeoutS, value); _prefs.Save(); }
    }

    // ---- Task 4 accessors: the Tasks 1-3 engine knobs, wired the same get-engine/set-engine+persist
    // way as the accessors above. ----

    internal bool AutoArchiveWipeIgnoreSolo
    {
        get => _autoArchive.WipeIgnoreSolo;
        set { _autoArchive.WipeIgnoreSolo = value; _prefs.Set(PrefAaWipeIgnoreSolo, value); _prefs.Save(); }
    }

    internal int AutoArchiveWipeGraceS
    {
        get => (int)(_autoArchive.WipeGraceMs / 1000);
        set { _autoArchive.WipeGraceMs = value * 1000L; _prefs.Set(PrefAaWipeGraceS, value); _prefs.Save(); }
    }

    internal int AutoArchiveCooldownS
    {
        get => (int)(_autoArchive.CooldownMs / 1000);
        set { _autoArchive.CooldownMs = value * 1000L; _prefs.Set(PrefAaCooldownS, value); _prefs.Save(); }
    }

    internal int AutoArchiveSettleS
    {
        get => (int)(_archiveSettleMs / 1000);
        set { _archiveSettleMs = value * 1000L; _prefs.Set(PrefAaSettleS, value); _prefs.Save(); }
    }

    // Boss-phase "keep before" seconds (Task 7). Plugin-local (no engine field) — the inline boss cut
    // in Plugin.Capture.cs applies it. Persisted the same get-field/set-field+persist way as above.
    internal int AutoArchiveKeepBeforeS
    {
        get => (int)(_autoArchiveKeepBeforeMs / 1000);
        set { _autoArchiveKeepBeforeMs = value * 1000L; _prefs.Set(PrefAaKeepBeforeS, value); _prefs.Save(); }
    }

    // The most recently committed (BANKED, not suppressed) archive — for the settings UI readout
    // (Task 5). Set by NoteLastArchive, called from ManualArchive (Plugin.History.cs) on every bank.
    internal (AutoArchive.ArchiveReason reason, long ms)? LastArchive { get; private set; }

    internal void NoteLastArchive(AutoArchive.ArchiveReason reason, long ms) => LastArchive = (reason, ms);

    private void InitAutoArchive()
    {
        _autoArchive.WipeEnabled   = _prefs.Get(PrefAaWipe, true);
        _autoArchive.BossEnabled   = _prefs.Get(PrefAaBoss, true);
        _autoArchive.IdleEnabled   = _prefs.Get(PrefAaIdle, true);
        _autoArchive.StageEnabled  = _prefs.Get(PrefAaStage, true);
        // Settlement-ONLY ticked by default (owner 2026-08-06, Image #14 — End=off, Settlement=on,
        // Vote=off): a VAULT floor clear lands at the Settlement stage (the ~10 Hz flow sample skips End),
        // so Settlement-on makes vault floors archive AT the clear out of the box, and a normal dungeon's
        // End->Settlement transition archives there too (the always-on scene archive is the run-end
        // fallback either way, invariant 4). A single armed stage keeps the archive count deterministic.
        // Per-stage prefs are opt-in: an existing install that already SAVED a stage choice keeps it; only
        // new / never-touched installs pick up this default.
        foreach (var stage in AutoArchive.AutoArchiveEngine.SelectableStages)
            _autoArchive.SetStageSelected(stage, _prefs.Get(PrefAaStageState(stage),
                stage == DungeonFlowState.Settlement));
        _autoArchive.IdleTimeoutMs = _prefs.Get(PrefAaIdleTimeoutS, 300) * 1000L;   // ship default 300s (owner Image #25, 2026-07-21)

        _autoArchive.Enabled             = _prefs.Get(PrefAaEnabled, true);
        // NOTE: the retired autoArchive.bossRecut key (2026-07-26) is deliberately NOT read — any value
        // left on disk from an older build is inert. See the BossKill spec § 2.8.
        _autoArchive.WipeIgnoreSolo      = _prefs.Get(PrefAaWipeIgnoreSolo, false);
        _autoArchive.WipeGraceMs         = _prefs.Get(PrefAaWipeGraceS, 2) * 1000L;
        _autoArchive.CooldownMs          = _prefs.Get(PrefAaCooldownS, 5) * 1000L;  // ship default 5s Min gap (owner Image #25)
        _archiveSettleMs                 = _prefs.Get(PrefAaSettleS, 2) * 1000L;
        _autoArchiveKeepBeforeMs         = _prefs.Get(PrefAaKeepBeforeS, 0) * 1000L;   // Task 7: default 0 = cut at first hit
    }

    // ~10 Hz from OnUpdate's throttled region (Plugin.cs). An AUTO trigger is deferred until combat
    // goes quiet for _archiveSettleMs so trailing damage lands before the snapshot (see the field
    // docs); during the wait we stop evaluating new triggers so the engine can't re-fire/duplicate.
    private void TickAutoArchiveTriggers()
    {
        if (_paused) return;
        // Master toggle: manual/hotkey/scene archives are unaffected (separate paths). This is a perf
        // short-circuit only — the engine (_autoArchive.Enabled) is the actual policy source of truth
        // (Fix 1, review round) — but we still clear any stranded pending here so a mid-wait master-off
        // doesn't leave a deferred AUTO archive to fire later once re-enabled (Minor, review round).
        if (!_autoArchive.Enabled) { _pendingArchiveReason = null; return; }

        // Arm a fresh pending only when none is outstanding — while one waits, the engine is skipped.
        if (_pendingArchiveReason is null)
        {
            var inputs = BuildAutoArchiveInputs();
            if (_autoArchive.Evaluate(in inputs) is not { } reason) return;
            LogAutoArchiveFired(reason, inputs);
            if (!IsDeferrableArchive(reason)) { ManualArchive(reason); return; }
            _pendingArchiveReason  = reason;
            _pendingArchiveArmedMs = inputs.NowMs;
            // fall through to the due-check below so an already-quiet arm commits this same tick
        }

        if (_pendingArchiveReason is not { } pending) return;
        var now = _services.CombatSnapshot.ServerNowMs;
        if (!PendingArchiveDue(now, _lastDamageMs, _archiveSettleMs) &&
            !PendingArchiveCapped(now, _pendingArchiveArmedMs, ArchiveIdleCapMs)) return;
        LogAutoArchiveCommit(pending, now, _lastDamageMs);
        ManualArchive(pending);   // ManualArchive clears _pendingArchiveReason on commit
    }

    /// <summary>True once combat has been quiet for <paramref name="idleSettleMs"/> — no activity on
    /// <paramref name="lastActivityMs"/> in that window, so trailing DoTs / the killing-blow tick have
    /// landed. <paramref name="lastActivityMs"/> is a caller-selected activity clock — production feeds
    /// it <c>_lastDamageMs</c> directly, the SAME general damage clock for every deferrable reason
    /// including <see cref="AutoArchive.ArchiveReason.BossKill"/> (owner ruling 2026-07-28 — a prior
    /// boss-targeted narrowing via a retired SettleClockMs helper is gone, see this file's settle-window
    /// doc above). Pure so it unit-tests headless (the AutoArchiveEngine precedent).</summary>
    internal static bool PendingArchiveDue(long nowMs, long lastActivityMs, long idleSettleMs)
        => nowMs - lastActivityMs >= idleSettleMs;

    /// <summary>Backstop for the idle wait: true once <paramref name="capMs"/> has elapsed since the
    /// trigger armed, so sustained combat with no scene change can't defer the archive forever.</summary>
    internal static bool PendingArchiveCapped(long nowMs, long armedMs, long capMs)
        => nowMs - armedMs >= capMs;

    /// <summary>True for the engine-driven AUTO reasons that should wait out the settle delay
    /// (a floor-clear <see cref="AutoArchive.ArchiveReason.StageChange"/>, wipe, idle). A
    /// <see cref="AutoArchive.ArchiveReason.Manual"/> button/hotkey archive stays immediate, and
    /// <see cref="AutoArchive.ArchiveReason.SceneChange"/> must beat the entity teardown at the
    /// boundary — neither defers.
    /// <para><b>BossPhase is now IMMEDIATE (2026-07-21, Task 7):</b> the boss cut moved INLINE into
    /// <c>Plugin.Capture.cs</c> (see <c>MaybeCutForBossPhase</c>), firing at the first boss hit BEFORE
    /// that hit is accumulated so the boss fight is one clean segment. The old deferred BossPhase path
    /// hit the 15 s cap mid-fight and chopped the fight (owner-reported from the log). This engine path
    /// no longer fires BossPhase in production (the inline cut sets <c>_bossSegmentActive</c> before the
    /// next engine tick observes the boss), but should a BossPhase reason ever reach here it must NOT
    /// defer.</para>
    /// Pure so it unit-tests headless.</summary>
    internal static bool IsDeferrableArchive(AutoArchive.ArchiveReason reason) => reason switch
    {
        AutoArchive.ArchiveReason.Wipe        => true,
        AutoArchive.ArchiveReason.Idle        => true,
        AutoArchive.ArchiveReason.StageChange => true,
        AutoArchive.ArchiveReason.BossKill    => true,
        _                                     => false,   // Manual + SceneChange + BossPhase stay immediate
    };

    // Run-scoped CLEAR latch tracker — called UNCONDITIONALLY every ~10 Hz tick (OnUpdate's throttled
    // region), OUTSIDE the master auto-archive gate AND the pending-archive gate. Owner design: the latch
    // "always tracks", so a clear is latched the moment it is observed even in manual-only mode (auto-
    // archive off) — a manual/scene archive of that run then reads "kill" (vault-floor P0, run
    // sea/qyvCSXteqC). Cheap: two sticky dungeon-state reads + the pure live verdict + the pure
    // UpdateClearLatch seam; no allocation. It is safe to run out of a dungeon: the clear signal only
    // exists inside a genuine run (the framework WIPES LastOutcome/LastSettlement on every new run-id, and
    // IsFreshKill's baseline rejects a stale carry-over on a same-uuid re-entry), and the flag resets at
    // the next encounter's combat start — so an out-of-run tick never mislatches. Deliberately NOT gated
    // on IsInstancedRun(): the clear settlement can land as CurrentRunId drops to 0 on leave-scene, and
    // gating there would drop the very clear we must capture.
    private void TrackClearLatch()
    {
        var freshSettlement = IsFreshKill(_services.Dungeon.LastSettlement, _settlementAtCombatStart)
            ? _services.Dungeon.LastSettlement : null;
        var hasFreshClear = ResolveVerdict(freshSettlement, _services.Dungeon.LastOutcome) == "kill";
        (_clearedThisRun, _clearedSettlement) = UpdateClearLatch(
            _clearedThisRun, _clearedSettlement, hasFreshClear, _services.Dungeon.LastSettlement);
    }

    private AutoArchiveInputs BuildAutoArchiveInputs()
    {
        ScanRosterVitals(out var rosterSize, out var dead, out var unknown);
        var (bossPresent, bossGone, bossDead) = BossStatus();
        // A fresh CLEAR is present when this encounter's settlement newly resolves to "kill" — the SAME
        // gate ManualArchive uses to bank the clear marker (IsFreshKill + ResolveVerdict). Lets the engine
        // fire the run-end stage archive through the HasStats + cooldown gates on a fast kill (see Evaluate).
        var freshSettlement = IsFreshKill(_services.Dungeon.LastSettlement, _settlementAtCombatStart)
            ? _services.Dungeon.LastSettlement : null;
        // The run-scoped clear latch is now tracked UNCONDITIONALLY in TrackClearLatch (OnUpdate),
        // independent of this master-gated / pending-gated engine path — so a manual-only-mode clear still
        // latches (owner design: "the latch always tracks"). This is the LIVE verdict (2-arg, no latch)
        // that drives the engine's HasFreshClear input only; reading the latch here would make it
        // self-sustaining and re-fire the engine forever.
        var inputs = new AutoArchiveInputs
        {
            NowMs            = _services.CombatSnapshot.ServerNowMs,
            CombatActive     = _combatActive,
            CombatStartMs    = _combatStartMs,
            LastDamageMs     = _lastDamageMs,
            HasStats         = _stats.Count > 0,
            HasFreshClear    = ResolveVerdict(freshSettlement, _services.Dungeon.LastOutcome) == "kill",
            RosterSize       = rosterSize,
            DeadCount        = dead,
            UnknownCount     = unknown,
            OutcomeFailed    = _services.Dungeon.LastOutcome == DungeonOutcome.Failed,
            BossPresent      = bossPresent,
            BossGone         = bossGone,
            BossDead         = bossDead,
            InstancedRun     = IsInstancedRun(),
            FlowStateVersion = _services.Dungeon.FlowStateVersion,
            CurrentFlowState = _services.Dungeon.CurrentFlowState,
        };
        LogFlowTransition(inputs.CurrentFlowState, inputs.FlowStateVersion);
        return inputs;
    }

    // Wipe scan: self via IPlayerState (authoritative — the vitals cache doesn't track self
    // reliably, see HpFractionFor's doc), others via the SAME source ladder IsDead uses
    // (Plugin.List.cs) — calibrated FastSyncState first (post-calibration; inert at 0), then
    // combat vitals (HasHpObservation-gated: an unknown member BLOCKS the trigger rather than
    // false-firing), then the fast-sync roster HP as fallback. Consulting FastSyncState here too
    // (not just in IsDead) matters: post-calibration, a member the meter renders dead/alive via
    // FastSyncState must count the same way for the wipe trigger, or the two diverge (spec-coverage
    // finding — a stale hp<=0 vitals row could count someone dead the meter shows alive, or vice versa).
    private void ScanRosterVitals(out int rosterSize, out int deadCount, out int unknownCount)
    {
        rosterSize = 0; deadCount = 0; unknownCount = 0;

        var ps = _services.PlayerState;
        rosterSize++;
        if (ps.MaxHealth > 0) { if (ps.Health <= 0) deadCount++; }
        else unknownCount++;

        long selfChar = _services.CombatSnapshot.LocalEntityId.Value >> 16;
        foreach (var m in _services.PartyRoster.Members)
        {
            if (m.CharId == selfChar) continue;   // self handled above
            rosterSize++;
            if (FastSyncStateMapper.TryMap(m.FastSyncState, FastSyncStateDead) is { } mappedDead)
            {
                if (mappedDead) deadCount++;
                continue;
            }
            var v = _services.CombatLookup.GetVitals(m.EntityId);
            if (v.HasHpObservation && v.MaxHp > 0) { if (v.Hp <= 0) deadCount++; }
            else if (m.MaxHp > 0) { if (m.Hp <= 0) deadCount++; }
            else unknownCount++;
        }
    }

    /// <summary>Pure decision (Task 7): on the first-detected boss hit, should the accumulated pre-boss
    /// trash be archived as its own boss-phase segment? Only when there WAS prior combat (trash) to
    /// bank — a direct engage (no combat before the boss) has nothing to archive, so it must NOT emit a
    /// spurious pre-fight segment; the boss fight simply starts here as one clean segment. The
    /// boss-enabled + once-per-fight gating is applied separately by the caller
    /// (<see cref="AutoArchiveEngine.TryBeginBossSegmentCut"/>). Unit-tested headless.</summary>
    internal static bool ShouldArchiveTrashForBoss(bool priorCombat) => priorCombat;

    /// <summary>Pure decision: does a fresh boss engagement have to force a still-pending deferred
    /// archive to commit right now? Yes whenever one is pending — the new fight's opening hit must never
    /// land inside the previous segment's archive (owner ruling 2026-07-26). The commit is capped at
    /// (firstHit − keepBefore) exactly like the trash bank, so windows stay contiguous. Unit-tested
    /// headless.</summary>
    internal static bool ShouldPreemptPendingForBoss(bool hasPending) => hasPending;

    /// <summary>Pure guard: should the inline boss CUT even consider this event (admission is now a
    /// SEPARATE gate — see <see cref="ShouldConsiderBossAdmission"/>)? Only when boss auto-archive is
    /// enabled, NO boss segment is currently active, AND we are in an instanced run. Keying on
    /// <c>bossSegmentActive</c> (NOT "boss already known") is the recut-fix (2026-07-21, run
    /// sea/U051Yv8lf2): once an archive closes the segment (<see cref="AutoArchiveEngine.OnArchived"/>,
    /// any reason except BossPhase) or a run/scene boundary clears it (<see
    /// cref="AutoArchiveEngine.UpdateLatches"/>), the inline cut must fire AGAIN — capped at firstHit −
    /// keepBefore — even if <c>_autoArchiveBossId</c> is still set. A transient eviction (boss gone but
    /// not confirmed dead) re-arms nothing on its own. The old "boss already known" gate skipped the
    /// re-detect, and the engine's now-removed boss branch fired an UNCAPPED archive at the tick "now"
    /// instead (keep-before boundary at 0:55 vs 0:48). The <c>inRun</c> gate keeps <c>_autoArchiveBossId</c>
    /// + the cut out of the open world. When a segment IS active the fight is running and this fast-exits
    /// (hot-path). Unit-tested headless.</summary>
    internal static bool ShouldConsiderInlineBossCut(bool bossEnabled, bool bossSegmentActive, bool inRun)
        => bossEnabled && !bossSegmentActive && inRun;

    /// <summary>Pure guard (Critical fix, 2026-08-12 review): should this event be offered to
    /// <see cref="ObserveAutoArchiveBoss"/> for ADMISSION into <c>_stageBosses</c>? Same two terms as
    /// <see cref="ShouldConsiderInlineBossCut"/> (bossEnabled + inRun), deliberately WITHOUT its
    /// <c>bossSegmentActive</c> term: admission must run on EVERY combat event even while a segment is
    /// active, or a co-boss engaged after the fight's first hit can never join the set — the set could
    /// never exceed one member in a real simultaneous fight (multi-boss spec §3.2). The CUT decision
    /// itself is unchanged. Unit-tested headless; the set-level effect is pinned by
    /// <c>StageBossSetTests.Coboss_joins_while_first_is_present</c>.</summary>
    internal static bool ShouldConsiderBossAdmission(bool bossEnabled, bool inRun) => bossEnabled && inRun;

    // NOTE (2026-08-12, multi-boss plan Task 2): the doc comments above and on MaybeCutForBossPhase
    // below still narrate the fix history in terms of the single _autoArchiveBossId latch this method's
    // caller used to carry. That field is gone — Plugin.BossDetection.cs now carries the SET
    // (_stageBosses) — but the historical reasoning (why the gate is keyed on bossSegmentActive, why a
    // blink must not re-arm the cut) is unchanged and still load-bearing at the SET level, so the prose
    // is left intact rather than rewritten line-by-line.

    // Inline boss-phase cut (2026-07-21). Called from OnCombatEvent (Plugin.Capture.cs) on every
    // DamageDealt, BEFORE that event is accumulated — the SOLE boss-cut path. On the first boss combat
    // event of a fresh (or re-armed) boss segment, when enabled and no segment is active, cuts
    // IMMEDIATELY:
    //   • pre-emption (a deferred archive is still pending): open the NEW segment FIRST via
    //     TryBeginBossSegmentCutAcrossPreemption (finding 2, 2026-07-27), THEN commit the old pending —
    //     a one-shot engine guard stops that commit's OnArchived from closing the segment just opened.
    //   • trash→boss (priorCombat, no pending): bank the pre-boss trash as its own segment (immediate
    //     ManualArchive(BossPhase) → Clear()), capped at (firstHit − keepBefore) so run-up movement
    //     rides with the boss window (windows stay contiguous).
    //   • direct engage (!priorCombat, no pending): NO archive — mark the segment active and backdate
    //     the combat clock by keepBefore.
    // Re-cuts fire here too (CAPPED) once an archive or run/scene boundary re-arms the segment latch.
    // Hot-path safe: O(1), no allocation once a segment is active or boss auto-archive is off.
    //
    // ADMISSION vs CUT (Critical fix, 2026-08-12): admission now runs regardless of bossSegmentActive
    // (see ShouldConsiderBossAdmission) — the CUT below is otherwise unchanged.
    private void MaybeCutForBossPhase(EntityId src, EntityId tgt, long firstHitMs, bool priorCombat)
    {
        bool inRun = IsInstancedRun();

        // NOT gated on bossSegmentActive — must run every event so a co-boss engaged mid-fight is still
        // admitted. O(1)/alloc-free once the _bossCheck cache is warm.
        if (ShouldConsiderBossAdmission(_autoArchive.BossEnabled, inRun)) ObserveAutoArchiveBoss(src, tgt);

        // Fast-exit for the CUT only — admission above already ran regardless of bossSegmentActive.
        if (!ShouldConsiderInlineBossCut(_autoArchive.BossEnabled, _autoArchive.BossSegmentActive, inRun)) return;
        // Critical A / Important B (review round 2026-07-27, second pass): an EXPLICIT "does this event
        // touch a tracked boss" test, not a bare "a boss is tracked at all" proxy. The proxy was only
        // valid the instant ObserveAutoArchiveBoss had just set the id from THIS event — once the id (or,
        // now, any set member) survives past that (a still-alive boss after a wipe archive, or — before
        // Critical A's fix — a stale id pinned past its own fight), the proxy read "a boss is tracked" as
        // "this event is about the boss", so an unrelated event (a rez heal between two players on the
        // wipe→retry run-back) reached the cut below and opened a spurious boss segment over trash. See
        // EventInvolvesBoss / EventInvolvesAnyStageBoss (Plugin.BossDetection.cs) for the pinned cases.
        if (!EventInvolvesAnyStageBoss(src, tgt)) return;

        long keepBeforeMs = BossKeepBeforeMs;
        bool preempting = ShouldPreemptPendingForBoss(_pendingArchiveReason is not null);
        bool cut = preempting
            ? _autoArchive.TryBeginBossSegmentCutAcrossPreemption()   // guards the commit below (finding 2)
            : _autoArchive.TryBeginBossSegmentCut();                  // once cut per segment (no cooldown — finding 1)
        if (!cut) return;

        if (preempting)
        {
            // The deferred archive commits NOW, capped at the same boundary the trash bank would use —
            // it has already banked everything accumulated, so no trash bank follows. The segment
            // opened above (TryBeginBossSegmentCutAcrossPreemption) survives this commit.
            ManualArchive(_pendingArchiveReason!.Value, replayUpperCapServerMs: firstHitMs - keepBeforeMs);
        }
        else if (ShouldArchiveTrashForBoss(priorCombat))
        {
            // Bank the trash IMMEDIATELY, capped at (firstHit − keepBefore). ManualArchive Clear()s the
            // combat clock; EnsureCombatStarted below re-establishes it for the boss segment.
            ManualArchive(AutoArchive.ArchiveReason.BossPhase, replayUpperCapServerMs: firstHitMs - keepBeforeMs);
        }
        // Start (trash→boss/preempt) or backdate (direct engage) the boss segment's combat clock at
        // (firstHit − keepBefore). In the trash/preempt case ManualArchive already Clear()ed (or is
        // about to have banked) the prior segment's clock, so this establishes the fresh one; in direct
        // engage it pre-empts OnCombatEvent's own EnsureCombatStarted(firstHit) so keepBefore is
        // honoured. With keepBefore == 0 and direct engage this is identical to the normal
        // EnsureCombatStarted(firstHit) — a no-op refinement.
        EnsureCombatStarted(firstHitMs - keepBeforeMs);
    }
}
