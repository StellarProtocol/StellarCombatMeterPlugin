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
    private EntityId  _bossEntityId;          // set when boss is identified at assembly time; zero = none
    private float     _bossHpAccumMs;         // mirrors ReplayCapture's accumulator for same-cadence sampling
    private int       _bossHpMs0;
    private readonly List<int> _bossHpPct = new();   // pct per sample: round(100*hp/maxHp), clamped 0..100

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
    }

    // Called every frame from OnUpdate. Gate: toggle on + dungeon/raid + combat active.
    private void TickReplayCapture(float deltaTimeSec)
    {
        if (_replay is null || !_uploadReplay) return;
        _replay.Active = _combatActive && IsInstancedRun();
        if (!_replay.Active) return;
        var nowMs = (int)_services.CombatSnapshot.ServerNowMs;
        var dtMs  = deltaTimeSec * 1000f;
        _replay.Tick(nowMs, dtMs);
        TickBossHp(nowMs, dtMs);
    }

    // Called from OnCombatEvent BEFORE the player-only early-out so boss/add targets enter the set.
    private void NoteReplayEntity(EntityId src, EntityId tgt)
    {
        if (_replay is null) return;
        _replay.NoteEntity(src);
        _replay.NoteEntity(tgt);
    }

    private bool IsInstancedRun() => _services.Dungeon.CurrentRunId != 0;

    private void ResetReplay()
    {
        _replay?.Reset();
        _bossEntityId  = default;
        _bossHpAccumMs = 0f;
        _bossHpMs0     = 0;
        _bossHpPct.Clear();
    }

    // -----------------------------------------------------------------------
    // Boss HP timeline sampling (parallel cadence with ReplayCapture)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Samples the boss entity's HP% once per 500 ms, aligned to the same cadence as
    /// <see cref="ReplayCapture.Tick"/> — both receive the same nowMs/dtMs per frame.
    /// Only runs when <see cref="_bossEntityId"/> is non-zero (set after first Tick once boss
    /// is identified from the captured entity set); the boss is identified lazily on first
    /// sample opportunity to allow the entity set to fill during the opening seconds.
    /// </summary>
    private void TickBossHp(int nowMs, float dtMs)
    {
        // Lazy boss identification: resolve once when the entity set is non-empty.
        if (_bossEntityId.Value == 0 && _replay is not null && _replay.Tracks.Count > 0)
        {
            _bossEntityId = ResolveBossEntity(nowMs);
        }

        if (_bossEntityId.Value == 0) return;

        _bossHpAccumMs += dtMs;
        if (_bossHpAccumMs < ReplaySampleIntervalMs) return;
        _bossHpAccumMs -= ReplaySampleIntervalMs;
        if (_bossHpAccumMs >= ReplaySampleIntervalMs) _bossHpAccumMs = 0f;

        var vitals = _services.CombatLookup.GetVitals(_bossEntityId);
        if (!vitals.IsKnown || vitals.MaxHp <= 0) return;

        var pct = (int)Math.Round(100.0 * vitals.Hp / vitals.MaxHp);
        if (pct < 0) pct = 0;
        if (pct > 100) pct = 100;
        _bossHpPct.Add(pct);
    }

    /// <summary>
    /// Resolves the boss entity from the current captured track set and stamps
    /// <see cref="_bossHpMs0"/> at the moment of identification (combat-relative ms).
    /// Returns <c>default</c> (zero) when no boss entity is found.
    /// </summary>
    private EntityId ResolveBossEntity(int nowMs)
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

        // Stamp ms0 at identification time (not at the first HP sample), aligning with
        // ReplayCapture's position-track ms0 semantics.
        _bossHpMs0 = Math.Max(0, nowMs - _replay.CombatStartMs);

        return new EntityId(bossId.Value);
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

            // Resolve boss info for meta + upload fields (snapshot before Reset).
            var (bossEntityIdStr, bossMonsterInfo) = ResolveBossUploadFields();
            EmitBossDiag();
            var bossHpTrack = BuildBossHpTrack();

            var doc = PositionTrackAssembler.Assemble(
                tracks: _replay.Tracks,
                hz:     2,
                mapId:  encounter.MapId,
                origin: (0f, 0f),
                scale:  0.1f,
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
    /// Returns the boss entity id string and its MonsterInfo using the already-resolved
    /// <see cref="_bossEntityId"/> from capture time. Returns (empty, null) when no boss was
    /// identified during capture. This ensures the HP-timeline entity and the upload
    /// <c>bossEntityId</c> always refer to the same entity.
    /// </summary>
    private (string bossEntityIdStr, MonsterInfo? info) ResolveBossUploadFields()
    {
        if (_bossEntityId.Value == 0) return ("", null);

        var bossInfo  = _services.GameData.World.GetMonsterByEntity(_bossEntityId);
        var bossIdStr = _bossEntityId.Value.ToString(CultureInfo.InvariantCulture);
        return (bossIdStr, bossInfo);
    }

    private BossHpTrack? BuildBossHpTrack()
    {
        if (_bossHpPct.Count == 0) return null;
        return new BossHpTrack(_bossHpMs0, _bossHpPct.ToArray());
    }

    /// <summary>
    /// One-shot diagnostic emitted at replay archive time when a boss entity was resolved.
    /// Logs everything needed to diagnose empty name + missing HP: attr-10 (configId path),
    /// MonsterInfo fields, live vitals, and raw HP attrs (11310/11320) from the attr-cache.
    /// Never throws — all accesses are defensive.
    /// </summary>
    private void EmitBossDiag()
    {
        if (_bossEntityId.Value == 0) return;

        try
        {
            var info    = _services.GameData.World.GetMonsterByEntity(_bossEntityId);
            var vitals  = _services.CombatLookup.GetVitals(_bossEntityId);
            var attrs   = _services.EntityDetail.GetAttributes(_bossEntityId);

            attrs.TryGetValue(10,    out var attr10);
            attrs.TryGetValue(11310, out var attr11310);
            attrs.TryGetValue(11320, out var attr11320);

            _services.Log.Info(
                $"[BossDiag] bossEntity={_bossEntityId.Value} configId={attr10} " +
                $"monsterInfo: hasValue={info.HasValue} name=\"{info?.Name}\" isBoss={info?.IsBoss} id={info?.Id} monsterType={info?.MonsterType} " +
                $"vitals: isKnown={vitals.IsKnown} hp={vitals.Hp} maxHp={vitals.MaxHp} " +
                $"attrCache: hp11310={attr11310} maxHp11320={attr11320}");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[BossDiag] threw: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private Dictionary<EntityId, PositionMetaDto> BuildReplayMeta(
        string bossEntityIdStr, MonsterInfo? bossMonsterInfo)
    {
        var meta = new Dictionary<EntityId, PositionMetaDto>(_replay!.Tracks.Count);
        foreach (var id in _replay.Tracks.Keys)
        {
            var monsterInfo = id.IsPlayer
                ? null
                : _services.GameData.World.GetMonsterByEntity(id);
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

    private int ReplayProfessionFor(EntityId id)
        => id.IsPlayer ? _services.CombatSpec.GetSubProfession(id) : 0;

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
