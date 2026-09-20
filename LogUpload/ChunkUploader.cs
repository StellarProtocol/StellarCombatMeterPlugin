// Task 8: sequential chunk uploads for the auto path. Started only from the summary upload's
// success callback (LogUploader.UploadFireAndForget) — chunks upload only if the summary landed.
// Same HTTP posture as LogUploader: shared HttpClient, fire-and-forget on the thread pool, never
// throws into the caller. Per-chunk retries (2, 1s/3s backoff); a still-failing chunk is logged
// and skipped so later chunks still get uploaded.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Posts the raw event chunks produced by <see cref="EventChunker"/> to
/// <c>{base}/run/{region}/{levelUuid}/events</c>, one at a time, after the summary blob has
/// uploaded successfully. Fire-and-forget: never blocks or crashes the game.
/// </summary>
internal static class ChunkUploader
{
    // Single shared client (avoids socket exhaustion on repeated uploads); same posture as LogUploader.
    // Mutable (not readonly) + internal, mirroring LogUploader.HttpClient: tests swap this for a fake
    // HttpMessageHandler for the duration of a test (restored in `finally`), same
    // DisableParallelization collection pattern as LogUploaderHttpCollection.
    internal static HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    // 2 retries (3 attempts total) with 1s then 3s backoff between attempts. Internal (was private)
    // so PositionUploaderRetryTests can pin PositionUploader's policy to THIS one (parity model).
    internal static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };

    /// <summary>
    /// Kicks off sequential chunk uploads on the thread pool. Returns immediately; never throws.
    /// A chunk that still fails after retries is reported via <paramref name="logWarn"/> and
    /// skipped — later chunks continue uploading regardless.
    /// </summary>
    internal static void UploadChunksFireAndForget(
        string baseUrl,
        string region,
        long levelUuid,
        string logId,
        List<EventChunk> chunks,
        Action<string> logWarn)
    {
        if (chunks.Count == 0) return;
        _ = Task.Run(() => UploadSequentialAsync(baseUrl, region, levelUuid, logId, chunks, logWarn));
    }

    /// <summary>Re-POST pre-serialized chunk envelopes verbatim, sequentially, after the summary landed.</summary>
    internal static void PostRawEnvelopesFireAndForget(
        string baseUrl, string region, long levelUuid, IReadOnlyList<string> envelopeJsons, Action<string> logWarn)
        => _ = Task.Run(async () =>
        {
            var url = BuildUrl(baseUrl, region, levelUuid);
            for (var i = 0; i < envelopeJsons.Count; i++)
            {
                try
                {
                    // V1 envelopes are damage chunks on /events — a 404 there is an ordinary failure (retried).
                    if (!await PostWithRetryAsync(url, envelopeJsons[i], terminalOn404: false).ConfigureAwait(false))
                        logWarn($"[CombatMeter.SP1] Re-upload chunk {i} FAILED after retries — skipping; later chunks continue.");
                }
                catch (Exception ex) { logWarn($"[CombatMeter.SP1] Re-upload chunk {i} threw: {ex.Message} — skipping."); }
            }
        });

    /// <summary>Builds the region-scoped chunk-upload URL: <c>{baseUrl}/run/{region}/{levelUuid}/events</c>.</summary>
    internal static string BuildUrl(string baseUrl, string region, long levelUuid)
        => $"{baseUrl}/run/{region}/{levelUuid.ToString(CultureInfo.InvariantCulture)}/events";

    /// <summary>The buff track's own endpoint. A server that predates it answers 404 — terminal, never
    /// retried, and the blobs stay on disk for a later re-upload (see <see cref="PostRefsAsync"/>).</summary>
    internal static string BuildBuffUrl(string baseUrl, string region, long levelUuid)
        => $"{baseUrl}/run/{region}/{levelUuid.ToString(CultureInfo.InvariantCulture)}/buff-events";

    /// <summary>The sheet track's own endpoint (spec § 6.1). Like /buff-events, a 404 here is an old worker:
    /// terminal for the segment's sheet track, blobs kept for re-upload.</summary>
    internal static string BuildSheetUrl(string baseUrl, string region, long levelUuid)
        => $"{baseUrl}/run/{region}/{levelUuid.ToString(CultureInfo.InvariantCulture)}/sheet-events";

    /// <summary>One track's upload destination: where its chunks POST, how a 404 reads there, and the noun
    /// its warnings use. Grouped into one value so <see cref="PostRefsAsync"/> keeps a 5-parameter
    /// signature — the three fields always vary together, per endpoint.</summary>
    internal readonly struct TrackEndpoint
    {
        internal TrackEndpoint(string url, bool terminalOn404, string label)
        { Url = url; TerminalOn404 = terminalOn404; Label = label; }
        internal string Url { get; }
        /// <summary>Whether a 404 means "this ROUTE does not exist on that server" (terminal for the whole
        /// track) rather than "this chunk failed". True for <c>/buff-events</c> AND <c>/sheet-events</c>: they ship with this
        /// release, so a 404 there is an old worker. <c>/events</c> has existed all along, so a 404 there is
        /// an ordinary failure and keeps the V1 per-chunk retry semantics.</summary>
        internal bool TerminalOn404 { get; }
        internal string Label { get; }
    }

    // internal (not private) so the per-track 404 semantics above are PINNED by a test rather than only
    // described — ChunkUploaderSheetTests.Each_tracks_404_semantics_match_its_endpoints_age.
    internal static TrackEndpoint DmgEndpoint(string baseUrl, string region, long levelUuid, string label)
        => new(BuildUrl(baseUrl, region, levelUuid), false, label);

    internal static TrackEndpoint BuffEndpoint(string baseUrl, string region, long levelUuid, string label)
        => new(BuildBuffUrl(baseUrl, region, levelUuid), true, label);

    internal static TrackEndpoint SheetEndpoint(string baseUrl, string region, long levelUuid, string label)
        => new(BuildSheetUrl(baseUrl, region, levelUuid), true, label);

    /// <summary>Uploads a rotated segment: dmg → /events, buff → /buff-events, sheet → /sheet-events; buffx
    /// absent. The segment's disk-only track (<c>buffx</c>, the rows the send filter rejected) is deliberately
    /// never posted anywhere. Blobs are NOT deleted here — they belong to the retention container
    /// (Plugin.LogUpload's PersistReUpload) and die with it, so a re-upload can still replay them verbatim.
    /// Thin fire-and-forget wrapper over <see cref="UploadSegmentAsync"/> — see that method for the guard
    /// behavior. <paramref name="logInfo"/> is optional (defaults to no-op) so existing callers compile
    /// unchanged; <c>Plugin.LogUpload.cs</c>'s call site passes <c>_services.Log.Info</c>.</summary>
    internal static void UploadSegmentFireAndForget(
        string baseUrl, string region, long levelUuid, string logId, SpoolSegment seg,
        Stellar.Abstractions.Services.IPluginDataStore store, Action<string> logWarn, Action<string>? logInfo = null)
    {
        if (seg.ChunkCount == 0) return;
        _ = Task.Run(() => UploadSegmentAsync(baseUrl, region, levelUuid, logId, seg, store, logWarn, logInfo));
    }

    /// <summary>Task-returning core of <see cref="UploadSegmentFireAndForget"/> — extracted so tests can
    /// AWAIT it directly instead of racing the fire-and-forget wrapper's discarded task (and so a bug here
    /// surfaces as a thrown/observed exception in a test, never an unobserved one on the finalizer thread).
    /// <para><b>Fix (2026-09-20, prod measurement: 18/294 damage-bearing archives/day arrived with a header
    /// declaring 1–20 chunks and delivered NONE).</b> The prior body had NO guard at all: <c>await
    /// seg.Completion</c> and each track's <see cref="PostRefsAsync"/> call were unguarded, so ANY fault
    /// anywhere in this method — the spool's blob-write completion faulting, or a track throwing before its
    /// own per-chunk try/catch — silently killed the whole discarded <see cref="Task.Run"/> and dropped every
    /// track. Now: a faulted <c>Completion</c> is logged and the tracks are attempted anyway (their refs still
    /// point at whatever blobs DID land — a missing one is caught by <see cref="PostRefsAsync"/>'s own
    /// per-chunk guard); each track's send is wrapped individually via <see cref="PostTrackGuarded"/> so one
    /// track throwing never takes the other two down; an outer try/catch is the last belt. Behavior when
    /// nothing faults is unchanged — same three POSTs, same dmg → buff → sheet order.</para></summary>
    internal static async Task UploadSegmentAsync(
        string baseUrl, string region, long levelUuid, string logId, SpoolSegment seg,
        Stellar.Abstractions.Services.IPluginDataStore store, Action<string> logWarn, Action<string>? logInfo = null)
    {
        try
        {
            logInfo?.Invoke($"[CombatMeter.SP1] Sending segment chunks for {logId}: dmg={seg.Dmg.Count} buff={seg.Buff.Count} sheet={seg.Sheet.Count}");
            try
            {
                // Thread-pool only: the main thread never blocks on a segment's write completion.
                await seg.Completion.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logWarn($"[CombatMeter.SP1] Segment blob writes FAULTED for {logId} ({ex.GetType().Name}: {ex.Message}) — attempting the tracks whose blobs exist");
            }
            await PostTrackGuarded(DmgEndpoint(baseUrl, region, levelUuid, "chunk"), SpoolCodec.TrackDmg, logId, seg.Dmg, store, logWarn).ConfigureAwait(false);
            await PostTrackGuarded(BuffEndpoint(baseUrl, region, levelUuid, "buff chunk"), SpoolCodec.TrackBuff, logId, seg.Buff, store, logWarn).ConfigureAwait(false);
            await PostTrackGuarded(SheetEndpoint(baseUrl, region, levelUuid, "sheet chunk"), SpoolCodec.TrackSheet, logId, seg.Sheet, store, logWarn).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logWarn($"[CombatMeter.SP1] Segment send aborted for {logId}: {ex.Message}");
        }
    }

    /// <summary>Re-upload leg for a retention container's stored chunk REFS (container V2). Splits by track
    /// so each posts to its own endpoint with its own per-track <c>total</c>; the container also stores the
    /// disk-only <c>buffx</c> refs (so the sweep keeps those blobs), and <see cref="SplitUploadable"/> drops
    /// them here. Thin fire-and-forget wrapper over <see cref="ReuploadRefsAsync"/>.</summary>
    internal static void ReuploadRefsFireAndForget(
        string baseUrl, string region, long levelUuid, string logId,
        IReadOnlyList<SpoolChunkRef> refs, Stellar.Abstractions.Services.IPluginDataStore store, Action<string> logWarn)
    {
        if (refs.Count == 0) return;
        _ = Task.Run(() => ReuploadRefsAsync(baseUrl, region, levelUuid, logId, refs, store, logWarn));
    }

    /// <summary>Task-returning core of <see cref="ReuploadRefsFireAndForget"/> — same guard shape as
    /// <see cref="UploadSegmentAsync"/> (per-track guard + outer belt), minus the Completion await (a
    /// retention container's refs are already resolved; there is nothing in flight to wait on).</summary>
    internal static async Task ReuploadRefsAsync(
        string baseUrl, string region, long levelUuid, string logId,
        IReadOnlyList<SpoolChunkRef> refs, Stellar.Abstractions.Services.IPluginDataStore store, Action<string> logWarn)
    {
        try
        {
            var (dmg, buff, sheet) = SplitUploadable(refs);
            await PostTrackGuarded(DmgEndpoint(baseUrl, region, levelUuid, "re-upload chunk"), SpoolCodec.TrackDmg, logId, dmg, store, logWarn).ConfigureAwait(false);
            await PostTrackGuarded(BuffEndpoint(baseUrl, region, levelUuid, "re-upload buff chunk"), SpoolCodec.TrackBuff, logId, buff, store, logWarn).ConfigureAwait(false);
            await PostTrackGuarded(SheetEndpoint(baseUrl, region, levelUuid, "re-upload sheet chunk"), SpoolCodec.TrackSheet, logId, sheet, store, logWarn).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logWarn($"[CombatMeter.SP1] Re-upload send aborted for {logId}: {ex.Message}");
        }
    }

    /// <summary>One track's guarded send: <see cref="PostRefsAsync"/> already catches per-CHUNK faults
    /// internally, but a track-level exception (thrown before its loop even starts, or from the await
    /// machinery itself) must not take the other two tracks down with it — each track gets its own
    /// try/catch so a failure is logged and the caller moves on to the next track.</summary>
    private static async Task PostTrackGuarded(
        TrackEndpoint ep, string track, string logId, IReadOnlyList<SpoolChunkRef> refs,
        Stellar.Abstractions.Services.IPluginDataStore store, Action<string> logWarn)
    {
        try
        {
            await PostRefsAsync(ep, logId, refs, store, logWarn).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logWarn($"[CombatMeter.SP1] {track} track send threw for {logId}: {ex.Message} — other tracks continue");
        }
    }

    /// <summary>Splits stored refs into the three UPLOADABLE tracks. Refs of the disk-only
    /// <see cref="SpoolCodec.TrackBuffRejected"/> track are DROPPED — the send filter rejected those rows, and
    /// a re-upload must not smuggle them to a server the first send withheld them from. Pure, so the "never
    /// uploaded" rule pins without an HTTP fake (EventSpoolTests.Rejected_buff_rows_are_never_uploaded).</summary>
    internal static (IReadOnlyList<SpoolChunkRef> Dmg, IReadOnlyList<SpoolChunkRef> Buff, IReadOnlyList<SpoolChunkRef> Sheet) SplitUploadable(
        IReadOnlyList<SpoolChunkRef> refs)
    {
        var dmg = new List<SpoolChunkRef>(refs.Count); var buff = new List<SpoolChunkRef>(); var sheet = new List<SpoolChunkRef>();
        foreach (var r in refs)
        {
            if (r.Track == SpoolCodec.TrackBuffRejected) continue;
            (r.Track == SpoolCodec.TrackBuff ? buff : r.Track == SpoolCodec.TrackSheet ? sheet : dmg).Add(r);
        }
        return (dmg, buff, sheet);
    }

    /// <summary>Posts one track's chunk refs: read the blob, gunzip it, wrap it in the envelope, POST. A
    /// missing blob or a failed POST skips that chunk only — later chunks continue. On an endpoint whose
    /// <see cref="TrackEndpoint.TerminalOn404"/> is set, a <b>404</b> is the route not existing on that
    /// server: terminal for the whole track, logged ONCE, blobs kept.</summary>
    internal static async Task PostRefsAsync(
        TrackEndpoint ep, string logId, IReadOnlyList<SpoolChunkRef> refs,
        Stellar.Abstractions.Services.IPluginDataStore store, Action<string> logWarn)
    {
        for (var i = 0; i < refs.Count; i++)
        {
            var r = refs[i];
            try
            {
                var gz = store.Read(r.BlobName);
                if (gz is null) { logWarn($"[CombatMeter.SP1] {ep.Label} {r.Index}/{refs.Count} for {logId}: blob {r.BlobName} missing — skipping."); continue; }
                var json = BuildEnvelope(logId, r, refs.Count, SpoolCodec.Gunzip(gz));
                var res = await PostAsync(ep.Url, json, ep.TerminalOn404).ConfigureAwait(false);
                if (res.NotFound)
                {
                    logWarn($"[CombatMeter.SP1] {r.Track} track not accepted by server (404) — blobs retained for re-upload.");
                    return;   // one line per segment, not per chunk; the rest of this track is pointless
                }
                if (!res.Ok)
                    logWarn($"[CombatMeter.SP1] {ep.Label} upload FAILED after retries (index {r.Index}/{refs.Count}) for {logId} — skipping; later chunks continue.");
            }
            catch (Exception ex)
            {
                logWarn($"[CombatMeter.SP1] {ep.Label} upload threw (index {r.Index}/{refs.Count}) for {logId}: {ex.Message} — skipping; later chunks continue.");
            }
        }
    }

    private static async Task UploadSequentialAsync(
        string baseUrl, string region, long levelUuid, string logId, List<EventChunk> chunks, Action<string> logWarn)
    {
        var url = BuildUrl(baseUrl, region, levelUuid);
        foreach (var chunk in chunks)
        {
            try
            {
                var json = BuildEnvelope(logId, chunk);
                var ok = await PostWithRetryAsync(url, json, terminalOn404: false).ConfigureAwait(false);
                if (!ok)
                    logWarn($"[CombatMeter.SP1] Chunk upload FAILED after retries (index {chunk.Index}/{chunk.Total}) for {logId} — skipping; later chunks continue.");
            }
            catch (Exception ex)
            {
                // Any unexpected failure (e.g. envelope build) must not abort the remaining chunks.
                logWarn($"[CombatMeter.SP1] Chunk upload threw (index {chunk.Index}/{chunk.Total}) for {logId}: {ex.Message} — skipping; later chunks continue.");
            }
        }
    }

    /// <summary>Outcome of a POST-with-retries. <c>NotFound</c> is carried separately from a plain failure
    /// because on the buff endpoint a 404 means the ROUTE does not exist on that server (a worker predating
    /// /buff-events) — terminal, not transient, so the caller stops the track instead of retrying it chunk by
    /// chunk. It is only ever set when the caller asked for that reading (<c>terminalOn404</c>).</summary>
    internal readonly struct PostOutcome
    {
        internal PostOutcome(bool ok, bool notFound) { Ok = ok; NotFound = notFound; }
        internal bool Ok { get; }
        internal bool NotFound { get; }
    }

    private static async Task<bool> PostWithRetryAsync(string url, string json, bool terminalOn404)
        => (await PostAsync(url, json, terminalOn404).ConfigureAwait(false)).Ok;

    private static async Task<PostOutcome> PostAsync(string url, string json, bool terminalOn404)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await HttpClient.PostAsync(url, content, CancellationToken.None).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return new PostOutcome(true, false);
                // 404 on the /buff-events endpoint = the endpoint itself is absent. Retrying cannot conjure a
                // route; the caller keeps the blobs so a later re-upload against an updated server still lands
                // them. On /events (terminalOn404 false) a 404 is just another failure and falls through to the
                // V1 retry ladder — that path has always retried it, and this change must not alter it.
                if (terminalOn404 && (int)response.StatusCode == 404) return new PostOutcome(false, true);
            }
            catch
            {
                // Network/transport error — fall through to the retry/backoff below.
            }

            if (attempt >= RetryDelays.Length) return new PostOutcome(false, false);
            await Task.Delay(RetryDelays[attempt]).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the per-chunk JSON envelope POSTed to <c>/run/{region}/{levelUuid}/events</c>:
    /// <c>{"logId":…,"index":…,"total":…,"startMs":…,"endMs":…,"count":…,"events":[…]}</c>.
    /// The <c>events</c> array rides <see cref="EventsJsonWriter"/> — the SAME event serialization
    /// the summary blob used to carry, so the wire shape of one event is byte-identical.
    /// </summary>
    internal static string BuildEnvelope(string logId, EventChunk chunk)
    {
        var w = new JsonWriter();
        w.BeginObject();
        w.Name("logId").Str(logId);
        w.Name("index").Number(chunk.Index);
        w.Name("total").Number(chunk.Total);
        w.Name("startMs").Number(chunk.StartMs);
        w.Name("endMs").Number(chunk.EndMs);
        w.Name("count").Number(chunk.Events.Count);
        w.Name("events").Raw(EventsJsonWriter.Write(chunk.Events));
        w.EndObject();
        return w.ToString();
    }

    /// <summary>Same envelope, built from a spool chunk REF plus the blob's already-serialized events JSON —
    /// the chunk's events never re-enter memory as <see cref="CombatLogEvent"/> objects. <paramref name="total"/>
    /// is the ref's own TRACK count (dmg and buff are numbered independently).</summary>
    internal static string BuildEnvelope(string logId, SpoolChunkRef r, int total, string eventsJson)
    {
        var w = new JsonWriter();
        w.BeginObject();
        w.Name("logId").Str(logId);
        w.Name("index").Number(r.Index);
        w.Name("total").Number(total);
        w.Name("startMs").Number(r.StartMs);
        w.Name("endMs").Number(r.EndMs);
        w.Name("count").Number(r.Count);
        w.Name("events").Raw(eventsJson);
        w.EndObject();
        return w.ToString();
    }
}
