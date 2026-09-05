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
    private static readonly HttpClient HttpClient = new()
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
                    if (!await PostWithRetryAsync(url, envelopeJsons[i]).ConfigureAwait(false))
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

    /// <summary>Uploads a rotated segment: dmg refs to /events, buff refs to /buff-events. Blobs are NOT
    /// deleted here — they belong to the retention container (Plugin.LogUpload's PersistReUpload) and die
    /// with it, so a re-upload can still replay them verbatim.</summary>
    internal static void UploadSegmentFireAndForget(
        string baseUrl, string region, long levelUuid, string logId, SpoolSegment seg,
        Stellar.Abstractions.Services.IPluginDataStore store, Action<string> logWarn)
    {
        if (seg.ChunkCount == 0) return;
        _ = Task.Run(async () =>
        {
            // Thread-pool only: the main thread never blocks on a segment's write completion.
            await seg.Completion.ConfigureAwait(false);
            await PostRefsAsync(BuildUrl(baseUrl, region, levelUuid), logId, seg.Dmg, store, logWarn, "chunk").ConfigureAwait(false);
            await PostRefsAsync(BuildBuffUrl(baseUrl, region, levelUuid), logId, seg.Buff, store, logWarn, "buff chunk").ConfigureAwait(false);
        });
    }

    /// <summary>Re-upload leg for a retention container's stored chunk REFS (container V2). Splits by track
    /// so each posts to its own endpoint with its own per-track <c>total</c>.</summary>
    internal static void ReuploadRefsFireAndForget(
        string baseUrl, string region, long levelUuid, string logId,
        IReadOnlyList<SpoolChunkRef> refs, Stellar.Abstractions.Services.IPluginDataStore store, Action<string> logWarn)
    {
        if (refs.Count == 0) return;
        _ = Task.Run(async () =>
        {
            var dmg = new List<SpoolChunkRef>(refs.Count);
            var buff = new List<SpoolChunkRef>();
            foreach (var r in refs) (r.Track == "buff" ? buff : dmg).Add(r);
            await PostRefsAsync(BuildUrl(baseUrl, region, levelUuid), logId, dmg, store, logWarn, "re-upload chunk").ConfigureAwait(false);
            await PostRefsAsync(BuildBuffUrl(baseUrl, region, levelUuid), logId, buff, store, logWarn, "re-upload buff chunk").ConfigureAwait(false);
        });
    }

    /// <summary>Posts one track's chunk refs: read the blob, gunzip it, wrap it in the envelope, POST. A
    /// missing blob or a failed POST skips that chunk only — later chunks continue. A <b>404</b> is the
    /// endpoint not existing on that server: terminal for the whole track, logged ONCE, blobs kept.</summary>
    internal static async Task PostRefsAsync(
        string url, string logId, IReadOnlyList<SpoolChunkRef> refs,
        Stellar.Abstractions.Services.IPluginDataStore store, Action<string> logWarn, string label)
    {
        for (var i = 0; i < refs.Count; i++)
        {
            var r = refs[i];
            try
            {
                var gz = store.Read(r.BlobName);
                if (gz is null) { logWarn($"[CombatMeter.SP1] {label} {r.Index}/{refs.Count} for {logId}: blob {r.BlobName} missing — skipping."); continue; }
                var json = BuildEnvelope(logId, r, refs.Count, SpoolCodec.Gunzip(gz));
                var res = await PostAsync(url, json).ConfigureAwait(false);
                if (res.NotFound)
                {
                    logWarn($"[CombatMeter.SP1] {r.Track} track not accepted by server (404) — blobs retained for re-upload.");
                    return;   // one line per segment, not per chunk; the rest of this track is pointless
                }
                if (!res.Ok)
                    logWarn($"[CombatMeter.SP1] {label} upload FAILED after retries (index {r.Index}/{refs.Count}) for {logId} — skipping; later chunks continue.");
            }
            catch (Exception ex)
            {
                logWarn($"[CombatMeter.SP1] {label} upload threw (index {r.Index}/{refs.Count}) for {logId}: {ex.Message} — skipping; later chunks continue.");
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
                var ok = await PostWithRetryAsync(url, json).ConfigureAwait(false);
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
    /// because a 404 means the ROUTE does not exist on that server (a worker predating /buff-events) — that
    /// is terminal, not transient, so the caller stops the track instead of retrying it chunk by chunk.</summary>
    internal readonly struct PostOutcome
    {
        internal PostOutcome(bool ok, bool notFound) { Ok = ok; NotFound = notFound; }
        internal bool Ok { get; }
        internal bool NotFound { get; }
    }

    private static async Task<bool> PostWithRetryAsync(string url, string json)
        => (await PostAsync(url, json).ConfigureAwait(false)).Ok;

    private static async Task<PostOutcome> PostAsync(string url, string json)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await HttpClient.PostAsync(url, content, CancellationToken.None).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return new PostOutcome(true, false);
                // 404 = the endpoint itself is absent. Retrying cannot conjure a route; the caller keeps the
                // blobs so a later re-upload against an updated server still lands them.
                if ((int)response.StatusCode == 404) return new PostOutcome(false, true);
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
