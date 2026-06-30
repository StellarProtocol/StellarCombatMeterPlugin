// UNVERIFIED — this code has never been executed in-game.
// SP1: Full combat-event capture + StellarLogs upload integration.
//
// Feature boundary:
//   - Capture: OnCombatEvent feeds _logBuffer during an encounter.
//   - Serialize: ManualArchive triggers SerializeAndUpload() after the encounter is archived.
//   - Upload: auto path gated on AutoUpload (default ON); fire-and-forget; never blocks or crashes the game.
//
// Wiring stubs clearly marked TODO(SP1) for items that require game-API access not yet in the framework.

using System;
using System.Collections.Generic;
using System.Globalization;
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
    // Per-entry upload status (read by the history UI on the main thread; written
    // by the fire-and-forget callback on a thread-pool thread). Backed by the
    // services-free UploadStatusTable (immutable values → no torn cross-thread reads).
    // -----------------------------------------------------------------------

    private readonly UploadStatusTable _uploadStatus = new();

    internal UploadPhase UploadStateFor(EncounterHistoryEntry e) => _uploadStatus.PhaseFor(e);

    internal string? UploadUrlFor(EncounterHistoryEntry e) => _uploadStatus.UrlFor(e);

    // -----------------------------------------------------------------------
    // Settings keys (read/written from the "combatmeter" config section)
    // -----------------------------------------------------------------------

    private const string PrefAutoUpload = "logUpload.autoUpload";
    private const string PrefSignerKey  = "logUpload.signerKey";

    // -----------------------------------------------------------------------
    // Lazy initialisation (assembler is created once on first use)
    // -----------------------------------------------------------------------

    private CombatLogAssembler LogAssembler
        => _logAssembler ??= new CombatLogAssembler(_services);

    // -----------------------------------------------------------------------
    // Settings accessors (expose to Plugin.Settings.cs if a UI toggle is added)
    // -----------------------------------------------------------------------

    // Cached copy of the auto-upload preference. Read on the per-combat-event hot path
    // (MaybeCaptureForLog), so the getter MUST stay O(1) — never a lock + JSON deserialize.
    // Loaded once at init via InitLogUpload(); the setter keeps it in sync with persisted prefs.
    private bool _autoUpload = true;

    /// <summary>Auto-upload every archived run + capture raw forensic events. Default ON; toggle in settings.
    /// Manual per-run upload from history works regardless of this flag.</summary>
    internal bool AutoUpload
    {
        get => _autoUpload;
        set { _autoUpload = value; _prefs.Set(PrefAutoUpload, value); _prefs.Save(); }
    }

    // Load the cached auto-upload pref once at plugin init (alongside the other settings loads in Plugin.cs).
    // Reads from _prefs exactly once so the per-event getter never touches the config store.
    private void InitLogUpload() => _autoUpload = _prefs.Get(PrefAutoUpload, true);

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
        if (!AutoUpload) return;   // only buffer raw events when auto-uploading; manual uses entry aggregates
        _logBuffer.Add(evt);
    }

    // -----------------------------------------------------------------------
    // Serialize + upload: called from ManualArchive (Plugin.History.cs) after
    // the encounter entry has been added to history.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Auto path: serializes the captured raw event stream + actor snapshots and fires off a
    /// fire-and-forget upload. Called once per archive when <see cref="AutoUpload"/> is on; never throws.
    /// </summary>
    internal void MaybeUploadLog(EncounterHistoryEntry entry)
    {
        if (!AutoUpload) { _logBuffer.Clear(); return; }

        var truncated = _logBuffer.Truncated;   // capture before Flush() clears it
        var events = _logBuffer.Flush();         // also clears the buffer
        if (events.Count == 0)
        {
            _services.Log.Info("[CombatMeter.SP1] No events captured — skipping auto-upload.");
            return;
        }

        AssembleAndUpload(entry, events, truncatedEvents: truncated);
    }

    /// <summary>Manual per-run upload from history. Uses the entry's stored aggregates; no raw events
    /// (the buffer was flushed at archive) — every rendered number rides on <c>derived</c>.</summary>
    internal void UploadHistoryEntry(EncounterHistoryEntry entry)
    {
        if (UploadStateFor(entry) == UploadPhase.InFlight) return;   // debounce double-click
        AssembleAndUpload(entry, Array.Empty<CombatLogEvent>(), truncatedEvents: true);
    }

    // Shared assemble+upload core for both paths. Differs only in the event source (buffer flush for
    // auto, empty for manual) and the truncation flag. Never throws into the (main-thread) caller.
    private void AssembleAndUpload(EncounterHistoryEntry entry, IReadOnlyList<CombatLogEvent> events, bool truncatedEvents)
    {
        try
        {
            var log = LogAssembler.Assemble(entry, events, SignerKey, truncatedEvents);
            var url = "https://stellar-logs-web.boshido.workers.dev/run/" +
                      log.Header.Encounter.LevelUuid.ToString(CultureInfo.InvariantCulture);
            _uploadStatus.Set(entry, UploadPhase.InFlight, url);
            _services.Log.Info(
                $"[CombatMeter.SP1] Uploading log {log.Header.LogId} levelUuid={log.Header.Encounter.LevelUuid} " +
                $"({events.Count} events, {entry.Entities.Count} actors).");

            LogUploader.UploadFireAndForget(log, (ok, status, err) =>
            {
                // Callback fires on a thread-pool thread; only mutate the (lock-free) status dict +
                // call thread-safe log methods here — never touch uGUI.
                _uploadStatus.Set(entry, ok ? UploadPhase.Done : UploadPhase.Failed, url);
                if (ok) _services.Log.Info($"[CombatMeter.SP1] Upload OK (HTTP {status}): {log.Header.LogId}");
                else    _services.Log.Warning($"[CombatMeter.SP1] Upload FAILED (HTTP {status}): {err}");
            });
        }
        catch (Exception ex)
        {
            // Any unhandled exception here must NOT propagate into the main-thread caller.
            _uploadStatus.Set(entry, UploadPhase.Failed, null);
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
