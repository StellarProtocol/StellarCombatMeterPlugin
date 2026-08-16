using System;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Client side of the upload compatibility floor (server: services/stellar-logs src/uploadCompat.ts +
/// GET /api/upload/compat). Builds below the floor split/mislabel a run at the source, so the server
/// returns 426 for their /upload. Rather than let the player discover that only when a run-end upload
/// fails, the plugin asks the endpoint for the floor at startup (when auto-upload is on) and compares
/// its own version — surfacing an "update" notice and withholding the send when below.
///
/// FAIL-OPEN is the invariant everywhere here: an unreachable/garbled endpoint, an unpublished route
/// (404), or an unreadable own version must never nag the player or withhold a send. Same HTTP posture
/// as <see cref="ContentKindFetcher"/>: one shared <see cref="HttpClient"/>, thread-pool fetch, nothing
/// propagates to the caller.
/// </summary>
internal static class UploadCompat
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly Regex MinVerRx = new("\"minPluginVer\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex MessageRx = new("\"message\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="currentVer"/> is strictly below <paramref name="minVer"/>, comparing the
    /// first three components numerically (so <c>2.10.0</c> &gt; <c>2.2.1</c>, which a string compare gets
    /// wrong). The floor is inclusive — a version equal to it is allowed. Fails OPEN (returns
    /// <see langword="false"/>) if EITHER value is absent or unparseable, so we never withhold/nag on a
    /// value we could not read.
    /// </summary>
    internal static bool IsBelowFloor(string? currentVer, string? minVer)
    {
        var cur = Parse(currentVer);
        var min = Parse(minVer);
        if (cur is null || min is null) return false;
        for (var i = 0; i < 3; i++)
        {
            if (cur[i] < min[i]) return true;
            if (cur[i] > min[i]) return false;
        }
        return false; // equal — inclusive floor
    }

    /// <summary>Parses a version to <c>[major, minor, patch]</c>, tolerating the 4-part assembly form
    /// (<c>major.minor.patch.build</c>) by taking the first three. Null for absent/empty/non-numeric.</summary>
    private static int[]? Parse(string? v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        var parts = v.Split('.');
        if (parts.Length < 3) return null;
        var nums = new int[3];
        for (var i = 0; i < 3; i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out nums[i]) || nums[i] < 0)
                return null;
        return nums;
    }

    /// <summary>Extracts <c>minPluginVer</c> (required) and <c>message</c> (optional) from the compat
    /// response body. Regex, dependency-free — matching the plugin's other small-response parsers
    /// (<c>UploadVerdict.Parse</c>). Returns <see langword="false"/> when no usable floor is present, so a
    /// garbled/empty body fails open at the call site.</summary>
    internal static bool TryParseCompat(string? json, out string? minPluginVer, out string? message)
    {
        minPluginVer = null;
        message = null;
        if (string.IsNullOrEmpty(json)) return false;
        var m = MinVerRx.Match(json);
        if (!m.Success || string.IsNullOrEmpty(m.Groups[1].Value)) return false;
        minPluginVer = m.Groups[1].Value;
        var msg = MessageRx.Match(json);
        if (msg.Success) message = msg.Groups[1].Value;
        return true;
    }

    /// <summary>
    /// GET <c>{apiBase}/api/upload/compat</c>. Invokes <paramref name="onResult"/> exactly once on a
    /// thread-pool thread: <c>(minPluginVer, message)</c> on a parseable 200, <c>(null, null)</c> on any
    /// failure (404 / non-success / unparseable / transport) — meaning "could not determine, fail open".
    /// </summary>
    internal static void FetchFireAndForget(string apiBase,
                                           Action<string?, string?> onResult, Action<string> onWarn)
    {
        _ = Task.Run(async () =>
        {
            string? minVer = null;
            string? message = null;
            string? warn = null;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, apiBase + "/api/upload/compat");
                using var res = await HttpClient.SendAsync(req).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    // 404 (route not deployed yet) or any other non-2xx: fail open, quietly.
                    warn = $"[CombatMeter.SP1] upload-compat check skipped (HTTP {(int)res.StatusCode}).";
                }
                else
                {
                    var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!TryParseCompat(body, out minVer, out message))
                        warn = "[CombatMeter.SP1] upload-compat response unparseable — assuming compatible.";
                }
            }
            catch (Exception ex)
            {
                // Offline / DNS / timeout: expected and harmless — assume compatible.
                warn = $"[CombatMeter.SP1] upload-compat check threw: {ex.Message} — assuming compatible.";
            }

            // Deliver OUTSIDE the try/catch and guard each delegate, so a throwing callback can neither
            // re-enter this method nor suppress the other callback (mirrors ContentKindFetcher.DeliverResult).
            if (warn != null)
            {
                try { onWarn(warn); } catch { /* thread-pool fire-and-forget: no higher handler */ }
            }
            try { onResult(minVer, message); } catch { /* onResult already delivered — never re-interpret as undelivered */ }
        });
    }
}
