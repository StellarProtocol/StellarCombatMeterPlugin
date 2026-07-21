// Fire-and-forget HTTP upload of a gzip-compressed PositionUploadDoc JSON to the replay worker.

using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.CombatMeter.Replay;

/// <summary>
/// Uploads a serialized <see cref="PositionUploadDoc"/> JSON payload to the StellarLogs replay worker.
/// Gzip-compresses the body and uses a shared <see cref="HttpClient"/>.
/// Fire-and-forget: never blocks or throws on the Unity main thread.
/// </summary>
internal static class PositionUploader
{
    private const string BaseUrl = "https://api.stellarresonance.app/run/";

    // Single shared client (avoids socket exhaustion on repeated uploads).
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

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

        var url = string.Concat(
            BaseUrl, region, "/",
            doc.LevelUuid.ToString(CultureInfo.InvariantCulture),
            "/positions");

        // Fire off the actual HTTP on the thread-pool so the main thread is never blocked.
        _ = Task.Run(() => UploadAsync(json, url, onComplete));
    }

    /// <summary>Re-POST a pre-serialized positions body verbatim. Never throws.</summary>
    internal static void PostRawFireAndForget(string region, long levelUuid, string json, Action<bool, int, string?>? onComplete = null)
    {
        var url = string.Concat(BaseUrl, region, "/", levelUuid.ToString(CultureInfo.InvariantCulture), "/positions");
        _ = Task.Run(() => UploadAsync(json, url, onComplete));   // UploadAsync already gzips + POSTs
    }

    private static async Task UploadAsync(string json, string url, Action<bool, int, string?>? onComplete)
    {
        try
        {
            var gzipped = Gzip(json);
            using var content = new ByteArrayContent(gzipped);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            content.Headers.ContentEncoding.Add("gzip");

            using var response = await HttpClient.PostAsync(url, content, CancellationToken.None)
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
