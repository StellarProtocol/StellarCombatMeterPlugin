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
    // wait a fixed interval, hold an AUTO archive until combat has gone QUIET: no combat event of any
    // channel for this long (every dealt/heal/taken event resets the window via _lastCombatEventMs).
    // If it's already been this quiet when the trigger fires, the archive commits immediately. There
    // is a comfortable window: after a floor clear the game shows "Enter the next floor in 5s". A
    // MANUAL (button/hotkey) archive and the SceneChange archive (which must beat the entity teardown)
    // stay IMMEDIATE.
    internal const long ArchiveIdleSettleMs = 2_000;

    // Backstop: sustained combat that never goes quiet (and no scene change to supersede the pending)
    // would defer the archive forever. Commit anyway once this long has elapsed since the trigger
    // armed. A scene change already supersedes the pending, so this is only a rare-case safety net.
    internal const long ArchiveIdleCapMs = 15_000;

    // The single pending deferred-archive slot. Set when the engine returns a deferrable reason;
    // committed once combat has been quiet for ArchiveIdleSettleMs (or the cap elapses); cleared by
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

    // The currently-identified boss for trigger purposes. NOT cleared by Clear(): the framework's
    // vitals cache outlives a plugin archive, so BossStatus keeps tracking the same boss across
    // the boss-phase archive; scene resets / AOI disappear / the idle sweep wipe its vitals row,
    // which BossStatus reads as "gone" (the re-arm signal).
    private EntityId _autoArchiveBossId;

    // ---- settings accessors (the AutoUpload pattern, Plugin.LogUpload.cs:79-91: cached in the
    // engine, persisted on set; loaded once by InitAutoArchive from the ctor) ----
    private const string PrefAaWipe         = "autoArchive.wipe";
    private const string PrefAaBoss         = "autoArchive.bossPhase";
    private const string PrefAaIdle         = "autoArchive.idle";
    private const string PrefAaIdleTimeoutS = "autoArchive.idleTimeoutS";
    private const string PrefAaStage        = "autoArchive.stageChange";

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

    private void InitAutoArchive()
    {
        _autoArchive.WipeEnabled   = _prefs.Get(PrefAaWipe, true);
        _autoArchive.BossEnabled   = _prefs.Get(PrefAaBoss, true);
        _autoArchive.IdleEnabled   = _prefs.Get(PrefAaIdle, true);
        _autoArchive.StageEnabled  = _prefs.Get(PrefAaStage, true);
        _autoArchive.IdleTimeoutMs = _prefs.Get(PrefAaIdleTimeoutS, 60) * 1000L;
    }

    // ~10 Hz from OnUpdate's throttled region (Plugin.cs). An AUTO trigger is deferred until combat
    // goes quiet for ArchiveIdleSettleMs so trailing damage lands before the snapshot (see the field
    // docs); during the wait we stop evaluating new triggers so the engine can't re-fire/duplicate.
    private void TickAutoArchiveTriggers()
    {
        if (_paused) return;

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
        if (!PendingArchiveDue(now, _lastCombatEventMs, ArchiveIdleSettleMs) &&
            !PendingArchiveCapped(now, _pendingArchiveArmedMs, ArchiveIdleCapMs)) return;
        LogAutoArchiveCommit(pending, now);
        ManualArchive(pending);   // ManualArchive clears _pendingArchiveReason on commit
    }

    /// <summary>True once combat has been quiet for <paramref name="idleSettleMs"/> — no combat event
    /// (any dealt/heal/taken channel, tracked by <c>_lastCombatEventMs</c>) in that window, so trailing
    /// DoTs / the killing-blow tick have landed. Pure so it unit-tests headless (the AutoArchiveEngine
    /// precedent).</summary>
    internal static bool PendingArchiveDue(long nowMs, long lastCombatEventMs, long idleSettleMs)
        => nowMs - lastCombatEventMs >= idleSettleMs;

    /// <summary>Backstop for the idle wait: true once <paramref name="capMs"/> has elapsed since the
    /// trigger armed, so sustained combat with no scene change can't defer the archive forever.</summary>
    internal static bool PendingArchiveCapped(long nowMs, long armedMs, long capMs)
        => nowMs - armedMs >= capMs;

    /// <summary>True for the engine-driven AUTO reasons that should wait out the settle delay
    /// (a floor-clear <see cref="AutoArchive.ArchiveReason.StageChange"/>, wipe, boss, idle). A
    /// <see cref="AutoArchive.ArchiveReason.Manual"/> button/hotkey archive stays immediate, and
    /// <see cref="AutoArchive.ArchiveReason.SceneChange"/> must beat the entity teardown at the
    /// boundary — neither defers. Pure so it unit-tests headless.</summary>
    internal static bool IsDeferrableArchive(AutoArchive.ArchiveReason reason) => reason switch
    {
        AutoArchive.ArchiveReason.Wipe        => true,
        AutoArchive.ArchiveReason.BossPhase   => true,
        AutoArchive.ArchiveReason.Idle        => true,
        AutoArchive.ArchiveReason.StageChange => true,
        _                                     => false,   // Manual + SceneChange stay immediate
    };

    /// <summary>Should this archive FINALIZE the position replay — assemble + upload the whole-run
    /// track and reset it? The replay is ONE continuous capture per dungeon run; mid-run auto
    /// archives (stage change / boss phase / idle) bank their DAMAGE segment but must NOT truncate
    /// the replay, or one dungeon splits into slices and the upload is only the final slice
    /// (shorter than the game's clear time — owner report 2026-07-19). Finalize only on a
    /// run-TERMINAL archive: the user's manual archive, a scene change out of the run, a wipe, or
    /// the kill/settlement that ends the run. A non-kill mid-run auto trigger leaves the replay
    /// accumulating. Pure, unit-tested (ReplayFinalizeGateTests).</summary>
    internal static bool ShouldFinalizeReplay(AutoArchive.ArchiveReason reason, bool isKill)
        => reason == AutoArchive.ArchiveReason.Manual
        || reason == AutoArchive.ArchiveReason.SceneChange
        || reason == AutoArchive.ArchiveReason.Wipe
        || isKill;

    private AutoArchiveInputs BuildAutoArchiveInputs()
    {
        ScanRosterVitals(out var rosterSize, out var dead, out var unknown);
        var (bossPresent, bossGone) = BossStatus();
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
            InstancedRun     = IsInstancedRun(),
            FlowStateVersion = _services.Dungeon.FlowStateVersion,
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
    private (bool present, bool gone) BossStatus()
    {
        if (_autoArchiveBossId.Value == 0) return (false, false);
        var v = _services.CombatLookup.GetVitals(_autoArchiveBossId);
        bool dead    = v.HasHpObservation && v.MaxHp > 0 && v.Hp <= 0;
        bool evicted = !v.IsKnown;
        if (dead || evicted) { _autoArchiveBossId = default; return (false, true); }
        return (true, false);
    }

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
        if (isBoss) _autoArchiveBossId = id;
    }
}
