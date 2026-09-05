// SP1: Full combat-event capture + StellarLogs upload integration.
//
// Feature boundary:
//   - Capture: OnCombatEvent feeds the EventSpool (disk-backed, two tracks) during an encounter.
//   - Serialize: ManualArchive triggers SerializeAndUpload() after the encounter is archived.
//   - Upload: auto path gated on the CURRENT content's `<kind>.stats` policy cell (default auto —
//     see Plugin.UploadPolicy.cs); fire-and-forget; never blocks or crashes the game.
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

    // Disk-backed replacement for the two event rings (rDPS spool). Lazy: _services is not available at
    // field-initialiser time — every call site below goes through the Spool property.
    private EventSpool? _spool;
    private EventSpool Spool => _spool ??= new EventSpool(_services.Data);
    private CombatLogAssembler? _logAssembler;

    // -----------------------------------------------------------------------
    // Per-entry upload status (read by the history UI on the main thread; written
    // by the fire-and-forget callback on a thread-pool thread). Backed by the
    // services-free UploadStatusTable (immutable values → no torn cross-thread reads).
    // -----------------------------------------------------------------------

    private readonly UploadStatusTable _uploadStatus = new();

    // Entries whose upload phase changed since the last drain — added by the fire-and-forget upload callback
    // (thread-pool thread) and drained on the Unity main thread (PersistUploadStateIfDirty, from OnUpdate) to
    // rewrite ONLY those runs' per-run history files. A completed Done/Failed/Outdated — including a manual
    // retry's result, which has no surrounding archive write — thus survives a relaunch. Guarded by its own
    // lock; the HashSet dedups an entry that changes phase more than once before the drain.
    private readonly HashSet<EncounterHistoryEntry> _uploadStateDirty = new();

    private void MarkUploadStateDirty(EncounterHistoryEntry entry)
    {
        lock (_uploadStateDirty) _uploadStateDirty.Add(entry);
    }

    // Main-thread drain: rewrite the per-run history file for each entry whose upload phase changed. Only
    // still-live entries are written (an evicted/deleted run's file is already gone).
    private void PersistUploadStateIfDirty()
    {
        EncounterHistoryEntry[] dirty;
        lock (_uploadStateDirty)
        {
            if (_uploadStateDirty.Count == 0) return;
            dirty = new EncounterHistoryEntry[_uploadStateDirty.Count];
            _uploadStateDirty.CopyTo(dirty);
            _uploadStateDirty.Clear();
        }
        foreach (var e in dirty) if (_history.Contains(e)) WriteHistoryFile(e);
    }

    internal UploadPhase UploadStateFor(EncounterHistoryEntry e) => _uploadStatus.PhaseFor(e);

    internal string? UploadUrlFor(EncounterHistoryEntry e) => _uploadStatus.UrlFor(e);

    // Persistence lives in Plugin.HistoryStore.cs: the live _uploadStatus is mirrored to the "uploadStates"
    // sidecar (keyed by the entry's LevelUuid+ArchivedAtMs) at SaveHistory and re-hydrated from it at
    // LoadHistory. The transient-InFlight collapse rule is UploadStatusTable.Persistable.

    // -----------------------------------------------------------------------
    // Settings keys (read/written from the "combatmeter" config section)
    // -----------------------------------------------------------------------

    // logUpload.autoUpload is RETIRED as a live setting — the eight per-content policy cells replaced it
    // (spec § 2.2). The legacy key is still read exactly once, by LoadOrMigrateUploadPolicy, to seed those
    // cells on the first load after upgrade, and is left on disk untouched afterwards.
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

    // The upload policy's prefs lifecycle + the cached hot-path booleans live in Plugin.UploadPolicy.cs
    // (InitUploadPolicy, called from the ctor in place of the retired InitLogUpload).

    /// <summary>
    /// Base64-PKCS#8 ECDSA P-256 private key used to sign uploads.
    /// MUST come from config / env — never hardcode a real secret here.
    /// If empty or absent the upload is sent unsigned (server will reject if UPLOAD_PUBKEY is set).
    /// TODO(SP1): plumb through a secure key-provisioning flow (e.g. env-var injected by launcher).
    /// </summary>
    private string? SignerKey => _prefs.Get(PrefSignerKey, "");

    // Per-install identity for claim hardening — generated locally + persisted on first use, then
    // attached (pubkey + second signature) to every upload so the site can learn which install
    // genuinely plays each character. Lazy singleton (process-lifetime), like LogAssembler.
    private InstallKey? _installKey;
    private InstallKey InstallKeyInstance
        => _installKey ??= InstallKey.LoadOrCreate(k => _prefs.Get(k, ""), (k, v) => _prefs.Set(k, v));

    // -----------------------------------------------------------------------
    // Capture: called from OnCombatEvent (Plugin.Capture.cs) for every event
    // -----------------------------------------------------------------------

    /// <summary>
    /// Feeds the raw event into the spool — on every combat event, BEFORE the existing processing
    /// path, while the meter is PAUSED, and whatever the upload policy says (owner ruling 2026-08-14:
    /// capture is always-on; policy gates the SEND, at archive time). O(1) append to the open batch; a full
    /// batch hands its serialize+gzip+write to the thread pool, so no frame blocks on it.</summary>
    internal void MaybeCaptureForLog(CombatEvent evt)
    {
        // One cached bool (RecomputeUploadPolicyCache) — never prefs, never a kind resolution. TRUE
        // unconditionally since the ruling; see Plugin.UploadPolicy.cs's EventCaptureEnabled for why.
        if (!_captureForLogEnabled) return;
        Spool.Add(evt, _services.CombatSnapshot.LocalEntityId);
    }

    // -----------------------------------------------------------------------
    // Serialize + upload: called from ManualArchive (Plugin.History.cs) after
    // the encounter entry has been added to history.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Auto path: serializes the captured raw event stream + actor snapshots and fires off a
    /// fire-and-forget upload. Called once per archive; uploads only when the archived run's content
    /// kind has <c>&lt;kind&gt;.stats = auto</c> (spec § 2.4). Never
    /// throws. Returns <c>true</c> iff a summary upload was fired — from that point the upload's
    /// callback OWNS <paramref name="replayDoc"/> (uploads it per the merge verdict); <c>false</c>
    /// means no upload fired and the caller must upload <paramref name="replayDoc"/> itself.
    /// </summary>
    /// <summary>Terminal phase for an upload completion. A server <b>426</b> means the run's captured
    /// build is below the upload floor — the payload carries the OLD <c>pluginVer</c> baked in at capture,
    /// so retrying (even from an upgraded client) can never succeed; it maps to <see cref="UploadPhase.Outdated"/>,
    /// not the retryable <see cref="UploadPhase.Failed"/>. Everything else non-2xx stays a real retryable failure.</summary>
    internal static UploadPhase PhaseFromResult(bool ok, int status)
        => ok ? UploadPhase.Done
         : status == 426 ? UploadPhase.Outdated
         : UploadPhase.Failed;

    internal bool MaybeUploadLog(EncounterHistoryEntry entry, PositionUploadDoc? replayDoc = null)
    {
        // Compat floor (owner ask 2026-08-16): a build below the server floor would be 426'd, so withhold
        // the send rather than fail it — the record is RETAINED and hand-pushes normally once updated. This
        // gates the SEND only; capture/archive already happened (capture is always-on). Fail-open: the flag
        // is set true only after the endpoint confirmed we are below (Plugin.UploadCompat.cs).
        if (_uploadBelowFloor)
        {
            _services.Log.Info(
                $"[CombatMeter.SP1] stats upload withheld: plugin below the server upload floor " +
                $"(min {_uploadFloorMin ?? "?"}) — update to send. Record retained.");
            _uploadStatus.Set(entry, UploadPhase.Outdated);
            RetainWithoutUpload(entry, replayDoc);
            return false;
        }

        var kind = ResolveKind(entry);
        var state = EffectivePolicyFor(entry, UploadArtifact.Stats);   // fail-open on an empty map (§ 8.3)
        if (!UploadPolicy.Allows(state, UploadTrigger.Auto))
        {
            LogUploadRefusal(kind, UploadArtifact.Stats, UploadTrigger.Auto, state);
            // Owner ruling 2026-07-29: "off = just don't upload it but game still keep record into chunk
            // always" / "it suppose to store all replay even flag mark off". `off` withholds the SEND; it
            // must never destroy the record. This used to CLEAR the capture buffer and drop everything, which
            // is why the owner's Giant Golem Crusade could never be recovered: raw events are NOT stored
            // with an archive (history keeps aggregates only), and the retained first-send copy — the one
            // thing a later manual push can replay verbatim — was written ONLY when a send fired. No send
            // ever fired for `other`, so no copy existed, and the manual push sent "0 events in 0
            // chunk(s)" however many hours or relaunches later.
            RetainWithoutUpload(entry, replayDoc);
            return false;
        }
        // Difficulty axis (owner-approved 2026-07-29, spec § 8.5). A SECOND, independent gate: the per-kind
        // cell above says whether this KIND may send, this says whether this DIFFICULTY may — a run must
        // pass both. Retains exactly like the `off` path above, for the same reason, and deliberately sets
        // NO UploadPhase: `Skipped` renders "Uploads off for this content", which would be a lie here (the
        // cell is on), and the row's default "⤓ Upload this run" is what keeps the hand push discoverable.
        if (!TierAllowsUpload(entry))
        {
            LogTierRefusal(entry, kind);
            RetainWithoutUpload(entry, replayDoc);
            return false;
        }
        if (!RegionKnownOrWarn())
        {
            // Fix 2026-08-14: this branch used to CLEAR the capture buffer — destroying the captured events
            // and writing NO container, so the prepared replay doc's only custody was the one-shot
            // positions POST in FinalizeAndMaybeUploadReplay (a failure there = permanent loss). Retain
            // exactly like the policy-off / tier paths above so the run's true bodies survive locally
            // for a later manual push. (The retained summary bakes region "unknown" — a verbatim push
            // before environment.region is configured will be refused server-side, but the bytes are
            // no longer destroyed.)
            RetainWithoutUpload(entry, replayDoc);
            return false;
        }
        if (entry.LevelUuid == 0)   // non-instanced (field) fight — same refusal as the manual
        {                           // path; uploading would collide every field fight on run:0
            Spool.Discard();
            _services.Log.Info("[CombatMeter.SP1] Field fight (no run id) — not uploaded.");
            return false;
        }

        // Pure overload: the logging one would double-log the refusal (PrepareReplayDoc already logged it).
        var replaySendAllowed = ReplayAutoUploadAllowed(_contentKinds, _uploadPolicy, entry);
        return AssembleAndUpload(entry, segment: null, flushBuffer: true, replayDoc, replaySendAllowed);
    }

    /// <summary>Manual per-run upload from history. Uses the entry's stored aggregates; no raw events
    /// (the buffer was flushed at archive) — every rendered number rides on <c>derived</c>. No replay
    /// doc exists for a past run (positions were already handled at that run's archive time).</summary>
    internal void UploadHistoryEntry(EncounterHistoryEntry entry)
    {
        if (UploadStateFor(entry) == UploadPhase.InFlight) return;   // debounce double-click

        // Compat floor (owner ask 2026-08-16): below the server floor the send would be 426'd. Withhold
        // even a hand push and say why (Outdated ⇒ "⚠ Update to upload") rather than let it fail with the
        // misleading "✗ Failed — Retry" that invites pointless retries. The archive stays; after updating
        // the same row pushes normally.
        if (_uploadBelowFloor)
        {
            _services.Log.Info(
                $"[CombatMeter.SP1] stats upload withheld (manual): plugin below the server upload floor " +
                $"(min {_uploadFloorMin ?? "?"}) — update to send.");
            _uploadStatus.Set(entry, UploadPhase.Outdated);
            MarkUploadStateDirty(entry);
            return;
        }

        // Spec § 2.4: a hand push proceeds unless the archived run's kind has stats `off`. The kind comes
        // from the ENTRY's stored scene name, so a re-upload after a relaunch resolves the kind the run
        // had when it was archived — never whatever scene happens to be live now.
        var kind = ResolveKind(entry);
        var state = EffectivePolicyFor(entry, UploadArtifact.Stats);   // fail-open on an empty map (§ 8.3)
        if (!UploadPolicy.Allows(state, UploadTrigger.Manual))
        {
            LogUploadRefusal(kind, UploadArtifact.Stats, UploadTrigger.Manual, state);
            // Skipped, NOT Failed. This is a policy refusal: nothing was sent and retrying cannot change
            // it until the cell is turned on. It used to reuse Failed to avoid adding a persisted phase
            // value, which rendered "✗ Failed — Retry" and cost the owner twelve pointless Retry presses
            // on a Giant Golem Crusade run (2026-07-29). The row still shows state, so the original
            // reason for reusing Failed — a click that looks like it did nothing — is still served.
            _uploadStatus.Set(entry, UploadPhase.Skipped);
            MarkUploadStateDirty(entry);
            return;
        }

        if (entry.LevelUuid == 0)   // pre-v3 archive (identity not persisted) — /run/0 would collide; refuse
        {
            _uploadStatus.Set(entry, UploadPhase.Failed);
            MarkUploadStateDirty(entry);
            _services.Log.Warning("[CombatMeter.SP1] Cannot upload: run has no levelUuid (archived before run-identity was persisted). Re-run the fight to upload it.");
            return;
        }

        if (TryLoadReUpload(entry, out var payload)) { ReplayReUpload(entry, payload); return; }

        // Pre-feature entry (no retained payload): today's fallback — assemble from the entry, no chunks/positions.
        // EMPTY-BUT-TRUNCATED: no event stream to send, and `truncatedEvents` is how the summary says so
        AssembleAndUpload(entry, SpoolSegment.EmptyTruncated, flushBuffer: false, replayDoc: null);
    }

    /// <summary>Loads the retained first-send payload for <paramref name="entry"/>, if one exists. The
    /// testable seam: absent/garbage container ⇒ <c>false</c> so the caller falls back to today's
    /// assemble-from-entry behavior (a run archived before this feature has nothing retained).</summary>
    private bool TryLoadReUpload(EncounterHistoryEntry entry, out ReUploadPayload payload)
    {
        payload = default!;
        var bytes = _services.Data.Read(ReUploadContainer.ContainerName(entry.LevelUuid, entry.ArchivedAtMs));
        return bytes is not null && ReUploadContainer.TryDeserialize(bytes, out payload);
    }

    // Reproduces the captured first-send BODIES byte-for-byte, but re-sends all three UNCONDITIONALLY
    // as a repair (skipPrecheck → full ingest) — deliberately NOT the verdict-gated subset the first
    // send transmitted (first send gates chunks on v.Kept and positions on !v.HavePositions). This is
    // correct because the server ingest is content-signed with no nonce/timestamp replay guard and is
    // non-destructive (worker dee9069b); a future edit must NOT "optimize" this to mirror first-send
    // verdict-gating or it silently breaks repair-after-total-server-loss.
    private void ReplayReUpload(EncounterHistoryEntry entry, ReUploadPayload payload)
    {
        try
        {
            var url = UploadVerdict.SiteBase + "/run/" + payload.Region + "/" + payload.LevelUuid.ToString(CultureInfo.InvariantCulture);
            _uploadStatus.Set(entry, UploadPhase.InFlight, url);
            _services.Log.Info($"[CombatMeter.SP1] Re-uploading run {payload.LevelUuid} verbatim (logId={payload.LogId}, {payload.Chunks.Count + payload.ChunkRefs.Count} chunk(s), positions={(payload.Positions is not null)}).");
            LogUploader.PostRawFireAndForget(payload.Summary, (ok, status, err, verdict) =>
            {
                // Prefer the server's SHORT run URL, same as the first-send path. Storing the constructed
                // numeric `url` here made every re-upload downgrade the entry's link (owner report
                // 2026-07-30: "click upload segment and upload all both return number"), even though the
                // worker returned shortId on all three segments — confirmed by tailing stellar-logs.
                _uploadStatus.Set(entry, PhaseFromResult(ok, status),
                    UploadVerdict.PreferredUrl(verdict, url));
                MarkUploadStateDirty(entry);
                if (!ok) { _services.Log.Warning($"[CombatMeter.SP1] Re-upload summary FAILED (HTTP {status}): {err}"); return; }
                if (payload.Chunks.Count > 0)   // V1 inlined envelopes; V2 points at spool blobs (below)
                    ChunkUploader.PostRawEnvelopesFireAndForget(LogUploader.ApiBase, payload.Region, payload.LevelUuid, payload.Chunks, m => _services.Log.Warning(m));
                if (payload.ChunkRefs.Count > 0)
                    ChunkUploader.ReuploadRefsFireAndForget(LogUploader.ApiBase, payload.Region, payload.LevelUuid, payload.LogId, payload.ChunkRefs, _services.Data, m => _services.Log.Warning(m));
                if (payload.Positions is not null) MaybeReUploadPositions(entry, payload);
            });
        }
        catch (Exception ex)
        {
            // Defense-in-depth (body is non-throwing today, matching AssembleAndUpload's guard below):
            // a replay-kickoff fault must never propagate into the main-thread caller.
            _uploadStatus.Set(entry, UploadPhase.Failed, null);
            MarkUploadStateDirty(entry);
            _services.Log.Warning($"[CombatMeter.SP1] Re-upload replay threw: {ex.Message}");
        }
    }

    // Spec § 2.4: a manual push attaches the replay only when that run's content has its replay cell
    // NOT `off`. This is a POLICY gate on the user's own configuration — deliberately NOT the
    // server-verdict gating that ReplayReUpload's comment forbids: the summary and chunks still
    // re-send unconditionally as a repair, and an `auto` or `manual` cell still re-sends positions
    // unconditionally. Runs on the thread-pool callback thread, so it touches only the immutable
    // _contentKinds reference, the enum-array policy table, and thread-safe log calls — never uGUI.
    private void MaybeReUploadPositions(EncounterHistoryEntry entry, ReUploadPayload payload)
    {
        var kind = ResolveKind(entry);
        var state = EffectivePolicyFor(entry, UploadArtifact.Replay);  // fail-open on an empty map (§ 8.3)
        if (!UploadPolicy.Allows(state, UploadTrigger.Manual))
        {
            LogUploadRefusal(kind, UploadArtifact.Replay, UploadTrigger.Manual, state);
            return;
        }
        PositionUploader.PostRawFireAndForget(payload.Region, payload.LevelUuid, payload.Positions!, (ok, status, err) =>
        {
            // Outcome-logging parity with the live path's positions OK/FAILED lines (UploadReplayDoc,
            // Plugin.Replay.cs). This leg used to pass NO callback, so a failed manual positions re-send
            // was silent both ways — the user had no signal the replay never landed (fix 2026-08-14).
            // Thread-pool thread: log calls only, never uGUI.
            var line = ReUploadPositionsOutcomeLine(ok, status, err, payload.LevelUuid);
            if (ok) _services.Log.Info(line);
            else    _services.Log.Warning(line);
        });
    }

    /// <summary>Outcome line for the manual re-upload positions leg — same OK/FAILED shape as the live
    /// path's lines (<c>UploadReplayDoc</c>, Plugin.Replay.cs). Pure so the logging decision pins
    /// headless (<c>ReUploadPositionsOutcomeTests</c>): BOTH outcomes must produce a line — this leg
    /// used to log nothing either way (fix 2026-08-14).</summary>
    internal static string ReUploadPositionsOutcomeLine(bool ok, int status, string? err, long levelUuid)
        => ok
            ? $"[CombatMeter.SP1] Re-upload positions OK (HTTP {status}) levelUuid={levelUuid}"
            : $"[CombatMeter.SP1] Re-upload positions FAILED (HTTP {status}) levelUuid={levelUuid}: {err}";

    // Shared assemble+upload core for both paths. Differs only in the event source: flushBuffer=true (auto)
    // ROTATES the spool INSIDE the try so a seal/hand-off throw can never escape uncaught; flushBuffer=false
    // (manual) uses the segment passed in as-is. Never throws into the (main-thread) caller.
    //
    // Returns true iff a summary upload was fired (the code reached LogUploader.UploadFireAndForget)
    // — from that point the callback OWNS replayDoc (P2 single-shot positions handoff). Returns false
    // on the zero-events early-return, or when the catch below runs BEFORE the upload was fired —
    // in either false case the CALLER is responsible for uploading replayDoc itself.
    private bool AssembleAndUpload(EncounterHistoryEntry entry, SpoolSegment? segment,
                                   bool flushBuffer, PositionUploadDoc? replayDoc, bool replaySendAllowed = true)
    {
        if (!flushBuffer && !RegionKnownOrWarn()) return false;

        var fired = false;
        try
        {
            if (flushBuffer)
            {
                var skipped = Spool.SkippedUnknownEvents;   // read BEFORE Rotate — it zeroes the counter
                segment = Spool.Rotate();
                if (skipped > 0)
                    _services.Log.Warning($"[CombatMeter.SP1] Skipped {skipped} unrecognized combat event(s) during log flush.");
                if (segment.ChunkCount == 0)
                {
                    _services.Log.Info("[CombatMeter.SP1] No events captured — skipping auto-upload.");
                    // Fix 2026-08-14: this early return used to skip PersistReUpload below, so a banked
                    // zero-events archive (e.g. a clear marker with capture off) got NO .replaydoc
                    // container and its prepared replay doc's only custody was the one-shot positions
                    // POST in FinalizeAndMaybeUploadReplay. Retain (summary + positions, zero chunks)
                    // so a failed/withheld positions send is recoverable by a manual push. The return
                    // value stays false — the caller still owns the direct positions hand-off
                    // (FinalizeUploadDecoupleTests' semantics are unchanged).
                    if (ShouldRetainUnsentArchive(entry.LevelUuid))
                        RetainAssembled(entry, segment, replayDoc);
                    return false;
                }
            }

            // Already chunked: the events live in this segment's blobs, one blob per upload chunk.
            var seg = segment ?? SpoolSegment.Empty;

            // Boss config id(s) ride on the entry itself (entry.StageBosses, snapshotted at archive
            // time) so the assembler never has to re-resolve from wiped entity caches (ResetEntities
            // fires before archive on scene change) — see CombatLogAssembler.ResolveStageBosses.
            var log = LogAssembler.Assemble(entry, Array.Empty<CombatLogEvent>(), SignerKey, seg.TruncatedDmg, seg.Dmg.Count, InstallKeyInstance);
            var url = UploadVerdict.SiteBase + "/run/" + log.Header.Region + "/" +
                      log.Header.Encounter.LevelUuid.ToString(CultureInfo.InvariantCulture);
            _uploadStatus.Set(entry, UploadPhase.InFlight, url);
            _services.Log.Info(
                $"[CombatMeter.SP1] Uploading log {log.Header.LogId} levelUuid={log.Header.Encounter.LevelUuid} " +
                $"({seg.Dmg.Count} dmg chunk(s), {seg.Buff.Count} buff chunk(s), {entry.Entities.Count} actors).");

            // Auto uploads (flushBuffer) get spread across a window so the party's simultaneous
            // archives don't all land on the worker in the same second; manual is user-initiated,
            // so it goes immediately.
            var delayMs = flushBuffer ? Random.Shared.Next(0, UploadJitterMaxMs) : 0;
            fired = true;
            if (flushBuffer) PersistReUpload(entry, log, seg, replayDoc);
            LogUploader.UploadFireAndForget(log, (ok, status, err, verdict) =>
            {
                // Callback fires on a thread-pool thread; only mutate the (lock-free) status dict +
                // call thread-safe log methods here — never touch uGUI. Flag the terminal phase change
                // so the main thread re-persists it (drained in PersistUploadStateIfDirty via OnUpdate).
                // On success prefer the server's short run URL when the response carried one (a relative
                // "/run/…" is absolutized against the same SiteBase as `url`); otherwise (old server,
                // failure, or 409-resolved path whose body has no shortUrl) keep the constructed `url`.
                _uploadStatus.Set(entry, PhaseFromResult(ok, status),
                    UploadVerdict.PreferredUrl(verdict, url));
                MarkUploadStateDirty(entry);
                if (ok) OnSummaryUploadOk(log, seg, replayDoc, status, verdict, replaySendAllowed);
                else    OnSummaryUploadFailed(replayDoc, status, err, verdict);
            }, delayMs, skipPrecheck: !flushBuffer);   // manual re-upload (flushBuffer=false) forces full ingest so the server can REPAIR a bad run

            MaybeReportPortraits();

            if (MasterScoreRefresh.IsMasterModeRun(entry.MasterModeScore, entry.DifficultyLevel))
                RefreshAndSendSelfMasterScore();

            return true;
        }
        catch (Exception ex)
        {
            // Any unhandled exception here must NOT propagate into the main-thread caller.
            _uploadStatus.Set(entry, UploadPhase.Failed, null);
            MarkUploadStateDirty(entry);
            Spool.Discard();
            _services.Log.Warning($"[CombatMeter.SP1] Log assembly/upload threw: {ex.Message}");
            return fired;
        }
    }

    // Success leg of the summary-upload callback (thread-pool thread — thread-safe calls only;
    // never touch uGUI). Gates chunk + positions uploads on the server's merge verdict.
    private void OnSummaryUploadOk(CombatLog log, SpoolSegment seg, PositionUploadDoc? replayDoc, int status, UploadVerdict? verdict, bool replaySendAllowed)
    {
        var v = verdict ?? new UploadVerdict(true, false);
        _services.Log.Info($"[CombatMeter.SP1] Upload OK (HTTP {status}): {log.Header.LogId} kept={v.Kept} havePositions={v.HavePositions}");
        // UPLOAD ALL — never skip on the merge verdict (owner rule 2026-08-25: "all uploads kept, never
        // drop"). Every uploader streams its OWN event chunks + positions regardless of `Kept`, so each
        // uploader's per-uploader view (`?upload=`) is COMPLETE. The worker keeps every contributor's
        // logId in the segment's ledger (resolveIngest.upsertUploadRef, logId-keyed) and accepts every
        // ledger logId's chunks (`acceptedLogIds`); positions attach to this uploader's OWN UploadRef
        // (`withUploaderPositionsRef`, incl. the non-representative "superseded" path). The old
        // `Kept`/`HavePositions` skips are what dropped a non-elected uploader's data — removed.
        // Chunks still upload only AFTER the summary landed (ordering guarantee — the worker cannot
        // associate chunks with a run it never saw).
        if (seg.ChunkCount > 0)
            ChunkUploader.UploadSegmentFireAndForget(
                LogUploader.ApiBase, log.Header.Region,
                log.Header.Encounter.LevelUuid, log.Header.LogId, seg,
                _services.Data, msg => _services.Log.Warning(msg));
        if (replayDoc is not null)
        {
            // The doc is built even when the replay cell is off (it must be RETAINED regardless), so the
            // send is gated ONLY on the replay cell — no longer on the server's `HavePositions`, so each
            // uploader's own tracks reach the server for their per-uploader replay.
            if (!replaySendAllowed)
                _services.Log.Info("[CombatMeter.SP1] Positions retained, not uploaded (replay cell off).");
            else UploadReplayDoc(replayDoc);
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
    // Dispose: drop whatever the live segment has spooled
    // -----------------------------------------------------------------------

    private void DisposeLogUpload()
    {
        _spool?.Discard();   // null-conditional: teardown must not build a spool just to throw it away
    }

    /// <summary>Assembles the retained re-upload payload from the exact artifacts the auto path uploads.
    /// Pure — summary/positions ride the SAME writers the uploaders use, so they are byte-identical to what
    /// the first send transmitted. The event stream is stored as chunk REFS (container V2): the events
    /// already live in this segment's <c>spool/*</c> blobs, so re-inlining them would double the bytes on
    /// disk. BODIES only — whether/how a replay resends them is ReplayReUpload's decision.</summary>
    internal static ReUploadPayload BuildReUploadPayload(CombatLog log, SpoolSegment seg, PositionUploadDoc? replayDoc)
    {
        var refs = new List<SpoolChunkRef>(seg.ChunkCount); refs.AddRange(seg.Dmg); refs.AddRange(seg.Buff);
        return new ReUploadPayload(
            ReUploadContainer.Version,
            log.Header.Region,
            log.Header.Encounter.LevelUuid,
            log.Header.LogId,
            CombatLogWriter.Write(log),
            System.Array.Empty<string>(),
            replayDoc is null ? null : PositionJsonWriter.Write(replayDoc),
            refs);
    }

    /// <summary>Auto path only: retain the exact bodies this send is about to POST — byte-identical
    /// captures of the summary/chunks/positions — so a later re-upload has the true originals to
    /// repair from (B4; see ReplayReUpload for why replay resends all three UNCONDITIONALLY, not
    /// mirroring this send's verdict-gated subset). Fire-and-forget — <c>log</c>, the segment's ref lists
    /// and <c>replayDoc</c> are immutable snapshots (records / arrays sealed by Rotate), so building the
    /// payload is safe to defer entirely onto the background thread alongside the gzip+write — nothing here
    /// may run on the archive frame, a chunk-heavy run must never hitch it. The segment's ref METADATA is
    /// available synchronously from Rotate, so this never awaits the blob writes.
    /// Keyed by the entry's stable (LevelUuid, ArchivedAtMs) composite.</summary>
    /// <summary>Assembles the run exactly as an upload would and RETAINS it locally without sending —
    /// the `off` path. Owner ruling 2026-07-29: a withheld upload keeps its record, so a later manual push
    /// replays the true originals instead of the summary-only fallback. Deliberately mirrors the auto
    /// path's capture (same flush, same chunker, same assembler, same container key) so a retained-then-
    /// pushed run is byte-identical to one that uploaded immediately. Retention is bounded and
    /// self-cleaning: Plugin.HistoryStore deletes a container with its entry and sweeps orphans against
    /// the live history, which is itself capped. Never throws.</summary>
    private void RetainWithoutUpload(EncounterHistoryEntry entry, PositionUploadDoc? replayDoc)
    {
        try
        {
            if (!ShouldRetainUnsentArchive(entry.LevelUuid)) { Spool.Discard(); return; }   // field fight: nothing addressable to retain
            var seg = Spool.Rotate();
            RetainAssembled(entry, seg, replayDoc);
        }
        catch (Exception ex)
        {
            Spool.Discard();
            _services.Log.Warning($"[CombatMeter.SP1] retain-without-upload failed: {ex.Message}");
        }
    }

    /// <summary>Custody rule (fix 2026-08-14): a banked archive whose auto path fires NO summary upload
    /// (zero events flushed, unknown region, policy/tier refusal) must STILL write its retained
    /// .replaydoc container whenever the run is addressable — the container is the only durable custody
    /// of the prepared replay doc; without it the one-shot positions POST is the sole owner and a failed
    /// POST is permanent loss. A field fight (levelUuid 0) has nothing addressable to retain (the
    /// container is keyed by LevelUuid — same rule <see cref="RetainWithoutUpload"/> always had). Pure
    /// so it pins headless (<c>RetainUnsentArchiveTests</c>).</summary>
    internal static bool ShouldRetainUnsentArchive(long levelUuid) => levelUuid != 0;

    /// <summary>Shared retention core (fix 2026-08-14, extracted from <see cref="RetainWithoutUpload"/>):
    /// assembles the summary exactly as an upload would and persists the .replaydoc container
    /// (summary + chunk refs + positions). Also called directly by the zero-events early return in
    /// <see cref="AssembleAndUpload"/>, whose segment is already rotated (and empty — zero chunks).
    /// Never throws: a retention fault must not surface as <c>UploadPhase.Failed</c> on the entry
    /// (AssembleAndUpload's outer catch does exactly that) — it only warns, like the persist path.</summary>
    private void RetainAssembled(EncounterHistoryEntry entry, SpoolSegment seg, PositionUploadDoc? replayDoc)
    {
        try
        {
            var log = LogAssembler.Assemble(entry, Array.Empty<CombatLogEvent>(), SignerKey, seg.TruncatedDmg, seg.Dmg.Count, InstallKeyInstance);
            PersistReUpload(entry, log, seg, replayDoc);
            _services.Log.Info(
                $"[CombatMeter.SP1] Retained (not uploaded) log {log.Header.LogId} levelUuid={log.Header.Encounter.LevelUuid} " +
                $"({seg.Dmg.Count} dmg chunk(s), {seg.Buff.Count} buff chunk(s), replay={(replayDoc is not null)}).");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[CombatMeter.SP1] retain-without-upload failed: {ex.Message}");
        }
    }

    private void PersistReUpload(EncounterHistoryEntry entry, CombatLog log, SpoolSegment seg, PositionUploadDoc? replayDoc)
    {
        var name = ReUploadContainer.ContainerName(entry.LevelUuid, entry.ArchivedAtMs);
        System.Threading.Tasks.Task.Run(() =>
        {
            try { _services.Data.Write(name, ReUploadContainer.Serialize(BuildReUploadPayload(log, seg, replayDoc))); }
            catch (Exception ex) { _services.Log.Warning($"[CombatMeter.SP1] re-upload persist failed: {ex.Message}"); }
        });
    }
}
