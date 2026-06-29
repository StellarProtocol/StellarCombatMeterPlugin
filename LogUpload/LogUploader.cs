// UNVERIFIED — this code has never been executed in-game.
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
    private const string UploadUrl = "https://stellar-logs.boshido.workers.dev/upload";

    // Single shared client (avoids socket exhaustion on repeated uploads).
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Serializes <paramref name="log"/> and posts it to the StellarLogs upload endpoint.
    /// Gzip-compresses the body. Any exception is swallowed — never crashes the game.
    /// <paramref name="onComplete"/> is invoked on a thread-pool thread (not Unity main thread)
    /// with (success, httpStatus, errorMessage).
    /// </summary>
    internal static void UploadFireAndForget(
        CombatLog log,
        Action<bool, int, string?>? onComplete = null)
    {
        // Serialize synchronously on the calling (main) thread — cheap; only called at archive.
        string json;
        try
        {
            json = CombatLogWriter.Write(log);
        }
        catch (Exception ex)
        {
            onComplete?.Invoke(false, 0, $"serialize error: {ex.Message}");
            return;
        }

        // Fire off the actual HTTP on the thread-pool so the main thread is never blocked.
        _ = Task.Run(() => UploadAsync(json, onComplete));
    }

    private static async Task UploadAsync(string json, Action<bool, int, string?>? onComplete)
    {
        try
        {
            var gzipped = Gzip(json);
            using var content = new ByteArrayContent(gzipped);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            content.Headers.ContentEncoding.Add("gzip");

            using var response = await HttpClient.PostAsync(UploadUrl, content, CancellationToken.None)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                onComplete?.Invoke(true, status, null);
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                onComplete?.Invoke(false, status, body);
            }
        }
        catch (Exception ex)
        {
            onComplete?.Invoke(false, 0, ex.Message);
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
