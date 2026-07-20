// P2 (multi-uploader courtesy): the /upload response's merge verdict. `Kept=false` means this
// upload lost the server-side merge — its logId is not a segment's blob, so chunk uploads would
// all 400 ("unknown-log"); `HavePositions=true` means the matched segment already has a positions
// doc. `ShortUrl` is the server's public short run URL (spec § 9) when the response carried one —
// preferred over the client-constructed run URL for display/copy. Absent fields (old server)
// default to today's behavior: send everything; a null ShortUrl falls back to the constructed URL.

using System.Text.RegularExpressions;

namespace Stellar.CombatMeter.LogUpload;

internal sealed record UploadVerdict(bool Kept, bool HavePositions, string? ShortUrl = null)
{
    private static readonly Regex KeptFalse = new("\"kept\"\\s*:\\s*false", RegexOptions.Compiled);
    private static readonly Regex HavePosTrue = new("\"havePositions\"\\s*:\\s*true", RegexOptions.Compiled);
    // Non-empty string value only ([^"]+) — a `"shortUrl":""` (or absent field) yields no match ⇒ null.
    private static readonly Regex ShortUrlValue = new("\"shortUrl\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    internal static UploadVerdict Parse(string? body)
    {
        if (string.IsNullOrEmpty(body)) return new UploadVerdict(true, false);
        var m = ShortUrlValue.Match(body);
        return new UploadVerdict(
            Kept: !KeptFalse.IsMatch(body),
            HavePositions: HavePosTrue.IsMatch(body),
            ShortUrl: m.Success ? m.Groups[1].Value : null);
    }

    /// <summary>Human-facing StellarLogs site origin — the base every run-page URL hangs off (distinct
    /// from <see cref="LogUploader.ApiBase"/>, the machine API host). Single source of truth: reused
    /// both to build the client-constructed run URL (Plugin.LogUpload) and to absolutize a server-
    /// supplied relative short URL, so the two can never drift.</summary>
    internal const string SiteBase = "https://logs.stellarresonance.app";

    /// <summary>The URL to display/copy for a landed upload: the server's short run URL when the
    /// response provided one, else the client-constructed canonical run URL. A relative short URL
    /// (path-only worker responses, e.g. "/run/sea/xyz") is made absolute against <see cref="SiteBase"/>
    /// — the same origin the constructed URL uses; an already-absolute short URL passes through
    /// untouched (future worker change). Null/empty ⇒ fall back to the constructed URL.</summary>
    internal static string PreferredUrl(UploadVerdict? verdict, string constructedUrl)
    {
        var s = verdict?.ShortUrl;
        if (string.IsNullOrEmpty(s)) return constructedUrl;
        return s[0] == '/' ? SiteBase + s : s;
    }
}
