using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Fetches the site's mapId→kind map (spec § 2.3). Fire-and-forget with a conditional GET: a 304 or
/// any failure leaves the cached map in place. Never blocks an archive or an upload; the plugin
/// classifies content as <c>other</c> until a map arrives, which with all-Auto defaults is
/// behaviour-identical to today.
///
/// Same HTTP posture as <see cref="LogUploader"/>: one shared <see cref="HttpClient"/>, the request
/// runs on the thread pool, and nothing propagates to the caller.
/// </summary>
internal static class ContentKindFetcher
{
    /// <summary>Refresh when the cache is older than this (spec § 2.3: 24h).</summary>
    internal const long RefreshIntervalMs = 86_400_000;

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Cache age check. A never-fetched cache (0) and a backwards clock both read as stale, so
    /// a wrong system clock can never pin a stale map forever.</summary>
    internal static bool IsStale(long fetchedAtMs, long nowMs)
        => fetchedAtMs <= 0 || nowMs < fetchedAtMs || nowMs - fetchedAtMs >= RefreshIntervalMs;

    /// <summary>
    /// Conditional GET of <c>{apiBase}/api/site/content-kinds</c>. Invokes
    /// <paramref name="onResult"/> exactly once on a thread-pool thread: <c>(body, etag)</c> on a 200,
    /// <c>(null, null)</c> on 304 / non-success / transport failure — meaning "keep the cache".
    /// </summary>
    internal static void FetchFireAndForget(string apiBase, string? etag,
                                           Action<string?, string?> onResult, Action<string> onWarn)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, apiBase + "/api/site/content-kinds");
                if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);

                using var res = await HttpClient.SendAsync(req).ConfigureAwait(false);
                if (res.StatusCode == HttpStatusCode.NotModified) { onResult(null, null); return; }
                if (!res.IsSuccessStatusCode)
                {
                    onWarn($"[CombatMeter.SP1] content-kinds fetch failed (HTTP {(int)res.StatusCode}) — keeping cached map.");
                    onResult(null, null);
                    return;
                }

                var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                onResult(body, res.Headers.ETag?.Tag);
            }
            catch (Exception ex)
            {
                // Offline / DNS / timeout: entirely expected, and harmless — the cached (or empty) map stands.
                onWarn($"[CombatMeter.SP1] content-kinds fetch threw: {ex.Message} — keeping cached map.");
                onResult(null, null);
            }
        });
    }
}
