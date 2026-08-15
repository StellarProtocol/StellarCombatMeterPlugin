using System;
using System.Collections.Generic;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>Pure link helpers for the Discord webhook feature. No I/O.</summary>
internal static class DiscordLink
{
    /// <summary>True only for an https Discord webhook URL — keeps the poster from becoming a
    /// generic POST-to-anywhere tool and catches typos.</summary>
    internal static bool IsValidWebhookUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        if (u.Host != "discord.com" && u.Host != "discordapp.com") return false;
        return u.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);
    }

    /// <summary>True iff the run URL ends in a base62 <c>shortId</c> (a non-digit in the last path
    /// segment). An all-digits last segment is the constructed <c>levelUuid</c> fallback, which opens
    /// the LATEST session (possibly a different run) — never shareable (owner ruling 2026-07-30).</summary>
    internal static bool IsShareable(string? runUrl)
    {
        if (string.IsNullOrWhiteSpace(runUrl)) return false;
        var slash = runUrl!.LastIndexOf('/');
        var seg = slash < 0 ? runUrl : runUrl.Substring(slash + 1);
        if (seg.Length == 0) return false;          // trailing slash, no segment at all => no shortId
        foreach (var c in seg)
            if (c < '0' || c > '9') return true;   // any non-digit => shortId
        return false;                              // all digits => levelUuid fallback
    }

    /// <summary>Most-recent (max <c>ArchivedAtMs</c>) candidate that is Done and shareable, else null.</summary>
    internal static string? PickShareable(IEnumerable<(long ArchivedAtMs, bool Done, string? Url)> candidates)
    {
        string? best = null;
        long bestMs = long.MinValue;
        foreach (var c in candidates)
            if (c.Done && c.ArchivedAtMs >= bestMs && IsShareable(c.Url))
            {
                best = c.Url;
                bestMs = c.ArchivedAtMs;
            }
        return best;
    }

    /// <summary>Display form of a webhook URL for the settings panel: drops the scheme, keeps the host +
    /// webhook id, and masks the secret token (it would leak in a shared screenshot). Returns the input
    /// unchanged if it is not an <c>/api/webhooks/</c> URL.</summary>
    internal static string MaskWebhook(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        const string marker = "/api/webhooks/";
        var i = url!.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return url;
        var head = url.Substring(0, i + marker.Length).Replace("https://", "").Replace("http://", "");
        var rest = url.Substring(i + marker.Length);          // {id}/{token}
        var slash = rest.IndexOf('/');
        if (slash < 0) return head + rest;                    // no token yet
        var id = rest.Substring(0, slash);
        var token = rest.Substring(slash + 1);
        var masked = token.Length <= 8 ? new string('•', token.Length)
            : token.Substring(0, 4) + "…" + token.Substring(token.Length - 4);
        return head + id + "/" + masked;
    }
}
