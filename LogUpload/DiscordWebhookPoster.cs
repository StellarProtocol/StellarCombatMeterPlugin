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

    /// <summary>Posts a run-card PNG as a multipart attachment (<c>payload_json</c> + <c>file</c>), the
    /// shape Discord requires for an image upload (proven: curl multipart → 200, renders inline). Same
    /// posture as <see cref="PostFireAndForget"/>: synchronous URL guard, then fire-and-forget on the
    /// thread pool, callback logs only. <paramref name="png"/> is captured/owned by the caller before this
    /// returns (it is not mutated here).</summary>
    internal static void PostImage(string? url, byte[] png, string payloadJson, string fileName = "card.png",
                                   Action<bool, int, string?>? onComplete = null)
    {
        if (!DiscordLink.IsValidWebhookUrl(url))
        {
            onComplete?.Invoke(false, 0, "invalid webhook url");
            return;
        }
        if (png is null || png.Length == 0)
        {
            onComplete?.Invoke(false, 0, "empty image");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(payloadJson, Encoding.UTF8, "application/json"), "payload_json");
                var file = new ByteArrayContent(png);
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                form.Add(file, "file", fileName);
                using var response = await HttpClient.PostAsync(url, form, CancellationToken.None).ConfigureAwait(false);
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
