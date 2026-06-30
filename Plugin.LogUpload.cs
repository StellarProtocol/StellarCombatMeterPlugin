// UNVERIFIED — this code has never been executed in-game.
// SP1: Full combat-event capture + StellarLogs upload integration.
//
// Feature boundary:
//   - Capture: OnCombatEvent feeds _logBuffer during an encounter.
//   - Serialize: ManualArchive triggers SerializeAndUpload() after the encounter is archived.
//   - Upload: opt-in (EnableLogUpload setting); fire-and-forget; never blocks or crashes the game.
//
// Wiring stubs clearly marked TODO(SP1) for items that require game-API access not yet in the framework.

using System;
using Stellar.CombatMeter.LogUpload;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    // -----------------------------------------------------------------------
    // SP1 fields (all private to this file; other partials are unaffected)
    // -----------------------------------------------------------------------

    private readonly CombatEventBuffer _logBuffer = new();
    private CombatLogAssembler? _logAssembler;

    // -----------------------------------------------------------------------
    // Settings keys (read/written from the "combatmeter" config section)
    // -----------------------------------------------------------------------

    private const string PrefEnableUpload = "logUpload.enabled";
    private const string PrefSignerKey    = "logUpload.signerKey";

    // -----------------------------------------------------------------------
    // Lazy initialisation (assembler is created once on first use)
    // -----------------------------------------------------------------------

    private CombatLogAssembler LogAssembler
        => _logAssembler ??= new CombatLogAssembler(_services);

    // -----------------------------------------------------------------------
    // Settings accessors (expose to Plugin.Settings.cs if a UI toggle is added)
    // -----------------------------------------------------------------------

    internal bool EnableLogUpload
    {
        get => _prefs.Get(PrefEnableUpload, false);
        set { _prefs.Set(PrefEnableUpload, value); _prefs.Save(); }
    }

    /// <summary>
    /// Base64-PKCS#8 ECDSA P-256 private key used to sign uploads.
    /// MUST come from config / env — never hardcode a real secret here.
    /// If empty or absent the upload is sent unsigned (server will reject if UPLOAD_PUBKEY is set).
    /// TODO(SP1): plumb through a secure key-provisioning flow (e.g. env-var injected by launcher).
    /// </summary>
    private string? SignerKey => _prefs.Get(PrefSignerKey, "");

    // -----------------------------------------------------------------------
    // Capture: called from OnCombatEvent (Plugin.Capture.cs) for every event
    // -----------------------------------------------------------------------

    /// <summary>
    /// Feeds the raw event into the log buffer when upload is enabled.
    /// Called on every combat event BEFORE the existing processing path; zero-allocation
    /// when disabled (the buffer add is an O(1) list append).
    /// </summary>
    internal void MaybeCaptureForLog(CombatEvent evt)
    {
        if (!EnableLogUpload) return;
        _logBuffer.Add(evt);
    }

    // -----------------------------------------------------------------------
    // Serialize + upload: called from ManualArchive (Plugin.History.cs) after
    // the encounter entry has been added to history.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Serializes the captured event stream + actor snapshots into the StellarLogs DTO
    /// and fires off a fire-and-forget upload. Called once per archive; never throws.
    /// </summary>
    internal void MaybeUploadLog(EncounterHistoryEntry entry)
    {
        if (!EnableLogUpload)
        {
            _logBuffer.Clear();
            return;
        }

        try
        {
            var events = _logBuffer.Flush();   // also clears the buffer
            if (events.Count == 0)
            {
                _services.Log.Info("[CombatMeter.SP1] No events captured — skipping upload.");
                return;
            }

            var log = LogAssembler.Assemble(entry, events, SignerKey);
            _services.Log.Info(
                $"[CombatMeter.SP1] Uploading log {log.Header.LogId} levelUuid={log.Header.Encounter.LevelUuid} " +
                $"({events.Count} events, {entry.Entities.Count} actors).");

            LogUploader.UploadFireAndForget(log, (ok, status, err) =>
            {
                // Callback fires on a thread-pool thread; only call thread-safe log methods here.
                if (ok)
                    _services.Log.Info($"[CombatMeter.SP1] Upload OK (HTTP {status}): {log.Header.LogId}");
                else
                    _services.Log.Warning($"[CombatMeter.SP1] Upload FAILED (HTTP {status}): {err}");
            });
        }
        catch (Exception ex)
        {
            // Any unhandled exception here must NOT propagate into ManualArchive (which runs on the
            // main thread and cannot crash without destabilising the game session).
            _logBuffer.Clear();
            _services.Log.Warning($"[CombatMeter.SP1] Log assembly/upload threw: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Dispose: clear the buffer
    // -----------------------------------------------------------------------

    private void DisposeLogUpload()
    {
        _logBuffer.Clear();
    }
}
