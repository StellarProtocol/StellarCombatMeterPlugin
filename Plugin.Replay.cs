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
    // ReplayDefaults — static seam used by unit tests (Plugin can't be headless-instantiated)
    // -----------------------------------------------------------------------

    internal static class ReplayDefaults
    {
        public const bool UploadReplayDefault = true;
    }

    // -----------------------------------------------------------------------
    // Prefs + constants
    // -----------------------------------------------------------------------

    private const string PrefUploadReplay       = "logUpload.uploadReplay";
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
    private bool _uploadReplay = ReplayDefaults.UploadReplayDefault;

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

    // Player sub-profession (spec), snapshotted DURING capture — spec is cast/loadout-inferred
    // from live caches that ResetEntities() wipes before archive, so resolving it at upload
    // time always yielded 0 (same timing bug as the boss name). Sticky: first non-zero wins.
    private readonly Dictionary<long, int> _replaySpecs = new();

    /// <summary>
    /// Capture + upload the replay position track for dungeon/raid runs.
    /// Default ON; separate from AutoUpload.
    /// </summary>
    internal bool UploadReplay
    {
        get => _uploadReplay;
        set { _uploadReplay = value; _prefs.Set(PrefUploadReplay, value); _prefs.Save(); }
    }

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    private void InitReplay()
    {
        _uploadReplay = _prefs.Get(PrefUploadReplay, ReplayDefaults.UploadReplayDefault);
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

    // Called every frame from OnUpdate. Gate: toggle on + dungeon/raid run in progress. Deliberately
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
        if (_replay is null || !_uploadReplay) return;

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

    private void ResetReplay()
    {
        _replay?.Reset();
        _bossEntityId    = default;
        _bossMonsterInfo = null;
        _replayMonsterInfo.Clear();
        _hpSampler?.Reset();
        _replaySpecs.Clear();
        _trackCapLogged = false;
    }

    // -----------------------------------------------------------------------
    // Boss + player HP timeline sampling (parallel cadence with ReplayCapture)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Registers the boss (lazily, once identified) and every player track with the
    /// HP sampler, then advances it. All timelines share the 500 ms cadence.
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

    /// <summary>
    /// At archive: assembles + signs the replay position doc for <paramref name="entry"/>, ALWAYS
    /// resetting the capture buffers before returning (regardless of outcome) — the capture-buffer
    /// reset must happen at archive time no matter what any later upload decision does. Returns
    /// <c>null</c> when replay upload is off, no capture exists, the run has no level id, or
    /// nothing was sampled. Never throws. Callers (Plugin.History.cs / Plugin.LogUpload.cs) decide
    /// separately whether to fire <see cref="UploadReplayDoc"/> for the returned doc, based on the
    /// summary upload's verdict (skip when the server already has positions for this run).
    /// </summary>
    internal PositionUploadDoc? PrepareReplayDoc(EncounterHistoryEntry entry)
    {
        try
        {
            if (!_uploadReplay || _replay is null) { ResetReplay(); return null; }
            if (entry.LevelUuid == 0 || _replay.TotalSamples == 0) { ResetReplay(); return null; }

            var localUid  = _services.CombatSnapshot.LocalEntityId.Value;
            var encounter = CombatLogAssembler.BuildEncounter(entry);

            // Sampling now starts at dungeon-enter (TickReplayCapture), not at first damage, so
            // _replay.CombatStartMs (the capture's own zero point) usually PRECEDES the DPS combat
            // clock's encounter.StartMs. Rebase every track's Ms0 by this offset so the uploaded
            // doc's zero point stays exactly at combat start (matching StartMs below) — pre-combat
            // samples land at negative ms, which the codec/viewer already tolerate (plain signed
            // ints; the site's death/imagine-cast markers are converted the same way and only ever
            // occur at/after combat start, so they stay aligned with the extended position timeline).
            var msOffset = _replay.CombatStartMs - (int)encounter.StartMs;

            // Resolve boss info for meta + upload fields using capture-time snapshot.
            var (bossEntityIdStr, bossMonsterInfo) = ResolveBossUploadFields();
            var bossHpTrack = RebaseHpTrack(BuildBossHpTrack(), msOffset);

            var doc = PositionTrackAssembler.Assemble(
                tracks: _replay.Tracks,
                hz:     2,
                mapId:  encounter.MapId,
                origin: (0f, 0f),
                scale:  0.1f,
                msOffset: msOffset,
                meta:   BuildReplayMeta(bossEntityIdStr, bossMonsterInfo));

            var nonce = GenerateReplayNonce();
            doc = doc with
            {
                LogId        = GenerateReplayLogId(),
                LevelUuid    = entry.LevelUuid,
                LocalUid     = localUid,
                StartMs      = encounter.StartMs,
                EndMs        = encounter.EndMs,
                Nonce        = nonce,
                BossEntityId = bossEntityIdStr,
                BossHp       = bossHpTrack,
                PlayerHp     = RebasePlayerHpTracks(BuildPlayerHpTracks(), msOffset),
            };
            doc = doc with { Sig = SignReplay(doc) };

            ResetReplay();

            return doc;
        }
        catch (Exception ex)
        {
            ResetReplay();
            _services.Log.Warning($"[CombatMeter.Replay] threw: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fires the fire-and-forget positions upload for an already-assembled <paramref name="doc"/>
    /// (see <see cref="PrepareReplayDoc"/>). Never throws. Region comes straight from
    /// <see cref="Stellar.Abstractions.Services.IGameEnvironment"/> — <c>PositionUploadDoc</c>
    /// carries no region of its own, and this call site has no <c>CombatLog</c> in scope
    /// (both of <c>UploadReplayDoc</c>'s callers — the summary-upload callback legs in
    /// Plugin.LogUpload.cs and the no-summary-fired path in Plugin.History.cs — only have
    /// <c>replayDoc</c>, not the assembled log).
    /// </summary>
    internal void UploadReplayDoc(PositionUploadDoc doc)
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
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[CombatMeter.Replay] upload threw: {ex.Message}");
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
    /// Returns (empty, null) when no boss was identified during capture.
    /// </summary>
    private (string bossEntityIdStr, MonsterInfo? info) ResolveBossUploadFields()
    {
        if (_bossEntityId.Value == 0) return ("", null);

        var bossIdStr = _bossEntityId.Value.ToString(CultureInfo.InvariantCulture);
        return (bossIdStr, _bossMonsterInfo);
    }

    private HpTrack? BuildBossHpTrack()
        => _bossEntityId.Value != 0 ? _hpSampler?.GetTrack(_bossEntityId.Value) : null;

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private Dictionary<EntityId, PositionMetaDto> BuildReplayMeta(
        string bossEntityIdStr, MonsterInfo? bossMonsterInfo)
    {
        var meta = new Dictionary<EntityId, PositionMetaDto>(_replay!.Tracks.Count);
        foreach (var id in _replay.Tracks.Keys)
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
