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
using Stellar.CombatMeter.Replay;
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

    // Set by the fire-and-forget upload callback (thread-pool thread) whenever an entry's terminal
    // upload phase changes; drained on the Unity main thread (PersistUploadStateIfDirty, called from
    // OnUpdate) to re-persist history so a completed Done/Failed — including a manual retry's result,
    // which has no surrounding archive save — survives a relaunch. The archive-time SaveHistory runs
    // BEFORE the async upload finishes, so without this the most-recent upload would not be persisted.
    private volatile bool _uploadStateDirty;

    // Main-thread drain: re-persist history once after an async upload changed a terminal phase.
    // A cheap volatile read per tick; SaveHistory runs only on the tick that observes the flag set.
    private void PersistUploadStateIfDirty()
    {
        if (!_uploadStateDirty) return;
        _uploadStateDirty = false;
        SaveHistory();
    }

    internal UploadPhase UploadStateFor(EncounterHistoryEntry e) => _uploadStatus.PhaseFor(e);

    internal string? UploadUrlFor(EncounterHistoryEntry e) => _uploadStatus.UrlFor(e);

    // Persistence lives in Plugin.HistoryStore.cs: the live _uploadStatus is mirrored to the "uploadStates"
    // sidecar (keyed by the entry's LevelUuid+ArchivedAtMs) at SaveHistory and re-hydrated from it at
    // LoadHistory. The transient-InFlight collapse rule is UploadStatusTable.Persistable.

    // -----------------------------------------------------------------------
    // Settings keys (read/written from the "combatmeter" config section)
    // -----------------------------------------------------------------------

    private const string PrefAutoUpload = "logUpload.autoUpload";
    private const string PrefSignerKey  = "logUpload.signerKey";

    // P2: spread the party's simultaneous auto-uploads so arrival order is meaningful and the
    // worker isn't hit by N summary POSTs in the same second (free plan). Manual is user-initiated.
    private const int UploadJitterMaxMs = 8000;

    // -----------------------------------------------------------------------
    // Lazy initialisation (assembler is created once on first use)
    // -----------------------------------------------------------------------

    private CombatLogAssembler LogAssembler
        => _logAssembler ??= new CombatLogAssembler(_services);

    private bool _warnedUnknownRegion;

    /// <summary>Spec §2: withhold uploads when the install's region is undetected; environment.region config rescues.</summary>
    private bool RegionKnownOrWarn()
    {
        if (_services.GameEnvironment.Region != GameRegion.Unknown) return true;
        if (!_warnedUnknownRegion)
        {
            _warnedUnknownRegion = true;
            _services.Log.Warning("[CombatMeter.SP1] Game region UNKNOWN — uploads withheld. Set environment.region (sea|jp) in stellar.framework.config.json to override.");
        }
        return false;
    }

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
    /// fire-and-forget upload. Called once per archive when <see cref="AutoUpload"/> is on; never
    /// throws. Returns <c>true</c> iff a summary upload was fired — from that point the upload's
    /// callback OWNS <paramref name="replayDoc"/> (uploads it per the merge verdict); <c>false</c>
    /// means no upload fired and the caller must upload <paramref name="replayDoc"/> itself.
    /// </summary>
    internal bool MaybeUploadLog(EncounterHistoryEntry entry, PositionUploadDoc? replayDoc = null)
    {
        if (!AutoUpload) { _logBuffer.Clear(); return false; }
        if (!RegionKnownOrWarn()) { _logBuffer.Clear(); return false; }
        if (entry.LevelUuid == 0)   // non-instanced (field) fight — same refusal as the manual
        {                           // path; uploading would collide every field fight on run:0
            _logBuffer.Clear();
            _services.Log.Info("[CombatMeter.SP1] Field fight (no run id) — not uploaded.");
            return false;
        }

        return AssembleAndUpload(entry, events: null, truncatedEvents: false, flushBuffer: true, replayDoc);
    }

    /// <summary>Manual per-run upload from history. Uses the entry's stored aggregates; no raw events
    /// (the buffer was flushed at archive) — every rendered number rides on <c>derived</c>. No replay
    /// doc exists for a past run (positions were already handled at that run's archive time).</summary>
    internal void UploadHistoryEntry(EncounterHistoryEntry entry)
    {
        if (UploadStateFor(entry) == UploadPhase.InFlight) return;   // debounce double-click
        if (entry.LevelUuid == 0)   // pre-v3 archive (identity not persisted) — /run/0 would collide; refuse
        {
            _uploadStatus.Set(entry, UploadPhase.Failed);
            _uploadStateDirty = true;
            _services.Log.Warning("[CombatMeter.SP1] Cannot upload: run has no levelUuid (archived before run-identity was persisted). Re-run the fight to upload it.");
            return;
        }
        AssembleAndUpload(entry, Array.Empty<CombatLogEvent>(), truncatedEvents: true, flushBuffer: false, replayDoc: null);
    }

    // Shared assemble+upload core for both paths. Differs only in the event source (buffer flush for
    // auto, empty for manual) and the truncation flag. Never throws into the (main-thread) caller.
    // flushBuffer=true (auto path) flushes _logBuffer INSIDE this try so a throw from the conversion
    // step can never escape uncaught; flushBuffer=false (manual path) uses the events/truncatedEvents
    // passed in as-is.
    //
    // Returns true iff a summary upload was fired (the code reached LogUploader.UploadFireAndForget)
    // — from that point the callback OWNS replayDoc (P2 single-shot positions handoff). Returns false
    // on the zero-events early-return, or when the catch below runs BEFORE the upload was fired —
    // in either false case the CALLER is responsible for uploading replayDoc itself.
    private bool AssembleAndUpload(EncounterHistoryEntry entry, IReadOnlyList<CombatLogEvent>? events, bool truncatedEvents, bool flushBuffer, PositionUploadDoc? replayDoc)
    {
        if (!flushBuffer && !RegionKnownOrWarn()) return false;

        var fired = false;
        try
        {
            if (flushBuffer)
            {
                truncatedEvents = _logBuffer.Truncated;   // capture before Flush() clears it
                events = _logBuffer.Flush();               // also clears the buffer
                if (_logBuffer.SkippedUnknownEvents > 0)
                    _services.Log.Warning($"[CombatMeter.SP1] Skipped {_logBuffer.SkippedUnknownEvents} unrecognized combat event(s) during log flush.");
                if (events.Count == 0)
                {
                    _services.Log.Info("[CombatMeter.SP1] No events captured — skipping auto-upload.");
                    return false;
                }
            }

            // Chunk the raw stream up front (auto path only — flushBuffer=false/manual passes
            // an empty list, so Chunk() naturally yields zero chunks and eventChunks: 0).
            var chunks = EventChunker.Chunk(events!);

            // Pass the capture-time boss config id so the assembler doesn't re-resolve from
            // wiped entity caches (ResetEntities fires before archive on scene change).
            var log = LogAssembler.Assemble(entry, events!, SignerKey, truncatedEvents, _bossMonsterInfo?.Id ?? 0, chunks.Count);
            var url = UploadVerdict.SiteBase + "/run/" + log.Header.Region + "/" +
                      log.Header.Encounter.LevelUuid.ToString(CultureInfo.InvariantCulture);
            _uploadStatus.Set(entry, UploadPhase.InFlight, url);
            _services.Log.Info(
                $"[CombatMeter.SP1] Uploading log {log.Header.LogId} levelUuid={log.Header.Encounter.LevelUuid} " +
                $"({events!.Count} events in {chunks.Count} chunk(s), {entry.Entities.Count} actors).");

            // Auto uploads (flushBuffer) get spread across a window so the party's simultaneous
            // archives don't all land on the worker in the same second; manual is user-initiated,
            // so it goes immediately.
            var delayMs = flushBuffer ? Random.Shared.Next(0, UploadJitterMaxMs) : 0;
            fired = true;
            LogUploader.UploadFireAndForget(log, (ok, status, err, verdict) =>
            {
                // Callback fires on a thread-pool thread; only mutate the (lock-free) status dict +
                // call thread-safe log methods here — never touch uGUI. Flag the terminal phase change
                // so the main thread re-persists it (drained in PersistUploadStateIfDirty via OnUpdate).
                // On success prefer the server's short run URL when the response carried one (a relative
                // "/run/…" is absolutized against the same SiteBase as `url`); otherwise (old server,
                // failure, or 409-resolved path whose body has no shortUrl) keep the constructed `url`.
                _uploadStatus.Set(entry, ok ? UploadPhase.Done : UploadPhase.Failed,
                    UploadVerdict.PreferredUrl(verdict, url));
                _uploadStateDirty = true;
                if (ok) OnSummaryUploadOk(log, chunks, replayDoc, status, verdict);
                else    OnSummaryUploadFailed(replayDoc, status, err, verdict);
            }, delayMs);

            MaybeReportPortraits();

            if (MasterScoreRefresh.IsMasterModeRun(entry.MasterModeScore, entry.DifficultyLevel))
                RefreshAndSendSelfMasterScore();

            return true;
        }
        catch (Exception ex)
        {
            // Any unhandled exception here must NOT propagate into the main-thread caller.
            _uploadStatus.Set(entry, UploadPhase.Failed, null);
            _uploadStateDirty = true;
            _logBuffer.Clear();
            _services.Log.Warning($"[CombatMeter.SP1] Log assembly/upload threw: {ex.Message}");
            return fired;
        }
    }

    // Success leg of the summary-upload callback (thread-pool thread — thread-safe calls only;
    // never touch uGUI). Gates chunk + positions uploads on the server's merge verdict.
    private void OnSummaryUploadOk(CombatLog log, List<EventChunk> chunks, PositionUploadDoc? replayDoc, int status, UploadVerdict? verdict)
    {
        var v = verdict ?? new UploadVerdict(true, false);
        _services.Log.Info($"[CombatMeter.SP1] Upload OK (HTTP {status}): {log.Header.LogId} kept={v.Kept} havePositions={v.HavePositions}");
        // Chunks upload only after the summary landed (ordering guarantee) — the
        // worker cannot associate orphaned chunks with a run it never saw. Skip when
        // this upload lost the multi-uploader merge (Kept=false): the logId is not a
        // segment's blob, so chunk POSTs would all 400 ("unknown-log").
        if (v.Kept && chunks.Count > 0)
            ChunkUploader.UploadChunksFireAndForget(
                LogUploader.ApiBase, log.Header.Region,
                log.Header.Encounter.LevelUuid, log.Header.LogId, chunks,
                msg => _services.Log.Warning(msg));
        else if (!v.Kept && chunks.Count > 0)
            _services.Log.Info($"[CombatMeter.SP1] Run already fully uploaded by a party member — skipping {chunks.Count} chunk upload(s).");
        if (replayDoc is not null)
        {
            if (!v.HavePositions) UploadReplayDoc(replayDoc);
            else _services.Log.Info("[CombatMeter.SP1] Positions already attached server-side — skipping positions upload.");
        }
    }

    // Failure leg of the summary-upload callback (thread-pool thread — thread-safe calls only;
    // never touch uGUI).
    private void OnSummaryUploadFailed(PositionUploadDoc? replayDoc, int status, string? err, UploadVerdict? verdict)
    {
        _services.Log.Warning($"[CombatMeter.SP1] Upload FAILED (HTTP {status}): {err}");
        // Summary failed — fall back to today's behavior: positions upload ungated
        // (they attach via the pending path even without a matching segment). The one
        // exception: a failed SUPPLEMENT still carried a verdict whose HavePositions
        // came from the 409 body — respect it (Task 10's path).
        if (replayDoc is not null && verdict?.HavePositions != true) UploadReplayDoc(replayDoc);
    }

    // -----------------------------------------------------------------------
    // Dispose: clear the buffer
    // -----------------------------------------------------------------------

    private void DisposeLogUpload()
    {
        _logBuffer.Clear();
    }
}
