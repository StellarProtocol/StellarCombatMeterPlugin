using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Stellar.Abstractions.Domain;
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
        _replay.Tick((int)_services.CombatSnapshot.ServerNowMs, deltaTimeSec * 1000f);
    }

    // Called from OnCombatEvent BEFORE the player-only early-out so boss/add targets enter the set.
    private void NoteReplayEntity(EntityId src, EntityId tgt)
    {
        if (_replay is null) return;
        _replay.NoteEntity(src);
        _replay.NoteEntity(tgt);
    }

    private bool IsInstancedRun() => _services.Dungeon.CurrentRunId != 0;

    private void ResetReplay() => _replay?.Reset();

    // -----------------------------------------------------------------------
    // Archive upload
    // -----------------------------------------------------------------------

    // At archive: assemble + upload the track off-thread (PositionUploader uses Task.Run). Never throws.
    internal void MaybeUploadReplay(EncounterHistoryEntry entry)
    {
        try
        {
            if (!_uploadReplay || _replay is null) { _replay?.Reset(); return; }
            if (entry.LevelUuid == 0 || _replay.TotalSamples == 0) { _replay.Reset(); return; }

            var localUid  = _services.CombatSnapshot.LocalEntityId.Value;
            var encounter = CombatLogAssembler.BuildEncounter(entry);

            var doc = PositionTrackAssembler.Assemble(
                tracks: _replay.Tracks,
                hz:     2,
                mapId:  encounter.MapId,
                origin: (0f, 0f),
                scale:  0.1f,
                meta:   BuildReplayMeta());

            var nonce = GenerateReplayNonce();
            doc = doc with
            {
                LogId     = GenerateReplayLogId(),
                LevelUuid = entry.LevelUuid,
                LocalUid  = localUid,
                StartMs   = encounter.StartMs,
                EndMs     = encounter.EndMs,
                Nonce     = nonce,
            };
            doc = doc with { Sig = SignReplay(doc) };

            _replay.Reset();

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
            _replay?.Reset();
            _services.Log.Warning($"[CombatMeter.Replay] threw: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private Dictionary<EntityId, PositionMetaDto> BuildReplayMeta()
    {
        var meta = new Dictionary<EntityId, PositionMetaDto>(_replay!.Tracks.Count);
        foreach (var id in _replay.Tracks.Keys)
            meta[id] = new PositionMetaDto(ReplayKindFor(id), ReplayNameFor(id) ?? "", ReplayProfessionFor(id));
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

    private string ReplayKindFor(EntityId id)
        => id == _services.CombatSnapshot.LocalEntityId || id.IsPlayer ? "player" : "add";

    private string? ReplayNameFor(EntityId id)
        => EntityLabel.Resolve(
            id,
            _services.CombatSnapshot.LocalEntityId,
            _services.PlayerState,
            _services.CombatLookup,
            _services.PartyRoster.Members);

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
