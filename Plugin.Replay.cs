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

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    private ReplayCapture? _replay;
    private bool _uploadReplay = ReplayDefaults.UploadReplayDefault;

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
            tryGet: (EntityId id, out Position3D p, out float yaw) =>
                _services.EntityTransforms.TryGetTransform(id, out p, out yaw),
            maxSamplesPerTrack:  ReplayMaxSamplesPerTrack,
            maxTotalSamples:     ReplayMaxTotalSamples,
            sampleIntervalMs:    ReplaySampleIntervalMs);
        _hpSampler = new HpTimelineSampler(ReadHpPair);
    }

    // Called every frame from OnUpdate. Gate: toggle on + dungeon/raid run in progress. Deliberately
    // NOT gated on _combatActive — the run-id latch (IsInstancedRun) fires on dungeon ENTER, well
    // before the first pull, so the replay's walk-in from the dungeon entrance to the first pack is
    // captured too (previously the track started at the first damage event, mid-dungeon). The DPS
    // combat clock (_combatActive/_combatStartMs) is untouched by this — see MaybeUploadReplay's
    // msOffset rebase, which keeps the uploaded track's zero point at combat start regardless of how
    // early sampling actually began.
    private void TickReplayCapture(float deltaTimeSec)
    {
        if (_replay is null || !_uploadReplay) return;
        _replay.Active = IsInstancedRun();
        if (!_replay.Active) return;
        // Register local + party entities up front so their pre-combat movement (before any of them
        // has dealt/taken damage) is sampled too — NoteReplayEntity below only fires from combat
        // events, which by definition haven't happened yet during the walk-in. NoteEntity is a cheap
        // idempotent dict-contains check, safe to call every tick (covers late-joining members too).
        NoteRosterEntities();
        var nowMs = (int)_services.CombatSnapshot.ServerNowMs;
        var dtMs  = deltaTimeSec * 1000f;
        _replay.Tick(nowMs, dtMs);
        TickHpTimelines(nowMs, dtMs);
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
    private void NoteReplayEntity(EntityId src, EntityId tgt)
    {
        if (_replay is null) return;
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
        // ResetEntities() before MaybeUploadReplay/archive fires (scene-change sequence).
        _bossMonsterInfo = _services.GameData.World.GetMonsterByEntity(bossEntityId);

        return bossEntityId;
    }

    // -----------------------------------------------------------------------
    // Archive upload
    // -----------------------------------------------------------------------

    // At archive: assemble + upload the track off-thread (PositionUploader uses Task.Run). Never throws.
    internal void MaybeUploadReplay(EncounterHistoryEntry entry)
    {
        try
        {
            if (!_uploadReplay || _replay is null) { ResetReplay(); return; }
            if (entry.LevelUuid == 0 || _replay.TotalSamples == 0) { ResetReplay(); return; }

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

            PositionUploader.UploadFireAndForget(doc, (ok, status, err) =>
            {
                if (ok) _services.Log.Info(
                    $"[CombatMeter.Replay] positions OK (HTTP {status}) levelUuid={doc.LevelUuid}");
                else    _services.Log.Warning(
                    $"[CombatMeter.Replay] positions FAILED (HTTP {status}): {err}");
            });
        }
        catch (Exception ex)
        {
            ResetReplay();
            _services.Log.Warning($"[CombatMeter.Replay] threw: {ex.Message}");
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
