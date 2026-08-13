using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.CombatMeter.LogUpload;
using Stellar.CombatMeter.Replay;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    // -----------------------------------------------------------------------
    // Constants
    // -----------------------------------------------------------------------

    private const int    ReplaySampleIntervalMs  = 500;       // 2 Hz
    private const int    ReplayMaxSamplesPerTrack = 3600;
    private const int    ReplayMaxTotalSamples    = 200_000;

    // Skip ALL live-transform probing for this long after a scene change (line-switch / dungeon-
    // enter): the game mass-frees and re-streams nearby entities and the framework's AOI-disappear
    // bookkeeping can lag the native teardown by a few frames. See IsWithinReplaySettle.
    internal const long  ReplaySettleMs           = 2_000;

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    private ReplayCapture? _replay;

    // Latches the dungeon run-id TickReplayCapture last observed, so a transition to a
    // DIFFERENT run (including via 0, e.g. a crash → re-enter) can be detected and the prior
    // run's capture dropped before it leaks into the new run's replay (Bug 2 source fix).
    private long _replayRunId;

    // Server clock at the last scene change (ClientState.SceneChanged -> OnSceneChanged). Feeds the
    // post-transition settle gate in TickReplayCapture. 0 = no scene change seen yet (boot).
    private long _lastSceneChangeMs;

    // Is the CURRENT scene instanced-candidate content (dungeon approach / raid lobby)?
    // Drives provisional capture (ReplayCaptureGate.ShouldCapture) before the run-id
    // latches. Resolved on scene change from the game scene table's SceneKind; false when
    // the scene name doesn't parse or the table row is missing (safe default: Off).
    private bool _sceneIsCandidate;

    // Resolves whether a scene (IClientState.CurrentSceneName — the scene-table id as a
    // string, same parse CombatLogAssembler uses for MapId) is instanced-candidate content.
    private bool ResolveSceneCandidate(string? sceneName)
    {
        if (!int.TryParse(sceneName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sceneId))
            return false;
        var info = _services.GameData.World.GetScene(sceneId);
        return info.HasValue && ReplayCaptureGate.IsCandidateScene(info.Value.SceneKind);
    }

    // One-shot latch for LogReplayTrackCapHit — set the first time this encounter's ReplayCapture
    // reports TrackCapHit, so the diagnostics line fires once instead of every frame. Reset alongside
    // the rest of the capture state in ResetReplay().
    private bool _trackCapLogged;

    // ~60 s throttle for the periodic LogReplayTrackCount field artifact (Task 4 gate).
    private const float TrackDiagIntervalS = 60f;
    private float _trackDiagAccumS;

    // Boss HP timeline — sampled in parallel with the position capture cadence.
    private EntityId   _bossEntityId;          // set when boss is identified at assembly time; zero = none
    private MonsterInfo? _bossMonsterInfo;     // snapshotted at capture (caches live); used at archive (caches wiped)
    private HpTimelineSampler? _hpSampler;   // boss + players; created in InitReplay

    // One-shot latch: has TickHpTimelines already stamped the boss's final 0% death sample
    // (see HpTimelineSampler.MarkDead)? Reset alongside the rest of the capture state in ResetReplay().
    private bool _bossDeathMarked;

    // Delta-window upload watermark (owner design 2026-07-19): capture-relative ms through which the
    // replay has already been uploaded. Each banked archive serializes only (watermark, now] and — on
    // a successful upload hand-off — advances the watermark to that archive's cut and frees the
    // consumed samples. The recorder NEVER stops or resets mid-run; the buffers reset only at true run
    // end (scene-leave / run-id change) via ResetReplay. Started below zero so window 1 carries the
    // walk-in lead (position sample ms=0). A suppressed / empty / failed-hand-off window leaves the
    // watermark unchanged, so its samples merge into the next window (at-least-once, owner default 2).
    private const long ReplayWatermarkUnset = -1;
    private long _replayWatermarkMs = ReplayWatermarkUnset;
    // Upper bound of the window PrepareReplayDoc just serialized; consumed by AdvanceReplayWatermark
    // once the caller confirms the upload was handed off. Only meaningful between those two calls.
    private long _replayWindowUpperMs;

    // Player sub-profession (spec), snapshotted DURING capture — spec is cast/loadout-inferred
    // from live caches that ResetEntities() wipes before archive, so resolving it at upload
    // time always yielded 0 (same timing bug as the boss name). Sticky: first non-zero wins.
    private readonly Dictionary<long, int> _replaySpecs = new();

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    private void InitReplay()
    {
        _replay = new ReplayCapture(
            tryGet: SafeTryGetTransform,
            maxSamplesPerTrack:  ReplayMaxSamplesPerTrack,
            maxTotalSamples:     ReplayMaxTotalSamples,
            sampleIntervalMs:    ReplaySampleIntervalMs);
        _hpSampler = new HpTimelineSampler(ReadHpPair);
    }

    // The replay position probe reads a LIVE IL2CPP entity model (ZModel.GetAttrGoPosition via
    // reflection). If that model is freed — an entity torn down during a scene/line teardown, or a
    // party member who walked out of AOI on a wide map — the reflected call is an UNCATCHABLE native
    // access violation (a managed try/catch does NOT stop a c0000005). That is the crash-on-line-
    // switch / crash-on-dungeon-enter fault. Two pure gates make the probe safe.

    /// <summary>Liveness gate: probe SELF (the local player, never freed while in-world), a PARTY
    /// member (present with you from dungeon-enter — must be probed so the pre-combat walk-in is
    /// captured, and NOT gated on combat vitals which only arrive on the first hit), or any other
    /// entity the framework currently confirms AOI-present (<paramref name="aoiKnown"/>). A
    /// not-yet-engaged / despawned non-party mob is skipped. An out-of-AOI party member stays safe
    /// even though probed: the game's <c>GetEntity</c> returns null for culled ids, so the probe
    /// yields a clean GAP, never a deref of a freed model. Pure, so it is unit-tested.</summary>
    internal static bool ShouldProbeTransform(EntityId id, EntityId self, bool isPartyMember, bool aoiKnown)
        => id.Value == self.Value || isPartyMember || aoiKnown;

    /// <summary>Settle gate: skip the whole sample pass for <see cref="ReplaySettleMs"/> after a
    /// scene change, covering the mass teardown/rebuild window where AOI bookkeeping lags the native
    /// free. 0 lastSceneChangeMs = boot (not settling); a backwards clock does not wedge it.</summary>
    internal static bool IsWithinReplaySettle(long nowMs, long lastSceneChangeMs)
        => lastSceneChangeMs != 0 && nowMs >= lastSceneChangeMs && nowMs - lastSceneChangeMs < ReplaySettleMs;

    // Delegate handed to ReplayCapture — gates the live-transform probe on entity liveness so it can
    // never dereference a freed IL2CPP model. GetVitals is a managed cache read (no IL2CPP), so the
    // gate itself is always safe to evaluate even mid-teardown.
    private bool SafeTryGetTransform(EntityId id, out Position3D position, out float yaw)
    {
        position = Position3D.Zero;
        yaw = 0f;
        if (!ShouldProbeTransform(id, _services.CombatSnapshot.LocalEntityId,
                IsRosterMember(id), _services.CombatLookup.GetVitals(id).IsKnown))
            return false;
        return _services.EntityTransforms.TryGetTransform(id, out position, out yaw);
    }

    // Is this id the local player's current party roster? Cheap linear scan (roster is <= 20). Used
    // by the probe liveness gate so party members are captured during the pre-combat walk-in.
    private bool IsRosterMember(EntityId id)
    {
        foreach (var m in _services.PartyRoster.Members)
            if (m.EntityId.Value == id.Value) return true;
        return false;
    }

    // Called every frame from OnUpdate. Gate: ANY replay cell not `off` + dungeon/raid run. Deliberately
    // NOT gated on _combatActive — the run-id latch (IsInstancedRun) fires on dungeon ENTER, well
    // before the first pull, so the replay's walk-in from the dungeon entrance to the first pack is
    // captured too (previously the track started at the first damage event, mid-dungeon). The DPS
    // combat clock (_combatActive/_combatStartMs) is untouched by this — see PrepareReplayDoc's
    // msOffset rebase, which keeps the uploaded track's zero point at combat start regardless of how
    // early sampling actually began.
    // Server clock at this method's previous invocation — a gap >= TickGapRearmMs means the
    // framework tick was gated off (loading screen / world connect); see the re-arm below.
    private long _lastReplayTickMs;

    private void TickReplayCapture(float deltaTimeSec)
    {
        // ONE cached bool, no kind resolution and no prefs access on this per-frame path. It is true
        // whenever ANY kind's replay cell is not `off` — kind-INDEPENDENT on purpose, because the live
        // kind and the archived entry's kind can differ and an unsampled walk-in is unrecoverable
        // (P0 start clip). See AnyReplayCellEnabled / RecomputeUploadPolicyCache.
        if (_replay is null || !_replayCaptureEnabled) return;

        // Loading-screen hardening (2026-07-19 silent-crash follow-up): the settle gate arms on
        // the SceneChanged EVENT (load START); a load longer than ReplaySettleMs left the first
        // resumed frames unguarded. A large gap between our own ticks IS the load — re-arm the
        // settle from the resume moment so probing never starts on freshly-streaming entities.
        var serverNowMs = _services.CombatSnapshot.ServerNowMs;
        if (ReplayCaptureGate.ShouldRearmSettleAfterTickGap(serverNowMs, _lastReplayTickMs))
            _lastSceneChangeMs = serverNowMs;
        _lastReplayTickMs = serverNowMs;

        var runId = _services.Dungeon.CurrentRunId;
        if (runId != _replayRunId)
        {
            // 0 -> snowflake ADOPTS the provisional walk-in/lobby buffer (no reset);
            // snowflake -> different-snowflake still wipes (Bug-2 crash-re-enter fix);
            // snowflake -> 0 keeps the buffer for the dungeon->town archive window.
            if (ReplayCaptureGate.ShouldResetOnRunIdChange(_replayRunId, runId)) ResetReplay();
            _replayRunId = runId;
        }

        _replay.Active = ReplayCaptureGate.ShouldCapture(runId, _sceneIsCandidate);
        if (!_replay.Active) return;
        // Register local + party entities up front so their pre-combat movement (before any of them
        // has dealt/taken damage) is sampled too — NoteReplayEntity below only fires from combat
        // events, which by definition haven't happened yet during the walk-in. NoteEntity is a cheap
        // idempotent dict-contains check, safe to call every tick (covers late-joining members too).
        NoteRosterEntities();
        var nowMs = (int)_services.CombatSnapshot.ServerNowMs;
        var dtMs  = deltaTimeSec * 1000f;
        // Post-scene-change settle gate — skip probing while the mass entity teardown/rebuild after a
        // line-switch / dungeon-enter is still in flight (see SafeTryGetTransform's crash rationale).
        // NoteRosterEntities above already registered the tracks, so sampling resumes cleanly after.
        if (IsWithinReplaySettle(_services.CombatSnapshot.ServerNowMs, _lastSceneChangeMs)) return;
        _replay.Tick(nowMs, dtMs);
        if (_replay.TrackCapHit && !_trackCapLogged) { _trackCapLogged = true; LogReplayTrackCapHit(); }
        TickHpTimelines(nowMs, dtMs);
    }

    // Called every frame from OnUpdate (throttled internally to ~60s). Emits the field-observable
    // track-count line regardless of _replay.Active — in the open world this must read tracks=0,
    // which is itself the proof that NoteEntity is correctly gated to instanced runs only.
    private void TickReplayDiagnostics(float deltaTimeSec)
    {
        _trackDiagAccumS += deltaTimeSec;
        if (_trackDiagAccumS < TrackDiagIntervalS) return;
        _trackDiagAccumS = 0f;
        LogReplayTrackCount();
    }

    // Seeds the replay's tracked-entity set with the local player + current party roster, so
    // TickReplayCapture starts sampling their positions from dungeon-enter rather than waiting for
    // the first combat event to register them (see TickReplayCapture).
    private void NoteRosterEntities()
    {
        if (_replay is null) return;
        _replay.NoteEntity(_services.CombatSnapshot.LocalEntityId);
        foreach (var m in _services.PartyRoster.Members) _replay.NoteEntity(m.EntityId);
    }

    // Called from OnCombatEvent BEFORE the player-only early-out so boss/add targets enter the set.
    // Gated on IsInstancedRun(): replay only ever records inside dungeon/raid runs (TickReplayCapture
    // line ~102 sets Active from the same predicate), but this method used to allocate a ~72 KB
    // PositionTrack per distinct mob id in the OPEN WORLD too — where no reset path (scene change /
    // archive) ever fires during long farming sessions. That was the primary GC-pressure FPS leak.
    private void NoteReplayEntity(EntityId src, EntityId tgt)
    {
        if (_replay is null || !IsInstancedRun()) return;
        _replay.NoteEntity(src);
        _replay.NoteEntity(tgt);
        // Snapshot monster info NOW, while the AOI caches are live — BuildReplayMeta runs at
        // archive time when the caches are already wiped, which left every non-boss mob as
        // "Mob#uid" on the web replay (only the boss had a capture-time snapshot).
        SnapshotReplayMonster(src);
        SnapshotReplayMonster(tgt);
    }

    private readonly Dictionary<EntityId, MonsterInfo?> _replayMonsterInfo = new();

    private void SnapshotReplayMonster(EntityId id)
    {
        if (id.IsPlayer || _replayMonsterInfo.ContainsKey(id)) return;
        _replayMonsterInfo[id] = _services.GameData.World.GetMonsterByEntity(id);
    }

    private bool IsInstancedRun() => _services.Dungeon.CurrentRunId != 0;

    // Full reset — fired ONLY at true run end (scene-leave out of the run / run-id change), never at
    // archive time anymore (delta-window model): the recorder accumulates across the whole run and each
    // banked archive uploads a window + advances the watermark instead. See _replayWatermarkMs.
    private void ResetReplay()
    {
        _replay?.Reset();
        _bossEntityId    = default;
        _bossMonsterInfo = null;
        _replayMonsterInfo.Clear();
        _hpSampler?.Reset();
        _replaySpecs.Clear();
        _trackCapLogged = false;
        _bossDeathMarked = false;
        _replayWatermarkMs  = ReplayWatermarkUnset;
        _replayWindowUpperMs = 0;
    }

    // -----------------------------------------------------------------------
    // Boss + player HP timeline sampling (parallel cadence with ReplayCapture)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Registers the boss (lazily, once identified) and every player track with the
    /// HP sampler, then advances it. All timelines share the 500 ms cadence.
    ///
    /// Multi-boss (2026-08-12 review, Task 3 of the multi-boss plan): the block below ALSO
    /// Tracks/MarkDeads every member of <see cref="_stageBosses"/> — not just the single
    /// highest-MaxHp entity <see cref="ResolveBossEntity"/> lazily resolves. The scalar
    /// <see cref="_bossEntityId"/>/<see cref="_bossMonsterInfo"/> path is left running
    /// unchanged on purpose: <c>ResolveWindowBossFields</c>/<c>WindowMetaIds</c>/
    /// <c>ResolveBossUploadFields</c> (Plugin.ReplayWindow.cs / this file) and the DPS-log
    /// <c>_bossMonsterInfo?.Id</c> read (Plugin.LogUpload.cs) still consume it — Task 4/6 of the
    /// multi-boss plan rewire those to the set and retire the scalar. Until then both mechanisms
    /// coexist; <c>Track</c> is idempotent so re-tracking the same entity from both blocks is a
    /// harmless no-op.
    /// </summary>
    private void TickHpTimelines(int nowMs, float dtMs)
    {
        if (_hpSampler is null || _replay is null) return;
        // HP timelines stay committed-only: pre-combat vitals are unknown/absent (no boss
        // exists yet), and the provisional walk-in needs positions only.
        if (!IsInstancedRun()) return;

        // Lazy boss identification — unchanged semantics from the boss-HP feature.
        if (_bossEntityId.Value == 0 && _replay.Tracks.Count > 0)
        {
            _bossEntityId = ResolveBossEntity();
            if (_bossEntityId.Value != 0)
                _hpSampler.Track(_bossEntityId.Value, nowMs - _replay.CombatStartMs);
        }

        // Players join the sampler as their tracks appear (Track is idempotent).
        // Spec is snapshotted here too, while the inference caches are live.
        foreach (var id in _replay.Tracks.Keys)
        {
            if (!id.IsPlayer) continue;
            _hpSampler.Track(id.Value, nowMs - _replay.CombatStartMs);
            if (!_replaySpecs.TryGetValue(id.Value, out var spec) || spec == 0)
            {
                var sub = _services.CombatSpec.GetSubProfession(id);
                if (sub != 0) _replaySpecs[id.Value] = sub;
            }
        }

        // When the boss's vitals read dead (hp <= 0 while it was a known boss), stamp a final 0%
        // on its HP track so the replay shows the kill (see HpTimelineSampler.MarkDead). One-shot.
        if (!_bossDeathMarked && _bossEntityId.Value != 0)
        {
            var bv = _services.CombatLookup.GetVitals(_bossEntityId);
            if (bv.MaxHp > 0 && bv.Hp <= 0)
            {
                _hpSampler.MarkDead(_bossEntityId.Value, nowMs - _replay.CombatStartMs);
                _bossDeathMarked = true;
            }
        }

        // Multi-boss (Task 3): every boss the stage set knows gets its OWN HP track. MarkDead is driven
        // by the set's STICKY `killed` flag (amendment 3), never a raw Hp<=0 read here: a scripted-killed
        // raid co-boss never reads 0, it just vanishes, so gating on Hp<=0 would leave that boss's HP
        // line never terminating (the boss-hp-never-reaches-zero class; 0%-on-death precedent d1c8fbb).
        // Important 2 fix (final review): extracted to Plugin.BossDetection.cs's TickStageBossHpTracks,
        // which falls back to the sticky latch once the live set has drained same-tick-as-death — this
        // loop used to read ONLY the live set, so the last member's death (which drains the set in the
        // SAME BossStatus call) never got its terminal MarkDead stamp.
        TickStageBossHpTracks(_hpSampler, _replay.CombatStartMs, nowMs);
        TickEliteHpTracks(_hpSampler, _replay.CombatStartMs, nowMs);   // ELITE CAPTURE channel — CAPTURE ONLY, see Plugin.EliteDetection.cs

        _hpSampler.Tick(dtMs);
    }

    /// <summary>
    /// Resolves the boss entity from the current captured track set.
    /// Also snapshots <see cref="_bossMonsterInfo"/> from the live caches — caches will be
    /// wiped before archive fires, so we must capture here while they are still populated.
    /// Returns <c>default</c> (zero) when no boss entity is found.
    /// </summary>
    private EntityId ResolveBossEntity()
    {
        var candidates = new List<(long id, bool isBoss, long maxHp)>(_replay!.Tracks.Count);
        foreach (var id in _replay.Tracks.Keys)
        {
            if (id.IsPlayer) continue;
            var info   = _services.GameData.World.GetMonsterByEntity(id);
            var isBoss = info.HasValue && info.Value.IsBoss;
            var maxHp  = _services.CombatLookup.GetVitals(id).MaxHp;
            candidates.Add((id.Value, isBoss, maxHp));
        }
        var bossId = BossPicker.Pick(candidates);
        if (!bossId.HasValue) return default;

        var bossEntityId = new EntityId(bossId.Value);

        // Snapshot MonsterInfo NOW — caches are live at capture time but will be wiped by
        // ResetEntities() before PrepareReplayDoc/archive fires (scene-change sequence).
        _bossMonsterInfo = _services.GameData.World.GetMonsterByEntity(bossEntityId);

        return bossEntityId;
    }

    // -----------------------------------------------------------------------
    // Archive upload
    // -----------------------------------------------------------------------

    /// <summary>Spec § 2.4: the archive-time replay upload runs only when this run's content has its
    /// replay cell on <c>auto</c>. A <c>manual</c> or <c>off</c> cell prepares no doc, so
    /// <c>FinalizeAndMaybeUploadReplay</c> hands nothing off and the watermark HOLDS — the samples
    /// merge into the next window (D2; advancing it here would clip the run's replay, a P0 defect).
    /// The one refusal line (spec § 2.4) is logged HERE, not in <see cref="PrepareReplayDoc"/> whose
    /// body already sits at its 50-line ceiling. Decision logic is the pure static overload.</summary>
    internal bool ReplayAutoUploadAllowed(EncounterHistoryEntry entry)
    {
        if (ReplayAutoUploadAllowed(_contentKinds, _uploadPolicy, entry)) return true;
        var kind = ResolveKind(entry);
        LogUploadRefusal(kind, UploadArtifact.Replay, UploadTrigger.Auto, UploadPolicyFor(kind, UploadArtifact.Replay));
        return false;
    }

    /// <summary>
    /// Delta-window serializer (owner design 2026-07-19): assembles + signs the replay doc for the
    /// window <c>(watermark, now]</c> — the samples captured since the last uploaded window — linked to
    /// <paramref name="entry"/>'s damage segment. Does NOT reset or advance anything: the recorder
    /// keeps accumulating, and the watermark advances only once the caller confirms the upload was
    /// handed off (see <see cref="AdvanceReplayWatermark"/> / <c>FinalizeAndMaybeUploadReplay</c>).
    /// Returns <c>null</c> — leaving the watermark and buffers untouched — when this run's replay cell
    /// is not <c>auto</c>, there is no capture, the run has no level id, or the window is EMPTY (no
    /// samples since the watermark, e.g. a suppressed-junk or between-pull archive). Never throws.
    /// Task 7: the boss cut caps the upper to (firstBossHit − keepBefore) so the run-up moves next.</summary>
    internal PositionUploadDoc? PrepareReplayDoc(EncounterHistoryEntry entry, long replayUpperCapServerMs = ReplayUpperCapUnset)
    {
        try
        {
            if (_replay is null) return null;                        // Clear-decouple: NEVER reset here
            // The replay POLICY is deliberately NOT consulted here any more. Owner ruling 2026-07-29:
            // "it suppose to store all replay even flag mark off" — `off` withholds the SEND, never the
            // record. The doc is always serialized so it reaches the retained container (PersistReUpload /
            // RetainWithoutUpload both carry it), and FinalizeAndMaybeUploadReplay decides send vs store.
            // Open field is still excluded, structurally: a field fight has no run id.
            if (entry.LevelUuid == 0) return null;

            // upperMs = capture-relative "now" (int32-since-enter; see _replayWatermarkMs); a boss-cut cap (same truncation) moves it earlier.
            var upperMs = (long)((int)_services.CombatSnapshot.ServerNowMs - _replay.CombatStartMs);
            if (replayUpperCapServerMs != ReplayUpperCapUnset) upperMs = ReplayWindow.CapUpper(upperMs, (int)replayUpperCapServerMs - _replay.CombatStartMs, _replayWatermarkMs);
            var windowTracks = SliceWindowPositions(_replayWatermarkMs, upperMs);
            if (windowTracks.Count == 0) return null;                // empty window → no upload, watermark unchanged
            _replayWindowUpperMs = upperMs;
            var localUid  = _services.CombatSnapshot.LocalEntityId.Value;
            var encounter = CombatLogAssembler.BuildEncounter(entry);
            // Per-doc contract unchanged: rebase Ms0 onto THIS segment's combat start (capture zero is run-constant).
            var msOffset = _replay.CombatStartMs - (int)encounter.StartMs;

            var boss = ResolveWindowBossFields(windowTracks, upperMs, msOffset);
            // Multi-boss (Task 4): every stage-set boss, windowed — feeds Bosses[] + the meta-id union.
            var windowBosses = BuildWindowBossMembers(windowTracks, upperMs, msOffset);
            var windowElites = BuildWindowEliteMembers(windowTracks, upperMs, msOffset);   // ELITE CAPTURE channel — feeds Elites[] only, see PositionUploadDoc.Elites' doc

            var doc = PositionTrackAssembler.Assemble(
                samplesByEntity: windowTracks,
                hz:     2,
                mapId:  encounter.MapId,
                origin: (0f, 0f),
                scale:  0.1f,
                msOffset: msOffset,
                meta:   BuildReplayMeta(
                    WindowMetaIds(windowTracks, boss.id, boss.inWindow, windowBosses), boss.idStr, boss.info));

            doc = doc with
            {
                LogId        = GenerateReplayLogId(),
                LevelUuid    = entry.LevelUuid,
                LocalUid     = localUid,
                StartMs      = encounter.StartMs,
                EndMs        = encounter.EndMs,
                Nonce        = GenerateReplayNonce(),
                BossEntityId = boss.idStr,
                BossHp       = boss.hp,
                PlayerHp     = RebasePlayerHpTracks(SlicePlayerHpWindow(upperMs), msOffset),
                Bosses       = ToBossTrackDtos(windowBosses),
                Elites       = ToBossTrackDtos(windowElites),
            };
            return doc with { Sig = SignReplay(doc) };
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[CombatMeter.Replay] threw: {ex.Message}");
            return null;   // no reset — samples survive to the next window (watermark unchanged)
        }
    }

    /// <summary>
    /// Fires the fire-and-forget positions upload for an already-assembled <paramref name="doc"/>
    /// (see <see cref="PrepareReplayDoc"/>). Never throws. Returns <c>true</c> when the doc was handed
    /// off to the upload queue (the synchronous dispatch did not throw) — the signal that gates the
    /// watermark advance (owner default 2); <c>false</c> only when the dispatch itself threw, in which
    /// case the caller keeps the watermark so the window re-uploads next time. Region comes straight
    /// from <see cref="Stellar.Abstractions.Services.IGameEnvironment"/> — <c>PositionUploadDoc</c>
    /// carries no region of its own, and this call site has no <c>CombatLog</c> in scope (both of
    /// <c>UploadReplayDoc</c>'s callers — the summary-upload callback legs in Plugin.LogUpload.cs and
    /// the no-summary-fired path in Plugin.History.cs — only have <c>replayDoc</c>, not the log).
    /// </summary>
    internal bool UploadReplayDoc(PositionUploadDoc doc)
    {
        try
        {
            PositionUploader.UploadFireAndForget(_services.GameEnvironment.RegionCode, doc, (ok, status, err) =>
            {
                if (ok) _services.Log.Info(
                    $"[CombatMeter.Replay] positions OK (HTTP {status}) levelUuid={doc.LevelUuid}");
                else    _services.Log.Warning(
                    $"[CombatMeter.Replay] positions FAILED (HTTP {status}): {err}");
            });
            return true;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[CombatMeter.Replay] upload threw: {ex.Message}");
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Boss upload-field assembly helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the boss entity id string and the capture-time MonsterInfo snapshot.
    /// Uses <see cref="_bossMonsterInfo"/> (snapshotted in <see cref="ResolveBossEntity"/>
    /// while caches were live) — <b>not</b> a fresh <c>GetMonsterByEntity</c> call, because
    /// <c>ResetEntities()</c> wipes the attr/vitals caches before archive fires.
    /// Returns (empty, null) when no boss was identified during capture. Multi-boss (Task 4): now the
    /// LEGACY fallback only — <c>ResolveBossRepresentative</c> (Plugin.ReplayWindow.cs) calls this when
    /// <see cref="_stageBosses"/> is empty; a non-empty set resolves its representative from itself.
    /// </summary>
    private (string bossEntityIdStr, MonsterInfo? info) ResolveBossUploadFields()
    {
        if (_bossEntityId.Value == 0) return ("", null);

        var bossIdStr = _bossEntityId.Value.ToString(CultureInfo.InvariantCulture);
        return (bossIdStr, _bossMonsterInfo);
    }

    /// <summary>
    /// Per-member HP-track builder (multi-boss plan Task 3): every current stage-set boss's id,
    /// monster config id, and sampled HP track (null if the sampler has no samples for it yet).
    /// Consumed by <c>BuildWindowBossMembers</c> (Task 4, Plugin.ReplayWindow.cs), which slices+rebases
    /// each member's track to the upload window and feeds both the additive
    /// <see cref="Replay.PositionUploadDoc.Bosses"/> array and the meta-id union. Critical 1 fix (final
    /// review): reads <c>ResolveCurrentStageBosses()</c> (Plugin.BossDetection.cs) — the live set, or the
    /// sticky latch when the live set already drained/reset before this ran — instead of the live set
    /// directly, so a window built after the boss died/the run boundary fired still carries it. This
    /// allocates a list of size Count either way, which is fine here (archive/window-assembly time,
    /// never per-tick).
    /// </summary>
    private IReadOnlyList<(EntityId id, int configId, HpTrack? track)> BuildBossHpTracks()
    {
        var members = ResolveCurrentStageBosses();
        var list = new List<(EntityId, int, HpTrack?)>(members.Count);
        for (var i = 0; i < members.Count; i++)
        {
            var (id, configId, _) = members[i];
            list.Add((id, configId, _hpSampler?.GetTrack(id.Value)));
        }
        return list;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    // Meta covers the entities PRESENT IN THIS WINDOW (ids = the windowed track keys, plus any boss
    // carried via WindowMetaIds's HP-only union — Task 4 extends that union to every stage boss, not
    // just the representative), per the delta-window design — a boss present across windows is
    // described in each; the site's first-write-wins name capture + per-segment bossId mapping already
    // handle the repetition. A non-representative boss's MonsterInfo still resolves correctly below via
    // the plain _replayMonsterInfo branch (kind reads "add" for it — Bosses[] is the multi-boss carrier).
    private Dictionary<EntityId, PositionMetaDto> BuildReplayMeta(
        ICollection<EntityId> ids, string bossEntityIdStr, MonsterInfo? bossMonsterInfo)
    {
        var meta = new Dictionary<EntityId, PositionMetaDto>(ids.Count);
        foreach (var id in ids)
        {
            MonsterInfo? monsterInfo;
            if (!id.IsPlayer && !string.IsNullOrEmpty(bossEntityIdStr) &&
                id.Value.ToString(CultureInfo.InvariantCulture) == bossEntityIdStr)
            {
                // Use the capture-time snapshot for the boss — caches wiped at archive time.
                monsterInfo = bossMonsterInfo;
            }
            else if (id.IsPlayer)
            {
                monsterInfo = null;
            }
            else
            {
                // Capture-time snapshot first (live lookup returns null post-wipe at archive).
                if (!_replayMonsterInfo.TryGetValue(id, out monsterInfo))
                    monsterInfo = _services.GameData.World.GetMonsterByEntity(id);
            }
            var kind = ReplayKindFor(id, bossEntityIdStr);
            var name = ReplayNameFor(id, monsterInfo);
            meta[id] = new PositionMetaDto(kind, name ?? "", ReplayProfessionFor(id));
        }
        return meta;
    }

    private string SignReplay(PositionUploadDoc doc)
    {
        var key = _prefs.Get(PrefSignerKey, "");
        if (string.IsNullOrWhiteSpace(key)) return "";
        try
        {
            using var s = new LogSigner(key);
            return s.Sign(PositionCanonicalPayload.Build(doc));
        }
        catch (Exception ex) { return $"sig-error:{ex.Message}"; }
    }

    private string ReplayKindFor(EntityId id, string bossEntityIdStr)
    {
        if (id == _services.CombatSnapshot.LocalEntityId || id.IsPlayer) return "player";
        if (!string.IsNullOrEmpty(bossEntityIdStr) &&
            id.Value.ToString(CultureInfo.InvariantCulture) == bossEntityIdStr) return "boss";
        return "add";
    }

    private string? ReplayNameFor(EntityId id, MonsterInfo? monsterInfo)
        => EntityLabel.Resolve(
            id,
            _services.CombatSnapshot.LocalEntityId,
            _services.PlayerState,
            _services.CombatLookup,
            _services.PartyRoster.Members,
            monsterInfo);

    // Read from the capture-time snapshot — live inference caches are already wiped at archive.
    private int ReplayProfessionFor(EntityId id)
        => id.IsPlayer && _replaySpecs.TryGetValue(id.Value, out var spec) ? spec : 0;

    private static string GenerateReplayLogId()
    {
        var r = new byte[4];
        RandomNumberGenerator.Fill(r);
        return $"pos-{DateTime.UtcNow:yyyyMMddHHmmss}-{BitConverter.ToString(r).Replace("-", "").ToLowerInvariant()}";
    }

    private static string GenerateReplayNonce()
    {
        var r = new byte[12];
        RandomNumberGenerator.Fill(r);
        return Convert.ToBase64String(r);
    }
}
