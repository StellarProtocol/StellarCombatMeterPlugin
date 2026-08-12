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

    // The SET of bosses engaged this stage (multi-boss per battle, Spec A). Replaces the single
    // _autoArchiveBossId latch this file used to carry. NOT cleared by Clear(): the framework's vitals
    // cache outlives a plugin archive, so BossStatus keeps polling the same members across a boss-phase
    // archive; scene resets / AOI disappear / the idle sweep clear a member's vitals row, which
    // BossStatus reads as "gone" (the re-arm signal) — exactly as the single latch worked. Reset ONLY at
    // the scene boundary (Plugin.History.cs OnSceneChanged), and drained at the boss cut (all members
    // gone, ShouldClearTrackedBoss allows it) so the next stage opens a fresh set. Pure decisions
    // (admit/aggregate/drain) live in StageBossSet (unit-tested); this file is the IL2CPP glue that
    // polls _services.CombatLookup.GetVitals per member.
    //
    // Critical A (review round 2026-07-27, second pass; carried into the set): a run that ends without a
    // tracked boss ever being observed at hp<=0 (wipe-and-leave — the owner's normal loop, an abandoned
    // pull, a fail-out, the boss despawning on reset) must not leave a member pinned dead-and-gone for
    // the rest of the session. Fixed the same two ways as the old single latch: (1) the scene boundary
    // resets the whole set alongside _killedBosses; (2) within the SAME run, ShouldClearTrackedBoss still
    // clears on an eviction that has no open segment to protect (now applied to the SET's aggregate
    // gone/dead via Aggregate()), instead of pinning forever whenever a member is never confirmed dead.
    //
    // RETIRED sibling (owner ruling 2026-07-28, defect 2): a separate _settleBossId used to ride on the
    // old single-latch adoption to drive a boss-targeted settle clock (finding 3, 2026-07-27). That
    // narrowing stays withdrawn — see Plugin.AutoArchive.cs's retired-SettleClockMs note.
    private readonly AutoArchive.StageBossSet _stageBosses = new();

    // Per-member last LIVE Hp/MaxHp fraction, for the raid scripted-kill inference (HP never reads 0,
    // entity then vanishes). Keyed by entity; entries are pruned alongside the set at scene change AND
    // when the set drains at a boss cut (2026-08-12 review, amendment 6) — bounded either way, and keeps
    // the map from carrying corpse entries across stages within one run.
    private readonly Dictionary<EntityId, float> _memberLastHpFrac = new();

    // Per-segment boss upload fields (raid per-stage bossId + scripted-kill flag). UPLOAD-ONLY — none of
    // these feed BossStatus's (present,gone,dead) tuple or any engine gate, so cut timing/count is
    // unchanged (invariants 6/8). TEMPORARY REPRESENTATIVE MIRROR (multi-boss plan Task 2, 2026-08-12):
    // now derived from the FIRST-admitted member of _stageBosses (CheckBossCandidate on first admission;
    // BossStatus per tick) rather than the old single latch — for a single-boss stage (every stage
    // today) this is byte-identical to the old behavior. Task 6 replaces this mirror with a proper
    // roster-preferred pick built from the whole set at archive time and deletes these scalars.
    private int   _segmentBossConfigId;          // monster config id of the boss THIS segment engaged; 0 = none
    private bool  _segmentBossKilled;            // this segment's tracked boss was observed killed (upload flag)
    private float _segmentBossLastHpFrac = -1f;  // last LIVE Hp/MaxHp of the tracked boss; -1 = never observed

    // Scripted raid bosses are brought to ~1% then killed by a triggered event (HP never reads 0, entity
    // vanishes). Treat "last seen at/under this fraction, then evicted" as a kill — RAID-GATED so a dungeon
    // boss the player merely walked away from at low HP is never counted (dungeons keep pure HP<=0).
    private const float BossScriptedKillHpFrac = 0.15f;

    // Raid-content gate (owner: party size does NOT classify content — Golem is 20p but not a raid, so
    // PartyType.Raid20 is wrong here). mapId is derived the SAME way BuildEncounter derives the uploaded
    // mapId — ParseMapId(_lastSceneName), the scene id string that becomes encounter.mapId. The nine raid
    // ids are documented in docs/recon/combatmeter-data-facts.md.
    private static readonly HashSet<int> RaidMapIds =
        new() { 13001, 13002, 13003, 13011, 13012, 13013, 13021, 13022, 13023 };
    private bool IsRaidContent() => RaidMapIds.Contains(ParseMapId(_lastSceneName));

    // Boss liveness for the engine. Gone = a REAL death observation (HasHpObservation) or the
    // vitals row vanished (AOI disappear / scene reset / framework idle sweep all remove it).
    // dead is the CONFIRMED-death subset of gone (excludes a transient cache eviction): a death arms
    // the engine's BossKill want, while an eviction is ignored — only an archive ends a segment.
    // Aggregate() is the SET-level lift of this same present/gone/dead shape: present = ANY member
    // present; gone = ALL members gone; dead = set non-empty and ALL members killed (multi-boss plan,
    // Task 1/2) — the intentional timing change that keeps a two-boss stage as ONE entry.
    //
    // Finding 4 (2026-07-27): the old single latch used to clear ONLY on a confirmed death
    // (ShouldClearTrackedBoss), never on a transient eviction alone. Before that fix a mid-fight
    // vitals-cache blink cleared the id same as a real death, but re-adoption is gated behind
    // !bossSegmentActive (ShouldConsiderInlineBossCut), which stays closed for the WHOLE fight — so the
    // id was never re-set, BossDead never rose again, and no BossKill ever fired for that fight (it
    // only banked at the eventual run-end archive). Leaving a member's liveness Present=false (not
    // removed) lets the NEXT tick re-poll vitals for the SAME entity, so a recovered blink resumes with
    // no re-detect needed — preserved per-member below: a lone eviction sets Present=false, Dead=false,
    // so while another member is present Aggregate() stays (true,false,false) and re-polls next tick.
    //
    // Critical A (review round 2026-07-27, second pass): that finding-4 fix was too broad — an eviction
    // with NO open segment (the fight already ended via an earlier archive: a wipe, a scene change, a
    // stage cut) has no fight left to protect, so pinning a member there just leaves the set stuck on a
    // corpse forever. ShouldClearTrackedBoss still clears in that shape (now gating DrainIfAllGone at
    // the SET level); narrowing the blink protection to ONLY the case it was built for (a segment still
    // open).
    //
    // ACCEPTED RESIDUAL (not fixed, 2026-07-26): the mark below running BEFORE a member's liveness is
    // recorded — the ordering that makes the whole fix correct — cannot itself be unit-tested (Plugin
    // needs the IL2CPP service surface: _services.CombatLookup.GetVitals). What IS unit-tested
    // headless: the tracker (KilledBossTrackerTests), ShouldAdoptBossCandidate, ShouldClearTrackedBoss
    // (all in AutoArchiveContentGuardTests), and the set's own aggregate rules (StageBossSetTests). A
    // known, named gap — this method is the IL2CPP glue Task 2 exists to wire, not to prove.
    //
    // Alloc-free hot path (2026-08-12 review, amendment 1): this runs EVERY FRAME
    // (BuildAutoArchiveInputs), so the loop below is a plain indexed `for` over Count/MemberAt — no
    // LINQ, no MembersSnapshot() (that allocates and is archive-time-only).
    private (bool present, bool gone, bool dead) BossStatus()
    {
        if (_stageBosses.Count == 0) return (false, false, false);

        for (var i = 0; i < _stageBosses.Count; i++)
        {
            var (id, _, _) = _stageBosses.MemberAt(i);
            var v = _services.CombatLookup.GetVitals(id);
            bool dead    = v.HasHpObservation && v.MaxHp > 0 && v.Hp <= 0;
            bool evicted = !v.IsKnown;

            // UPLOAD-ONLY: remember this member's last LIVE HP fraction for the scripted-kill inference
            // below. Does NOT feed the (present,gone,dead) tuple, so the engine's cut timing/count is
            // byte-identical (invariants 6/8).
            if (v.HasHpObservation && v.MaxHp > 0) _memberLastHpFrac[id] = (float)v.Hp / v.MaxHp;
            var lastFrac = _memberLastHpFrac.TryGetValue(id, out var f) ? f : -1f;

            // Scripted raid bosses are brought to ~1% then killed by a triggered event (HP never reads
            // 0, entity then vanishes). RAID-GATED so a dungeon boss the player merely walked away from
            // at low HP is never counted — dungeons keep pure HP<=0 semantics.
            bool scriptedKill = evicted && IsRaidContent()
                && lastFrac >= 0f && lastFrac <= BossScriptedKillHpFrac;

            // Mark BOTH trackers on a confirmed/scripted kill (2026-08-12 review, amendment 2). A member
            // now STAYS in the set (sticky Killed) instead of being cleared like the old single latch,
            // so without the !IsKilled gate this would re-add-and-re-log every tick for as long as a
            // co-boss keeps the stage open. Also closes a phantom-stage hole: an unmarked scripted-killed
            // corpse in _killedBosses could otherwise be re-admitted by CheckBossCandidate right after
            // DrainIfAllGone.
            if ((dead || scriptedKill) && !_killedBosses.IsKilled(id)
                && _killedBosses.MarkKilled(id) is { } evictedBossId)
                LogKilledBossEviction(evictedBossId, id);

            // present when alive; a confirmed/scripted kill records Dead (sticky Killed); a bare
            // eviction records not-present (blink) but not dead — Aggregate() decides gone = ALL gone.
            var live = new AutoArchive.StageBossSet.BossLiveness
            {
                Present = !dead && !evicted,
                Dead    = dead || scriptedKill,
            };
            _stageBosses.SetLiveness(id, live);

            // UPLOAD-ONLY representative mirror (Task 2 stopgap — see the field doc; Task 6 replaces
            // this with a roster-preferred pick built from the whole set at archive time): the
            // FIRST-admitted member drives the per-segment scalar upload fields exactly as the single
            // latch used to.
            if (i == 0)
            {
                _segmentBossLastHpFrac = lastFrac;
                if (dead || scriptedKill) _segmentBossKilled = true;
            }
        }

        var agg = _stageBosses.Aggregate();
        // Mirror the old single-latch clear-on-death/no-open-segment behavior at the SET level: once the
        // whole stage is gone and no segment is open to protect, drain so the next stage opens fresh.
        // Prune this drain's members out of _memberLastHpFrac too (amendment 6) — read ids BEFORE
        // draining, since DrainIfAllGone clears the set's membership.
        if (agg.gone && ShouldClearTrackedBoss(agg.dead, _autoArchive.BossSegmentActive))
        {
            for (var i = 0; i < _stageBosses.Count; i++)
                _memberLastHpFrac.Remove(_stageBosses.MemberAt(i).id);
            _stageBosses.DrainIfAllGone();
        }
        return agg;
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
    // NoteReplayEntity — same "both sides of every event" coverage the boss-HP feature uses. NO
    // "already tracked" early-out (multi-boss plan Task 2): the old single latch stopped checking once
    // one boss was adopted; the set must keep admitting so a co-boss engaged later in the same stage is
    // seen too. CheckBossCandidate / StageBossSet.Admit are the actual gates (already-tracked id,
    // already-killed id, closed stage, MaxMembers all no-op harmlessly).
    private void ObserveAutoArchiveBoss(EntityId src, EntityId tgt)
    {
        if (!_autoArchive.BossEnabled) return;
        CheckBossCandidate(src);
        CheckBossCandidate(tgt);
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
        // Snapshot the boss's config id NOW (caches live at adoption) — the vitals/attr row is gone by
        // the deferred BossKill archive (same reason _bossMonsterInfo used to be snapshotted early in
        // ResolveBossEntity). Admit into the set: no-op if the stage is closed, this id is already
        // tracked, or the set is at MaxMembers.
        var configId = _services.GameData.World.GetMonsterByEntity(id)?.Id ?? 0;
        if (!_stageBosses.Admit(id, configId)) return;
        // UPLOAD-ONLY: the FIRST-ever admission into a fresh set opens a new per-segment upload window
        // (mirrors the old single latch's adoption reset — see the representative-mirror field doc). A
        // later co-boss admission into an ALREADY-open set must not clobber it.
        if (_stageBosses.Count == 1)
        {
            _segmentBossConfigId   = configId;
            _segmentBossLastHpFrac = -1f;
            _segmentBossKilled     = false;
        }
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

    /// <summary>Set-aware wrapper (multi-boss plan Task 2): does this combat event involve ANY current
    /// stage member? Replaces the single-id <c>EventInvolvesBoss(src, tgt, _autoArchiveBossId)</c> call
    /// in <c>MaybeCutForBossPhase</c> (Plugin.AutoArchive.cs) — the pure two-id overload above stays as
    /// it is for its own unit tests. Indexed Count/MemberAt iteration (2026-08-12 review, amendment 1):
    /// this runs per COMBAT EVENT, so no LINQ/allocation.</summary>
    private bool EventInvolvesAnyStageBoss(EntityId src, EntityId tgt)
    {
        for (var i = 0; i < _stageBosses.Count; i++)
        {
            var (id, _, _) = _stageBosses.MemberAt(i);
            if (EventInvolvesBoss(src, tgt, id)) return true;
        }
        return false;
    }
}
