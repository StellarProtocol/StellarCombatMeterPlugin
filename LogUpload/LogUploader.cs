// SP1: Fire-and-forget HTTP upload of a gzip-compressed CombatLog JSON to StellarLogs.

using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Uploads a serialized <see cref="CombatLog"/> JSON payload to the StellarLogs service.
/// Gzip-compresses the body and uses a shared <see cref="HttpClient"/>.
/// Fire-and-forget: never blocks or throws on the Unity main thread.
/// </summary>
internal static class LogUploader
{
    // Ingestion worker base — shared with ChunkUploader (POST {ApiBase}/run/{levelUuid}/events
    // lives on the same worker as this /upload route).
    internal const string ApiBase = "https://api.stellarresonance.app";
    private const string UploadUrl = ApiBase + "/upload";

    // Single shared client (avoids socket exhaustion on repeated uploads).
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Serializes <paramref name="log"/> and posts it to the StellarLogs upload endpoint.
    /// Gzip-compresses the body. Any exception is swallowed — never crashes the game.
    /// <paramref name="onComplete"/> is invoked on a thread-pool thread (not Unity main thread)
    /// with (success, httpStatus, errorMessage, verdict).
    /// </summary>
    internal static void UploadFireAndForget(
        CombatLog log,
        Action<bool, int, string?, UploadVerdict?>? onComplete = null,
        int delayMs = 0)
    {
        // Serialize synchronously on the calling (main) thread — cheap; only called at archive.
        string json;
        try
        {
            json = CombatLogWriter.Write(log);
        }
        catch (Exception ex)
        {
            onComplete?.Invoke(false, 0, $"serialize error: {ex.Message}", null);
            return;
        }

        // Fire off the actual HTTP on the thread-pool so the main thread is never blocked.
        _ = Task.Run(() => UploadAsync(log, json, onComplete, delayMs));
    }

    /// <summary>P3 pre-check header — MUST mirror the worker's parsePrecheckHeader fields.
    /// eventCount is the SERIALIZED events count (0 on the chunked auto path): it must equal
    /// what the server's ingest would compute from this body (log.events.length), or the
    /// pre-check and the merge would disagree about who wins.</summary>
    internal static string BuildPrecheckHeader(CombatLog log)
    {
        var e = log.Header.Encounter;
        var truncated = log.Derived?.TruncatedEvents == true ? 1 : 0;
        return $"levelUuid={e.LevelUuid}; startMs={e.StartMs}; endMs={e.EndMs}; " +
               $"eventCount={log.Events.Count}; truncated={truncated}; result={e.Result}";
    }

    private static async Task UploadAsync(CombatLog log, string json, Action<bool, int, string?, UploadVerdict?>? onComplete, int delayMs = 0)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);

            var gzipped = Gzip(json);
            using var content = new ByteArrayContent(gzipped);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            content.Headers.ContentEncoding.Add("gzip");
            using var req = new HttpRequestMessage(HttpMethod.Post, UploadUrl) { Content = content };
            req.Headers.TryAddWithoutValidation("X-Stellar-Precheck", BuildPrecheckHeader(log));

            using var response = await HttpClient.SendAsync(req, CancellationToken.None).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                var okBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                onComplete?.Invoke(true, status, null, UploadVerdict.Parse(okBody));
            }
            else if (status == 409)
            {
                // Server: run already fully covered — send the ~KB supplement instead.
                var body409 = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var havePositions = body409.Contains("\"havePositions\":true");
                var supStatus = await PostSupplementAsync(log).ConfigureAwait(false);
                // kept:false — chunks are pointless either way; havePositions gates positions.
                onComplete?.Invoke(supStatus is >= 200 and < 300, supStatus,
                    supStatus is >= 200 and < 300 ? null : "supplement upload failed",
                    new UploadVerdict(false, havePositions));
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                onComplete?.Invoke(false, status, body, null);
            }
        }
        catch (Exception ex)
        {
            onComplete?.Invoke(false, 0, ex.Message, null);
        }
    }

    private static readonly TimeSpan[] SupplementRetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };

    /// <summary>POSTs the supplement; returns the final HTTP status (0 on transport failure).
    /// A 404 unknown-window is NOT retried — the run was reshaped between pre-check and now;
    /// the member's detail is then absent and the site shows the gearless-actor fallback.</summary>
    private static async Task<int> PostSupplementAsync(CombatLog log)
    {
        var url = $"{ApiBase}/run/{log.Header.Encounter.LevelUuid.ToString(System.Globalization.CultureInfo.InvariantCulture)}/supplement";
        var json = SupplementWriter.Write(log);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await HttpClient.PostAsync(url, content, CancellationToken.None).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode || status == 404) return status;
            }
            catch { /* transport error — retry below */ }
            if (attempt >= SupplementRetryDelays.Length) return 0;
            await Task.Delay(SupplementRetryDelays[attempt]).ConfigureAwait(false);
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
