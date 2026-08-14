// Fire-and-forget HTTP upload of a gzip-compressed PositionUploadDoc JSON to the replay worker.

using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter.Replay;

/// <summary>
/// Uploads a serialized <see cref="PositionUploadDoc"/> JSON payload to the StellarLogs replay worker.
/// Gzip-compresses the body and uses a shared <see cref="HttpClient"/>.
/// Fire-and-forget: never blocks or throws on the Unity main thread.
/// Bounded per-doc retries (2, 1s/3s backoff) — the same transport policy <c>ChunkUploader</c> has
/// (parity fix 2026-08-14: positions used to get exactly ONE attempt while summaries and chunks
/// retried, so a transient blip permanently lost a banked replay window).
/// </summary>
internal static class PositionUploader
{
    /// <summary>Builds the positions URL: <c>{apiBase}/run/{region}/{levelUuid}/positions</c>. The base is
    /// <see cref="LogUploader.ApiBase"/> — the ONE effective ingestion base (config-overridable via
    /// <c>uploadApiBase</c>), never a second hardcoded host: a replay landing on a different backend than
    /// its own summary is worse than either backend alone.</summary>
    internal static string BuildUrl(string apiBase, string region, long levelUuid)
        => string.Concat(apiBase, "/run/", region, "/", levelUuid.ToString(CultureInfo.InvariantCulture), "/positions");

    // Single shared client (avoids socket exhaustion on repeated uploads).
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    // 2 retries (3 attempts total) with 1s then 3s backoff between attempts — byte-identical to
    // ChunkUploader.RetryDelays (the parity model; pinned by PositionUploaderRetryTests). Bounded by
    // construction (array-indexed via NextRetryDelay) — never an unbounded loop (hard rule).
    internal static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };

    /// <summary>Pure retry-policy seam: the backoff to wait after 0-based failed attempt
    /// <paramref name="attempt"/> before trying again, or <c>null</c> when the attempts are exhausted
    /// (<see cref="RetryDelays"/>.Length retries = Length+1 attempts total). Pinned by
    /// <c>PositionUploaderRetryTests</c> — never weaken to one-shot and never make it unbounded.</summary>
    internal static TimeSpan? NextRetryDelay(int attempt)
        => attempt < RetryDelays.Length ? RetryDelays[attempt] : null;

    /// <summary>
    /// Serializes <paramref name="doc"/> and posts it to the replay positions endpoint.
    /// Endpoint: <c>POST /run/{region}/{levelUuid}/positions</c>.
    /// Gzip-compresses the body. Any exception is swallowed — never crashes the game.
    /// <paramref name="onComplete"/> is invoked on a thread-pool thread (not Unity main thread)
    /// with (success, httpStatus, errorMessage).
    /// </summary>
    internal static void UploadFireAndForget(
        string region,
        PositionUploadDoc doc,
        Action<bool, int, string?>? onComplete = null)
    {
        // Serialize synchronously on the calling (main) thread — cheap; only called at archive.
        string json;
        try
        {
            json = PositionJsonWriter.Write(doc);
        }
        catch (Exception ex)
        {
            onComplete?.Invoke(false, 0, $"serialize error: {ex.Message}");
            return;
        }

        var url = BuildUrl(LogUploader.ApiBase, region, doc.LevelUuid);

        // Fire off the actual HTTP on the thread-pool so the main thread is never blocked.
        _ = Task.Run(() => UploadAsync(json, url, onComplete));
    }

    /// <summary>Re-POST a pre-serialized positions body verbatim. Never throws.</summary>
    internal static void PostRawFireAndForget(string region, long levelUuid, string json, Action<bool, int, string?>? onComplete = null)
    {
        var url = BuildUrl(LogUploader.ApiBase, region, levelUuid);
        _ = Task.Run(() => UploadAsync(json, url, onComplete));   // UploadAsync already gzips + POSTs
    }

    // Bounded retry loop around PostOnceAsync — mirrors ChunkUploader.PostWithRetryAsync (any
    // failure, transport or non-2xx, is retried until RetryDelays is exhausted). onComplete fires
    // exactly once, with the FINAL attempt's status/error.
    private static async Task UploadAsync(string json, string url, Action<bool, int, string?>? onComplete)
    {
        try
        {
            var gzipped = Gzip(json);
            var status = 0;
            string? err = null;
            for (var attempt = 0; ; attempt++)
            {
                bool ok;
                (ok, status, err) = await PostOnceAsync(url, gzipped).ConfigureAwait(false);
                if (ok) { onComplete?.Invoke(true, status, null); return; }
                if (NextRetryDelay(attempt) is not { } delay) break;   // exhausted — bounded, never loops forever
                await Task.Delay(delay).ConfigureAwait(false);
            }
            onComplete?.Invoke(false, status, err);
        }
        catch (Exception ex)
        {
            onComplete?.Invoke(false, 0, ex.Message);
        }
    }

    // One POST attempt. Never throws — a transport error maps to status 0 — so the retry loop above
    // stays in control of every failure mode.
    private static async Task<(bool Ok, int Status, string? Err)> PostOnceAsync(string url, byte[] gzipped)
    {
        try
        {
            using var content = new ByteArrayContent(gzipped);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            content.Headers.ContentEncoding.Add("gzip");

            using var response = await HttpClient.PostAsync(url, content, CancellationToken.None)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) return (true, status, null);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (false, status, body);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    private static byte[] Gzip(string input)
    {
        var raw = Encoding.UTF8.GetBytes(input);
        using var ms = new MemoryStream(raw.Length);
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }
}
