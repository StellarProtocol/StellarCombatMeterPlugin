using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>Fire-and-forget POST of a portrait batch to StellarLogs. Never blocks or throws
/// on the Unity main thread; onComplete fires on a thread-pool thread with (success, status).</summary>
internal static class PortraitUploader
{
    private const string Url = "https://stellar-logs.boshido.workers.dev/char/portraits";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    internal static void UploadFireAndForget(string bodyJson, Action<bool, int>? onComplete = null)
        => _ = Task.Run(() => UploadAsync(bodyJson, onComplete));

    private static async Task UploadAsync(string bodyJson, Action<bool, int>? onComplete)
    {
        try
        {
            using var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            using var response = await HttpClient.PostAsync(Url, content, CancellationToken.None).ConfigureAwait(false);
            onComplete?.Invoke(response.IsSuccessStatusCode, (int)response.StatusCode);
        }
        catch
        {
            onComplete?.Invoke(false, 0);
        }
    }
}
