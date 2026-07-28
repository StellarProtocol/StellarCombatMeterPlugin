using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

// Boss-detection region: identifying which entity is THIS run's tracked boss, tracking its lifetime
// across a fight (including a transient vitals blink), and deciding which combat events count as
// "involving the boss" for the inline cut in Plugin.AutoArchive.cs's MaybeCutForBossPhase. Extracted
// out of Plugin.AutoArchive.cs (Minor E, review round 2026-07-27, second pass) — that file was at 499
// LoC, one line under the major-size threshold, partly because of doc comments trimmed for size in an
// earlier pass (restored here and in the sibling file instead of re-trimmed). The inline-cut ORCHESTRATION
// (ShouldArchiveTrashForBoss / ShouldPreemptPendingForBoss / ShouldConsiderInlineBossCut /
// MaybeCutForBossPhase) stays in Plugin.AutoArchive.cs — it decides WHEN to cut a segment; this file
// decides WHICH entity the boss is and WHETHER a given event touches it.
public sealed partial class Plugin
{
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
    //
    // Critical A (review round 2026-07-27, second pass): a run that ends without the tracked boss ever
    // being observed at hp<=0 (wipe-and-leave — the owner's normal loop, an abandoned pull, a fail-out,
    // the boss despawning on reset) used to leave this id pinned to a dead-and-gone entity for the REST
    // OF THE SESSION — ObserveAutoArchiveBoss's `!= 0` early-out then blocks every later boss from ever
    // being adopted again, silently disabling BossKill for every later fight. Fixed two ways: (1) the
    // scene boundary (Plugin.History.cs OnSceneChanged) now resets this alongside _killedBosses — a
    // fresh run's boss is a new identity, so this must not survive into it stale; (2) within the SAME
    // run (no scene change — a wipe-and-retry on a boss that is still alive), ShouldClearTrackedBoss now
    // also clears on an eviction that has no open segment to protect, instead of pinning forever
    // whenever the boss is never confirmed dead.
    //
    // RETIRED sibling (owner ruling 2026-07-28, defect 2): a separate _settleBossId used to ride on this
    // same adoption to drive a boss-targeted settle clock (finding 3, 2026-07-27). That narrowing is
    // withdrawn — see Plugin.AutoArchive.cs's retired-SettleClockMs note — so _settleBossId,
    // _lastBossDamageMs, and IsSettleBossDamage are all deleted; this field alone now drives adoption.
    private EntityId _autoArchiveBossId;

    // Boss liveness for the engine. Gone = a REAL death observation (HasHpObservation) or the
    // vitals row vanished (AOI disappear / scene reset / framework idle sweep all remove it).
    // dead is the CONFIRMED-death subset of gone (excludes a transient cache eviction): a death arms
    // the engine's BossKill want, while an eviction is ignored — only an archive ends a segment.
    //
    // Finding 4 (2026-07-27): _autoArchiveBossId used to clear ONLY on a confirmed death
    // (ShouldClearTrackedBoss), never on a transient eviction alone. Before that fix a mid-fight
    // vitals-cache blink cleared the id same as a real death, but re-adoption is gated behind
    // !bossSegmentActive (ShouldConsiderInlineBossCut), which stays closed for the WHOLE fight — so the
    // id was never re-set, BossDead never rose again, and no BossKill ever fired for that fight (it
    // only banked at the eventual run-end archive). Leaving the id in place lets the NEXT tick re-poll
    // vitals for the SAME entity, so a recovered blink resumes with no re-detect needed. Do NOT instead
    // move detection outside the !bossSegmentActive gate — a boss-tagged ADD would then get adopted
    // while the real boss's death is momentarily cleared, and the add's own death would fire a
    // spurious mid-fight BossKill.
    //
    // Critical A (review round 2026-07-27, second pass): that finding-4 fix was too broad — an eviction
    // with NO open segment (the fight already ended via an earlier archive: a wipe, a scene change, a
    // stage cut) has no fight left to protect, so pinning the id there just leaves it stuck on a corpse
    // forever (see _autoArchiveBossId's doc). ShouldClearTrackedBoss now also clears in that shape;
    // narrowing the blink protection to ONLY the case it was built for (a segment still open).
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
            if (ShouldClearTrackedBoss(dead, _autoArchive.BossSegmentActive)) _autoArchiveBossId = default;
            return (false, true, dead);
        }
        return (true, false, false);
    }

    /// <summary>Pure decision (finding 4, narrowed by Critical A — review round 2026-07-27): clear the
    /// tracked boss id when EITHER the death is confirmed, OR there is no open segment left to protect.
    /// Pins all three reachable cases:
    /// <list type="bullet">
    /// <item>confirmed death → clears, regardless of segment state (the fight is over either way).</item>
    /// <item>a transient eviction (blink) WHILE a segment is open → keeps, so BossStatus can re-poll the
    /// SAME entity next tick and resume tracking without a re-detect (the finding-4 fix this narrows,
    /// not reverts).</item>
    /// <item>an eviction with NO segment open (the fight already ended via an earlier archive) → clears
    /// — there is nothing left to protect, and pinning here is exactly what left the id stuck on a
    /// dead-and-gone entity for the rest of the session (Critical A).</item>
    /// </list>
    /// <para><b>DO NOT "simplify" the second term away.</b> The gone-timeout P0 fix
    /// (<see cref="AutoArchive.AutoArchiveEngine.BossGoneTimeoutMs"/>, 2026-07-28) rides on the middle
    /// case: its streak needs <c>BossGone</c> to stay TRUE across consecutive ticks, which only happens
    /// while <see cref="BossStatus"/> still holds the evicted id — i.e. while a segment is open. Revert
    /// this to a bare <c>confirmedDead</c> and <c>BossStatus</c> returns all-false from the second tick,
    /// the streak resets every tick, the timeout never fires, and the owner's raid wedges again — with
    /// NO failing test, because the engine tests inject <c>BossGone</c> directly rather than deriving it
    /// through this method, so the composition is untested by construction.</para>
    /// Unit-tested headless.</summary>
    internal static bool ShouldClearTrackedBoss(bool confirmedDead, bool segmentActive)
        => confirmedDead || !segmentActive;

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
    }

    /// <summary>Pure decision: may this entity become the tracked boss? Only a boss-tagged entity that
    /// has not already been observed dead this run. Barring an already-killed boss is what stops the
    /// post-kill cut loop (2026-07-26). Unit-tested headless.</summary>
    internal static bool ShouldAdoptBossCandidate(bool isBoss, bool alreadyKilled)
        => isBoss && !alreadyKilled;

    /// <summary>Pure decision (Critical A, review round 2026-07-27, second pass): does this combat event
    /// actually involve the tracked boss — as opposed to a boss merely being tracked at all? Replaces the
    /// old <c>_autoArchiveBossId.Value == 0</c> proxy in <c>MaybeCutForBossPhase</c>
    /// (Plugin.AutoArchive.cs), which was only valid the instant <c>ObserveAutoArchiveBoss</c> had just
    /// SET the id from this same event — once the id survives across archives (a still-alive boss after
    /// a wipe, or — before this fix — a stale id pinned past its own fight), the proxy read "a boss is
    /// tracked" as "this event is about the boss", letting an unrelated event (a rez heal between two
    /// players, corpse cleanup on an unrelated add) reach the cut below it (Important B: the wipe→retry
    /// run-back regression). Two struct compares, O(1), allocation-free — hot-path safe. Pinned: boss as
    /// source, boss as target, neither side is the boss, and a zero (never-adopted) boss id.</summary>
    internal static bool EventInvolvesBoss(EntityId src, EntityId tgt, EntityId bossId)
        => bossId.Value != 0 && (src == bossId || tgt == bossId);
}
