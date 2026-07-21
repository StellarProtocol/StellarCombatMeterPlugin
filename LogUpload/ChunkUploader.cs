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

    // 2 retries (3 attempts total) with 1s then 3s backoff between attempts.
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };

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

    private static async Task<bool> PostWithRetryAsync(string url, string json)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await HttpClient.PostAsync(url, content, CancellationToken.None).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
            }
            catch
            {
                // Network/transport error — fall through to the retry/backoff below.
            }

            if (attempt >= RetryDelays.Length) return false;
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
}
