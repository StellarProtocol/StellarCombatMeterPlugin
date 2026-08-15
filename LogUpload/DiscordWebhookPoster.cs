using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>Posts a prebuilt Discord webhook JSON body to the user's own webhook. Same posture as the
/// other LogUpload/ uploaders: one shared <see cref="HttpClient"/>, fire-and-forget on the thread pool,
/// the callback only logs (never touches uGUI). No retries — a failed/429 post logs once and drops.</summary>
internal static class DiscordWebhookPoster
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    internal static void PostFireAndForget(string? url, string jsonBody, Action<bool, int, string?>? onComplete = null)
    {
        if (!DiscordLink.IsValidWebhookUrl(url))
        {
            onComplete?.Invoke(false, 0, "invalid webhook url");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                using var response = await HttpClient.PostAsync(url, content, CancellationToken.None).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode) onComplete?.Invoke(true, status, null);
                else onComplete?.Invoke(false, status, response.ReasonPhrase);
            }
            catch (Exception ex)
            {
                onComplete?.Invoke(false, 0, ex.Message);
            }
        });
    }
}
