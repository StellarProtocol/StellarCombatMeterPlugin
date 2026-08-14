using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.Replay;

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
    // Monster observation cache: entity id -> (IsBoss, IsElite, ConfigId), resolved at most once per
    // distinct entity (mirrors _replayMonsterInfo's contains-guard, Plugin.Replay.cs:163-169). BOUNDED:
    // hard cap + cleared by Clear() — the FPS-leak lesson (never an unbounded per-mob dict in the field).
    // Important 3 (final review): ConfigId rides alongside IsBoss now — the SAME GetMonsterByEntity call
    // that resolves IsBoss already has it, so a later admission off a cache hit needs no second interop
    // call (CheckBossCandidate used to re-call GetMonsterByEntity on every boss-touching event just to
    // fetch the config id, even once IsBoss was already cached).
    // IsElite added (ELITE CAPTURE channel, owner ruling 2026-08-13): the SAME GetMonsterByEntity call
    // already carries MonsterType, so CheckEliteCandidate (Plugin.EliteDetection.cs) rides this SAME
    // cache entry via the shared ResolveMonsterCandidate helper below — a distinct entity id costs
    // exactly ONE interop call total regardless of whether the boss check or the elite check resolves it
    // first (whichever runs first for a fresh id fills this one entry for both).
    private const int MaxBossCheckEntries = 512;
    private readonly Dictionary<EntityId, (bool IsBoss, bool IsElite, int ConfigId)> _bossCheck = new();

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

    // Sticky snapshot of _stageBosses' last known NON-EMPTY membership (final review, Critical 1: kill
    // archives were shipping empty bosses[]). Two places drain/clear the LIVE set before a deferred
    // archive's BuildHistoryEntry (Plugin.History.cs) can read it: (1) BossStatus below, via
    // DrainIfAllGone() on the SAME tick the last member dies/is scripted-killed — the deferred boss-kill
    // archive fires later, onto an already-empty set; (2) ResetRunScopedTrackers's _stageBosses.Clear()
    // (Plugin.RunBoundary.cs), which the always-firing scene archive calls BEFORE its own
    // BuildHistoryEntry — even for a stage that never drained (still open, abandoned at scene change).
    // LatchStageBosses() (below) re-latches at both points, ONLY when non-empty, so a later empty
    // clear/drain can never overwrite a previously latched segment with nothing. ResolveCurrentStageBosses
    // prefers the LIVE set and falls back here only when it is empty — a still-open multi-boss stage is
    // unaffected. Always assigned a WHOLE NEW list (StageBossSet.MembersSnapshot()), never mutated in
    // place, so an already-archived entry's copy of an older snapshot can never change out from under it.
    // Reset at TWO points, each AFTER the archive entitled to read it has had its chance: (1) Clear()
    // (Plugin.cs), run AFTER BuildHistoryEntry has read it for a REAL bank — mirrors _segmentBossKilled's
    // per-archive Clear() reset, commit 9346ece, before the multi-boss set replaced the single-boss scalar
    // (that mirror claim covers ONLY this reset point — the retired scalar had no boundary-scoped
    // counterpart of its own). (2) BankRunBoundary (Plugin.RunBoundary.cs), run immediately after its
    // ManualArchive(reason) call, beside `_lastRunId = 0` — new finding, re-review 2026-08-13: a boss
    // admitted by a whiffed/abandoned pull (CheckBossCandidate runs on every combat event, even 0-amount
    // ones) then dropped via ManualArchive's skip-empty or suppressed-junk early return (both SKIP
    // Clear()) left this latch holding a dead run's boss past its own run boundary — a LATER, unrelated
    // banked archive (next run, no boss engaged) would read it via ResolveCurrentStageBosses and
    // misattribute a stale boss to itself. Both reset points are boundary/archive-scoped ONLY — never
    // inside ResetRunScopedTrackers/OnSceneChanged's early half, which runs BEFORE the scene archive's own
    // BuildHistoryEntry and would reproduce the ORIGINAL Critical-1 bug if cleared there — so a within-run
    // suppressed cut (same stage, no boundary crossed) still keeps the latch correctly. With both reset
    // points in place, the "never bleeds into the next [segment/run]" claim is true again.
    private IReadOnlyList<(EntityId Id, int ConfigId, bool Killed)> _segmentStageBosses =
        Array.Empty<(EntityId Id, int ConfigId, bool Killed)>();

    // Per-member last LIVE Hp/MaxHp fraction, for the raid scripted-kill inference (HP never reads 0,
    // entity then vanishes). Keyed by entity; entries are pruned alongside the set at scene change AND
    // when the set drains at a boss cut (2026-08-12 review, amendment 6) — bounded either way, and keeps
    // the map from carrying corpse entries across stages within one run.
    private readonly Dictionary<EntityId, float> _memberLastHpFrac = new();

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
            LatchStageBosses();   // Critical 1: preserve this stage's final state before the drain empties it
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
    //
    // TOGGLE-INDEPENDENT (owner ruling 2026-08-14, verbatim intent: "boss tracking is supposed to be a
    // default feature"): the `if (!_autoArchive.BossEnabled) return;` that used to head this method is
    // GONE, as is the bossEnabled term in the caller's ShouldConsiderBossAdmission gate — boss-set
    // admission is now always-on during an instanced run, mirroring the elite capture channel
    // (Plugin.EliteDetection.cs's ObserveEliteCandidates). The ONLY remaining gate is the caller's
    // inRun test. Extends protected archive-flow invariant 5 to the SET; the Boss-phase toggle keeps
    // gating the per-boss archive CUTS alone (invariant 8).
    //
    // WHY THIS IS SAFE WITH THE TOGGLE OFF (traced 2026-08-14 — read this before re-gating anything).
    // Un-gating admission means _stageBosses can now be non-empty while Boss phase is OFF, so BossStatus()
    // no longer short-circuits on `Count == 0` and feeds AutoArchiveInputs.Boss{Present,Gone,Dead} REAL
    // readings for the first time. `BossEnabled == false` together with a live boss reading was previously
    // an unreachable input combination. It changes no decision, because every engine consumer of those
    // readings is transitively gated on _bossSegmentActive (AutoArchive/AutoArchiveEngine.cs):
    //   • TryBeginBossSegmentCut refuses to set _bossSegmentActive while !BossEnabled — so it stays false
    //     for the whole toggle-off run, and it is the ONLY thing that sets it;
    //   • UpdateLatches arms _bossKillWanted only under `s.BossDead && _bossSegmentActive`, and zeroes the
    //     gone-timeout streak (_bossGoneSinceMs) whenever !_bossSegmentActive — so neither of the two ways
    //     a BossKill can arm is reachable;
    //   • Evaluate's fire is additionally gated `BossEnabled && _bossKillWanted` (belt and suspenders);
    //   • the settle window watches the GENERAL damage clock (_lastDamageMs, Plugin.AutoArchive.cs) and
    //     carries no boss term at all, so cut TIMING is untouched too.
    // Net: with the toggle off the engine's decisions are byte-identical, and no want can latch while off
    // and then fire if the owner flips the toggle mid-run. Both properties are pinned by
    // AutoArchiveEngineTests.Boss_readings_are_inert_while_boss_disabled (a differential test against a
    // control engine fed the old all-false tuple) and .A_kill_seen_while_boss_disabled_does_not_fire_
    // when_the_toggle_is_turned_on. The side effects BossStatus() now performs on a toggle-off run are all
    // capture-side and desired: _memberLastHpFrac (upload-only), _killedBosses marks, and the
    // LatchStageBosses/DrainIfAllGone pair that hands bosses[] to the archive.
    private void ObserveAutoArchiveBoss(EntityId src, EntityId tgt)
    {
        CheckBossCandidate(src);
        CheckBossCandidate(tgt);
    }

    private void CheckBossCandidate(EntityId id)
    {
        if (id.IsPlayer || id.Value == 0) return;
        // Important 3 (final review): an already-admitted member can never be re-admitted (Admit's own
        // dupe check below would just no-op), so skip the cache lookup AND the interop call entirely for
        // one — a cheap Count/MemberAt-bounded scan, no allocation.
        if (_stageBosses.Contains(id)) return;
        var cached = ResolveMonsterCandidate(id);
        if (cached is null) return;   // runaway guard hit (MaxBossCheckEntries) — field id churn
        if (!ShouldAdoptBossCandidate(cached.Value.IsBoss, _killedBosses.IsKilled(id))) return;
        // Admit into the set with the config id already resolved above: no-op if the stage is closed,
        // this id is already tracked, or the set is at MaxMembers.
        _stageBosses.Admit(id, cached.Value.ConfigId);
    }

    /// <summary>Shared cache lookup/fill for <c>_bossCheck</c> — the ONE interop call site
    /// (<c>GetMonsterByEntity</c>) for both boss admission (<see cref="CheckBossCandidate"/>) and elite
    /// admission (<c>CheckEliteCandidate</c>, Plugin.EliteDetection.cs). Whichever check sees a fresh
    /// entity id first pays the interop cost and fills the entry; the other always hits the cache.
    /// Returns null when the runaway guard (<see cref="MaxBossCheckEntries"/>) is hit for a NEW id —
    /// same fail-closed behavior <c>CheckBossCandidate</c> always had (one non-boss/non-elite id gets
    /// re-resolved next time, never a stuck admission).</summary>
    private (bool IsBoss, bool IsElite, int ConfigId)? ResolveMonsterCandidate(EntityId id)
    {
        if (_bossCheck.TryGetValue(id, out var cached)) return cached;
        if (_bossCheck.Count >= MaxBossCheckEntries) return null;   // runaway guard (field id churn)
        var info = _services.GameData.World.GetMonsterByEntity(id);
        var isBoss  = info.HasValue && info.Value.IsBoss;                              // ResolveBossEntity's exact test
        var isElite = info.HasValue && info.Value.MonsterType == MonsterTypeElite;
        // Cache the config id alongside isBoss/isElite — the SAME call already has it (caches live now,
        // and the vitals/attr row is gone by the deferred archive, same reason _bossMonsterInfo used to
        // be snapshotted early in ResolveBossEntity), so admission off a cache hit below never repeats
        // this interop call.
        cached = (isBoss, isElite, info?.Id ?? 0);
        _bossCheck[id] = cached;
        return cached;
    }

    // EMonsterType.Elite = 1 (Abstractions' MonsterInfo only names the Boss=2 constant since only bosses
    // were classified before the ELITE CAPTURE channel, owner ruling 2026-08-13). See MonsterInfo.cs's
    // own doc: "0=Monster, 1=Elite, 2=Boss".
    private const int MonsterTypeElite = 1;

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

    // -----------------------------------------------------------------------
    // Sticky latch (final review, Critical 1 + Important 2)
    // -----------------------------------------------------------------------

    /// <summary>Re-latches <see cref="_segmentStageBosses"/> from the LIVE set — but ONLY when the live
    /// set is non-empty, so a call against an already-drained/never-populated set can never overwrite a
    /// previously latched segment with nothing. Called at both points that empty the live set: here (via
    /// <see cref="BossStatus"/>, right before <c>DrainIfAllGone()</c>) and <c>ResetRunScopedTrackers</c>
    /// (Plugin.RunBoundary.cs, right before <c>_stageBosses.Clear()</c>).</summary>
    private void LatchStageBosses()
    {
        if (_stageBosses.Count > 0) _segmentStageBosses = _stageBosses.MembersSnapshot();
    }

    /// <summary>Pure decision (final review, Critical 1): prefer the LIVE stage-boss membership, falling
    /// back to the sticky latch only when the live set is empty. This is the exact shape a deferred
    /// archive hits after the live set has already drained (same tick as the last member's death) or
    /// been cleared by a run boundary (scene change) before the archive's own BuildHistoryEntry ran.
    /// Pure/static so it pins headless without a live Plugin instance — <see cref="ResolveCurrentStageBosses"/>
    /// is the IL2CPP-adjacent instance wrapper that supplies the two arguments.</summary>
    internal static IReadOnlyList<(EntityId Id, int ConfigId, bool Killed)> PreferLiveStageBosses(
        IReadOnlyList<(EntityId Id, int ConfigId, bool Killed)> live,
        IReadOnlyList<(EntityId Id, int ConfigId, bool Killed)> latched)
        => live.Count > 0 ? live : latched;

    /// <summary>Archive-time resolver: the LIVE stage-boss set when it still has members, else
    /// <see cref="_segmentStageBosses"/>. Consumed by BuildHistoryEntry (Plugin.History.cs) and
    /// BuildBossHpTracks (Plugin.Replay.cs, feeds BuildWindowBossMembers). Allocates via
    /// MembersSnapshot() — archive/window-assembly time only, never per-frame (see
    /// <see cref="TickStageBossHpTracks"/> for the alloc-free hot-path equivalent).</summary>
    private IReadOnlyList<(EntityId Id, int ConfigId, bool Killed)> ResolveCurrentStageBosses()
        => PreferLiveStageBosses(_stageBosses.MembersSnapshot(), _segmentStageBosses);

    /// <summary>Ticks every stage boss's HP track for the replay sampler (multi-boss plan Task 3;
    /// Important 2 fix, final review): Track/MarkDead off the LIVE set when non-empty, else the sticky
    /// latch — so the member whose death drains the live set on THIS tick (BossStatus above) still gets
    /// its terminal MarkDead stamp on later ticks, instead of vanishing from this loop the instant it's
    /// gone. Called every replay tick (Plugin.Replay.cs's TickHpTimelines) — alloc-free either way:
    /// MemberAt is indexed access into the live set; the latch is an already-allocated snapshot from the
    /// drain/reset moment, indexed here with a plain [] read — no new allocation on this per-frame
    /// path.</summary>
    private void TickStageBossHpTracks(HpTimelineSampler sampler, long combatStartMs, int nowMs)
    {
        if (_stageBosses.Count > 0)
        {
            for (var i = 0; i < _stageBosses.Count; i++)
            {
                var (id, _, killed) = _stageBosses.MemberAt(i);
                sampler.Track(id.Value, nowMs - combatStartMs);
                if (killed) sampler.MarkDead(id.Value, nowMs - combatStartMs);
            }
            return;
        }
        for (var i = 0; i < _segmentStageBosses.Count; i++)
        {
            var (id, _, killed) = _segmentStageBosses[i];
            sampler.Track(id.Value, nowMs - combatStartMs);
            if (killed) sampler.MarkDead(id.Value, nowMs - combatStartMs);
        }
    }
}
