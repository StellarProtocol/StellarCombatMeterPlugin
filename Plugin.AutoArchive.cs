using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.AutoArchive;

namespace Stellar.CombatMeter;

// Auto-archive trigger wiring (Part B, 2026-07-17 sync/auto-archive spec): assembles the per-tick
// fact snapshot for the pure AutoArchiveEngine (~10 Hz, from OnUpdate's throttled region) and
// fires ManualArchive(reason) when the engine decides a segment ended. ALL policy (arm/fire/
// re-arm, cooldown, content guard) lives in the engine — this partial only reads services.
public sealed partial class Plugin
{
    private readonly AutoArchiveEngine _autoArchive = new();

    // Idle-settle delay (2026-07-18): an AUTO trigger fires the INSTANT the engine decides a segment
    // ended (a floor clear bumps EDungeonState, a wipe reads all-dead, etc.), but the mobs' corpses
    // are still present and trailing damage (DoTs, the killing-blow tick) is still landing — so
    // committing the snapshot immediately loses those last hits from the archived record. Rather than
    // wait a fixed interval, hold an AUTO archive until combat has gone QUIET: no damage event for this
    // long (owner ruling 2026-07-26: settle cares about DPS only — see SettleClockMs for which damage
    // clock a given reason watches; BossKill narrows to damage aimed at the boss).
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

    // Boss observation cache: entity id -> IsBoss, resolved at most once per distinct entity
    // (mirrors _replayMonsterInfo's contains-guard, Plugin.Replay.cs:163-169). BOUNDED: hard cap
    // + cleared by Clear() — the FPS-leak lesson (never an unbounded per-mob dict in the field).
    private const int MaxBossCheckEntries = 512;
    private readonly Dictionary<EntityId, bool> _bossCheck = new();

    // Bosses whose fight has already been observed dead THIS RUN — a corpse must never re-open a boss
    // segment. Extracted to AutoArchive.KilledBossTracker (review round, 2026-07-26): the mark/consult/
    // evict wiring is load-bearing and Plugin can't be instantiated in tests, so the pure state lives in
    // its own class (see its doc for the loop this closes + the FIFO-evicts-oldest cap rationale) and
    // only the two call sites below (mark in BossStatus, consult in CheckBossCandidate) stay here.
    // Cleared at the scene boundary (Plugin.History.cs OnSceneChanged) — NOT by Clear(), which runs on
    // every archive and would make the meter forget which bosses are already dead mid-run.
    private readonly AutoArchive.KilledBossTracker _killedBosses = new();

    // The currently-identified boss for trigger purposes. NOT cleared by Clear(): the framework's
    // vitals cache outlives a plugin archive, so BossStatus keeps tracking the same boss across
    // the boss-phase archive; scene resets / AOI disappear / the idle sweep wipe its vitals row,
    // which BossStatus reads as "gone" (the re-arm signal).
    private EntityId _autoArchiveBossId;

    // Finding 3 (2026-07-27): the boss id the BossKill settle clock watches damage against (see
    // IsSettleBossDamage). SEPARATE from _autoArchiveBossId — set at the same instant
    // (CheckBossCandidate) but cleared by NEITHER the confirmed death (BossStatus) NOR Clear() (which
    // runs on every banked archive, including the trash→boss bank that opens this very fight — the old
    // _bossCheck-keyed condition got wiped right there and the clock never engaged for the whole
    // fight). Only the scene boundary resets it (Plugin.History.cs OnSceneChanged), alongside
    // _killedBosses. A corpse DoT tick on the dead boss still targets this id, correctly holding the
    // settle window open.
    private EntityId _settleBossId;

    // ---- settings accessors (the AutoUpload pattern, Plugin.LogUpload.cs:79-91: cached in the
    // engine, persisted on set; loaded once by InitAutoArchive from the ctor) ----
    private const string PrefAaWipe         = "autoArchive.wipe";
    private const string PrefAaBoss         = "autoArchive.bossPhase";
    private const string PrefAaIdle         = "autoArchive.idle";
    private const string PrefAaIdleTimeoutS = "autoArchive.idleTimeoutS";
    private const string PrefAaStage        = "autoArchive.stageChange";

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
        var settleClock = SettleClockMs(pending, _lastDamageMs, _lastBossDamageMs);
        if (!PendingArchiveDue(now, settleClock, _archiveSettleMs) &&
            !PendingArchiveCapped(now, _pendingArchiveArmedMs, ArchiveIdleCapMs)) return;
        LogAutoArchiveCommit(pending, now, settleClock);
        ManualArchive(pending);   // ManualArchive clears _pendingArchiveReason on commit
    }

    /// <summary>True once combat has been quiet for <paramref name="idleSettleMs"/> — no activity on
    /// <paramref name="lastActivityMs"/> in that window, so trailing DoTs / the killing-blow tick have
    /// landed. <paramref name="lastActivityMs"/> is a caller-selected activity clock — production feeds
    /// it <see cref="SettleClockMs"/>'s result (damage only, boss-targeted for a
    /// <see cref="AutoArchive.ArchiveReason.BossKill"/>). Pure so it unit-tests headless (the
    /// AutoArchiveEngine precedent).</summary>
    internal static bool PendingArchiveDue(long nowMs, long lastActivityMs, long idleSettleMs)
        => nowMs - lastActivityMs >= idleSettleMs;

    /// <summary>Backstop for the idle wait: true once <paramref name="capMs"/> has elapsed since the
    /// trigger armed, so sustained combat with no scene change can't defer the archive forever.</summary>
    internal static bool PendingArchiveCapped(long nowMs, long armedMs, long capMs)
        => nowMs - armedMs >= capMs;

    /// <summary>Which activity clock the settle window watches for a given reason. Owner ruling
    /// 2026-07-26: settle cares about DAMAGE only — heals and damage-taken no longer reset it, so
    /// post-kill topping-up lands in the next segment instead of the fight. A
    /// <see cref="AutoArchive.ArchiveReason.BossKill"/> narrows that further to damage aimed at the
    /// boss, falling back to the general damage clock when no boss damage was recorded (an archive in
    /// between cleared it) so the window can never read "quiet forever" and commit instantly. Pure so
    /// it unit-tests headless.</summary>
    internal static long SettleClockMs(AutoArchive.ArchiveReason reason, long lastDamageMs, long lastBossDamageMs)
        => reason == AutoArchive.ArchiveReason.BossKill && lastBossDamageMs != 0 ? lastBossDamageMs : lastDamageMs;

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

    private AutoArchiveInputs BuildAutoArchiveInputs()
    {
        ScanRosterVitals(out var rosterSize, out var dead, out var unknown);
        var (bossPresent, bossGone, bossDead) = BossStatus();
        return new AutoArchiveInputs
        {
            NowMs            = _services.CombatSnapshot.ServerNowMs,
            CombatActive     = _combatActive,
            CombatStartMs    = _combatStartMs,
            LastDamageMs     = _lastDamageMs,
            HasStats         = _stats.Count > 0,
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

    // Boss liveness for the engine. Gone = a REAL death observation (HasHpObservation) or the
    // vitals row vanished (AOI disappear / scene reset / framework idle sweep all remove it).
    // dead is the CONFIRMED-death subset of gone (excludes a transient cache eviction): a death arms
    // the engine's BossKill want, while an eviction is ignored — only an archive ends a segment.
    //
    // Finding 4 (2026-07-27): _autoArchiveBossId is cleared ONLY on a confirmed death
    // (ShouldClearTrackedBoss), never on a transient eviction alone. Before this fix a mid-fight
    // vitals-cache blink cleared the id same as a real death, but re-adoption is gated behind
    // !bossSegmentActive (ShouldConsiderInlineBossCut), which stays closed for the WHOLE fight — so the
    // id was never re-set, BossDead never rose again, and no BossKill ever fired for that fight (it
    // only banked at the eventual run-end archive). Leaving the id in place lets the NEXT tick re-poll
    // vitals for the SAME entity, so a recovered blink resumes with no re-detect needed. Do NOT instead
    // move detection outside the !bossSegmentActive gate — a boss-tagged ADD would then get adopted
    // while the real boss's death is momentarily cleared, and the add's own death would fire a
    // spurious mid-fight BossKill.
    //
    // ACCEPTED RESIDUAL (not fixed, 2026-07-26): the mark below running BEFORE _autoArchiveBossId is
    // cleared — the ordering that makes the whole fix correct — cannot itself be unit-tested (Plugin
    // needs the IL2CPP service surface: _services.CombatLookup.GetVitals). What IS unit-tested
    // headless: the tracker (KilledBossTrackerTests), ShouldAdoptBossCandidate, and
    // ShouldClearTrackedBoss (all in AutoArchiveContentGuardTests). A known, named gap.
    private (bool present, bool gone, bool dead) BossStatus()
    {
        if (_autoArchiveBossId.Value == 0) return (false, false, false);
        var v = _services.CombatLookup.GetVitals(_autoArchiveBossId);
        bool dead    = v.HasHpObservation && v.MaxHp > 0 && v.Hp <= 0;
        bool evicted = !v.IsKnown;
        if (dead || evicted)
        {
            // Mark BEFORE clearing — this is the only place that still knows which entity died. Never
            // marks on a transient eviction, only a confirmed death.
            if (dead && _killedBosses.MarkKilled(_autoArchiveBossId) is { } evictedBossId)
                LogKilledBossEviction(evictedBossId, _autoArchiveBossId);
            if (ShouldClearTrackedBoss(dead)) _autoArchiveBossId = default;
            return (false, true, dead);
        }
        return (true, false, false);
    }

    /// <summary>Pure decision (finding 4): clear the tracked boss id ONLY on a confirmed death — a
    /// transient eviction (a blink) must not, or the boss can never resume tracking while a segment
    /// stays open (see BossStatus's doc). Unit-tested headless.</summary>
    internal static bool ShouldClearTrackedBoss(bool confirmedDead) => confirmedDead;

    // Called from OnCombatEvent (Plugin.Capture.cs) BEFORE the player-only early-out, next to
    // NoteReplayEntity — same "both sides of every event" coverage the boss-HP feature uses.
    private void ObserveAutoArchiveBoss(EntityId src, EntityId tgt)
    {
        if (!_autoArchive.BossEnabled || _autoArchiveBossId.Value != 0) return;
        CheckBossCandidate(src);
        if (_autoArchiveBossId.Value == 0) CheckBossCandidate(tgt);
    }

    private void CheckBossCandidate(EntityId id)
    {
        if (id.IsPlayer || id.Value == 0) return;
        if (!_bossCheck.TryGetValue(id, out var isBoss))
        {
            if (_bossCheck.Count >= MaxBossCheckEntries) return;   // runaway guard (field id churn)
            var info = _services.GameData.World.GetMonsterByEntity(id);
            isBoss = info.HasValue && info.Value.IsBoss;           // ResolveBossEntity's exact test
            _bossCheck[id] = isBoss;
        }
        if (!ShouldAdoptBossCandidate(isBoss, _killedBosses.IsKilled(id))) return;
        _autoArchiveBossId = id;
        _settleBossId = id;   // finding 3: stamp the settle-clock id at the same instant — see its field doc
    }

    /// <summary>Pure decision: may this entity become the tracked boss? Only a boss-tagged entity that
    /// has not already been observed dead this run. Barring an already-killed boss is what stops the
    /// post-kill cut loop (2026-07-26). Unit-tested headless.</summary>
    internal static bool ShouldAdoptBossCandidate(bool isBoss, bool alreadyKilled)
        => isBoss && !alreadyKilled;

    /// <summary>Pure decision (finding 3): does this damage event belong to the BossKill settle clock?
    /// Damage only (heals never count — owner ruling 2026-07-26: settle cares about DPS only), targeting
    /// the adopted settle boss (survives Clear() and its own death — see the field's doc — so a corpse
    /// DoT tick on the dead boss still counts). Zero-guard excludes "no boss adopted this run" (default
    /// id). Unit-tested headless.</summary>
    internal static bool IsSettleBossDamage(bool isHeal, EntityId targetId, EntityId settleBossId)
        => !isHeal && settleBossId.Value != 0 && targetId == settleBossId;

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

    /// <summary>Pure guard: should the inline boss cut even CONSIDER this event — i.e. detect the boss
    /// + (maybe) cut? Only when boss auto-archive is enabled, NO boss segment is currently active, AND
    /// we are in an instanced run. Keying on <c>bossSegmentActive</c> (NOT "boss already known") is the
    /// recut-fix (2026-07-21, run sea/U051Yv8lf2): once an archive closes the segment (<see
    /// cref="AutoArchiveEngine.OnArchived"/>, any reason except BossPhase) or a run/scene boundary clears
    /// it (<see cref="AutoArchiveEngine.UpdateLatches"/>), the inline cut must fire AGAIN — capped at
    /// firstHit − keepBefore — even if <c>_autoArchiveBossId</c> is still set. A transient eviction (boss
    /// gone but not confirmed dead) re-arms nothing on its own. The old "boss already known" gate skipped
    /// the re-detect, and the engine's now-removed boss branch fired an UNCAPPED archive at the tick "now"
    /// instead (keep-before boundary at 0:55 vs 0:48). The <c>inRun</c> gate keeps
    /// <c>_autoArchiveBossId</c> + the cut out of the open world. When a segment IS active the fight is
    /// running and this fast-exits (hot-path). Unit-tested headless.</summary>
    internal static bool ShouldConsiderInlineBossCut(bool bossEnabled, bool bossSegmentActive, bool inRun)
        => bossEnabled && !bossSegmentActive && inRun;

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
    private void MaybeCutForBossPhase(EntityId src, EntityId tgt, long firstHitMs, bool priorCombat)
    {
        if (!ShouldConsiderInlineBossCut(_autoArchive.BossEnabled, _autoArchive.BossSegmentActive, IsInstancedRun())) return;
        ObserveAutoArchiveBoss(src, tgt);              // sets _autoArchiveBossId iff this event involves the boss (no-op if already set)
        if (_autoArchiveBossId.Value == 0) return;     // this event didn't involve the boss — nothing to do

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
        // Start (trash→boss/preempt) or backdate (direct engage) the boss segment's clock at
        // (firstHit − keepBefore). With keepBefore == 0 and direct engage this is a no-op refinement.
        EnsureCombatStarted(firstHitMs - keepBeforeMs);
    }
}
