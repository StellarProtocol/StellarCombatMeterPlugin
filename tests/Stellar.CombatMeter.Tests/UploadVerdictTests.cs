using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class UploadVerdictTests
{
    [Fact]
    public void Parse_NullOrEmpty_DefaultsToKeptTrue()
    {
        Assert.Equal(new UploadVerdict(true, false), UploadVerdict.Parse(null));
        Assert.Equal(new UploadVerdict(true, false), UploadVerdict.Parse(""));
    }

    [Fact]
    public void Parse_OldServerResponseWithoutFields_DefaultsToKeptTrue()
    {
        var body = "{\"ok\":true,\"levelUuid\":\"123\",\"runUrl\":\"/api/run/123\",\"deduped\":false}";
        Assert.Equal(new UploadVerdict(true, false), UploadVerdict.Parse(body));
    }

    [Fact]
    public void Parse_KeptFalse_HavePositionsTrue()
    {
        var body = "{\"ok\":true,\"deduped\":true,\"kept\":false,\"havePositions\":true}";
        Assert.Equal(new UploadVerdict(false, true), UploadVerdict.Parse(body));
    }

    [Fact]
    public void Parse_ToleratesWhitespace()
    {
        var body = "{ \"kept\" : false , \"havePositions\" : false }";
        Assert.Equal(new UploadVerdict(false, false), UploadVerdict.Parse(body));
    }

    // --- shortUrl (server's public short run URL, spec § 9) — additive; old servers omit it. ---

    [Fact]
    public void Parse_ExtractsShortUrl_WhenPresent()
    {
        var body = "{\"ok\":true,\"levelUuid\":\"123\",\"runUrl\":\"/api/run/sea/123\"," +
                   "\"shortId\":\"prqPvke7Gi\",\"shortUrl\":\"https://logs.stellarresonance.app/run/sea/prqPvke7Gi\"," +
                   "\"kept\":true,\"havePositions\":false}";
        var v = UploadVerdict.Parse(body);
        Assert.Equal("https://logs.stellarresonance.app/run/sea/prqPvke7Gi", v.ShortUrl);
        Assert.True(v.Kept);                 // shortUrl parse must not disturb kept …
        Assert.False(v.HavePositions);       // … or havePositions
    }

    [Fact]
    public void Parse_ShortUrl_Null_WhenAbsent()
    {
        // Old worker: response has no shortUrl field.
        var body = "{\"ok\":true,\"levelUuid\":\"123\",\"runUrl\":\"/api/run/sea/123\",\"kept\":true,\"havePositions\":false}";
        var v = UploadVerdict.Parse(body);
        Assert.Null(v.ShortUrl);
        Assert.True(v.Kept);
        Assert.False(v.HavePositions);
    }

    [Fact]
    public void Parse_ShortUrl_CoexistsWith_KeptFalse_HavePositionsTrue()
    {
        var body = "{\"ok\":true,\"kept\":false,\"havePositions\":true," +
                   "\"shortUrl\":\"https://logs.stellarresonance.app/run/jp/abc123XY\"}";
        Assert.Equal(new UploadVerdict(false, true, "https://logs.stellarresonance.app/run/jp/abc123XY"),
            UploadVerdict.Parse(body));
    }

    [Fact]
    public void Parse_EmptyShortUrl_TreatedAsAbsent()
    {
        var body = "{\"ok\":true,\"kept\":true,\"havePositions\":false,\"shortUrl\":\"\"}";
        Assert.Null(UploadVerdict.Parse(body).ShortUrl);
    }

    // --- Done-transition URL preference + normalization (the seam the upload callback uses to record
    //     the entry URL). Relative short URLs (path-only worker responses) are made absolute against
    //     the SAME SiteBase the constructed run URL uses; absolute short URLs pass through untouched. ---

    private const string Constructed = "https://logs.stellarresonance.app/run/sea/146960651154096128";

    [Fact]
    public void PreferredUrl_PassesThroughAbsoluteShortUrl()
    {
        var verdict = new UploadVerdict(true, false, "https://logs.stellarresonance.app/run/sea/prqPvke7Gi");
        Assert.Equal("https://logs.stellarresonance.app/run/sea/prqPvke7Gi",
            UploadVerdict.PreferredUrl(verdict, Constructed));
    }

    [Fact]
    public void PreferredUrl_NormalizesRelativeShortUrl_ToAbsoluteWithSiteBase()
    {
        var verdict = new UploadVerdict(true, false, "/run/sea/prqPvke7Gi");
        // Prefixed with the REAL base the plugin uses to build run URLs (asserted via the shared constant).
        Assert.Equal(UploadVerdict.SiteBase + "/run/sea/prqPvke7Gi",
            UploadVerdict.PreferredUrl(verdict, Constructed));
        Assert.Equal("https://logs.stellarresonance.app/run/sea/prqPvke7Gi",
            UploadVerdict.PreferredUrl(verdict, Constructed));   // and the concrete absolute form
    }

    [Fact]
    public void PreferredUrl_FullPath_RelativeBodyBecomesAbsolute()
    {
        // End-to-end: worker returns a PATH-ONLY shortUrl → Parse keeps it raw → PreferredUrl absolutizes.
        var body = "{\"ok\":true,\"kept\":true,\"havePositions\":false,\"shortUrl\":\"/run/jp/abc123XY\"}";
        Assert.Equal(UploadVerdict.SiteBase + "/run/jp/abc123XY",
            UploadVerdict.PreferredUrl(UploadVerdict.Parse(body), Constructed));
    }

    [Fact]
    public void PreferredUrl_FallsBackToConstructed_WhenShortUrlAbsentOrNoVerdict()
    {
        Assert.Equal(Constructed, UploadVerdict.PreferredUrl(new UploadVerdict(true, false), Constructed));  // absent
        Assert.Equal(Constructed, UploadVerdict.PreferredUrl(null, Constructed));                            // no verdict
    }

    [Fact]
    public void From409_ParsesShortUrl_KeptForcedFalse()
    {
        // REGRESSION PIN (owner report 2026-07-20, run 179048802794078208): the 409 verdict was
        // hand-built with ShortUrl=null, so every re-upload of an already-complete session overwrote
        // the entry's stored short link with the numeric fallback. Body below is the REAL production
        // precheck-409 response (post worker fix). Never weaken this pin.
        var body = "{\"want\":\"supplement\",\"havePositions\":true,\"shortId\":\"F3fef2yu9w\",\"shortUrl\":\"/run/sea/F3fef2yu9w\"}";
        var v = UploadVerdict.From409(body);
        Assert.False(v.Kept);                                  // forced: run already covered server-side
        Assert.True(v.HavePositions);
        Assert.Equal("/run/sea/F3fef2yu9w", v.ShortUrl);
        Assert.Equal(UploadVerdict.SiteBase + "/run/sea/F3fef2yu9w",
            UploadVerdict.PreferredUrl(v, Constructed));       // the stored URL flips back to short
    }

    [Fact]
    public void From409_OldServerBodyWithoutShortUrl_KeepsConstructed()
    {
        // Pre-fix worker body ({want,havePositions} only) — degrades exactly as before.
        var v = UploadVerdict.From409("{\"want\":\"supplement\",\"havePositions\":false}");
        Assert.False(v.Kept);
        Assert.False(v.HavePositions);
        Assert.Null(v.ShortUrl);
        Assert.Equal(Constructed, UploadVerdict.PreferredUrl(v, Constructed));
    }

    // REGRESSION PIN (2026-07-30) — a VERBATIM RE-UPLOAD must resolve the server's short URL, not the
    // numeric fallback. Owner: "click upload segment and upload all both return number". The cause was not
    // the server: tailing stellar-logs showed `upload verdict ... session=y shortId=HpPqOu76Bh` for all three
    // of the owner's segments. LogUploader.PostGzipJsonAsync simply THREW THE SUCCESS BODY AWAY
    // (onComplete(true, status, null)), so the re-upload path never saw shortUrl and stored the constructed
    // numeric URL over a perfectly good short link. Same failure class as the 409 path fixed on 2026-07-20;
    // this was the remaining hole. Body below is the worker's real 200 shape (src/worker/routes/upload.ts).
    [Fact]
    public void ReUploadSuccessBody_ResolvesToTheShortUrl_NotTheNumericFallback()
    {
        const string body =
            "{\"ok\":true,\"levelUuid\":721167069613129728,\"runUrl\":\"/api/run/sea/721167069613129728\"," +
            "\"shortId\":\"HpPqOu76Bh\",\"shortUrl\":\"/run/sea/HpPqOu76Bh\"," +
            "\"deduped\":true,\"kept\":true,\"havePositions\":false}";
        var numeric = "https://logs.stellarresonance.app/run/sea/721167069613129728";
        Assert.Equal("https://logs.stellarresonance.app/run/sea/HpPqOu76Bh",
            UploadVerdict.PreferredUrl(UploadVerdict.Parse(body), numeric));
    }

    // ...and a deduped re-upload is still a landed upload carrying a short id, so it must NOT downgrade.
    [Fact]
    public void A_deduped_reupload_still_keeps_the_short_url()
    {
        const string body = "{\"ok\":true,\"shortUrl\":\"/run/sea/gsSR9PyvxA\",\"deduped\":true,\"kept\":false}";
        Assert.Equal("https://logs.stellarresonance.app/run/sea/gsSR9PyvxA",
            UploadVerdict.PreferredUrl(UploadVerdict.Parse(body), "https://logs.stellarresonance.app/run/sea/584088755955040256"));
    }
}
