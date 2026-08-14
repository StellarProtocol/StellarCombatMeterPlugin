using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>Fire-and-forget POST of a portrait batch to StellarLogs. Never blocks or throws
/// on the Unity main thread; onComplete fires on a thread-pool thread with (success, status, body).</summary>
internal static class PortraitUploader
{
    /// <summary>Builds the portrait-batch URL: <c>{apiBase}/char/portraits</c>. Built from
    /// <see cref="LogUploader.ApiBase"/> (config-overridable via <c>uploadApiBase</c>) so a
    /// staging-pointed build sends portraits to the same backend as its runs.</summary>
    internal static string BuildUrl(string apiBase) => apiBase + "/char/portraits";

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    internal static void UploadFireAndForget(string bodyJson, Action<bool, int, string?>? onComplete = null)
        => _ = Task.Run(() => UploadAsync(bodyJson, onComplete));

    private static async Task UploadAsync(string bodyJson, Action<bool, int, string?>? onComplete)
    {
        try
        {
            using var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            using var response = await HttpClient.PostAsync(BuildUrl(LogUploader.ApiBase), content, CancellationToken.None).ConfigureAwait(false);
            string? respBody = null;
            try { respBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { /* body optional */ }
            onComplete?.Invoke(response.IsSuccessStatusCode, (int)response.StatusCode, respBody);
        }
        catch
        {
            onComplete?.Invoke(false, 0, null);
        }
    }
}
