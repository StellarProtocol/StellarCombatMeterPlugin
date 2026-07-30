using System;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>Spec § 2.3: the cached kind map refreshes when older than 24h. Fire-and-forget — a stale
/// or absent map never blocks an archive or an upload, it just classifies as `other`.</summary>
public class ContentKindFetcherTests
{
    // The 24h-interval tests (IsStale) were RETIRED 2026-07-28: the fetch trigger is now the plugin
    // VERSION, not elapsed time — every request to a Worker route bills an invocation on Cloudflare, so
    // polling was pure waste for a table that changes about once per content patch. Replacement coverage
    // lives in ContentKindRefreshTriggerTests (NeedsFetch). DeliverResult's exactly-once contract below
    // is unaffected and still load-bearing.

    [Fact]
    public void DeliverResult_NoWarn_CallsOnResultOnceWithGivenValues_AndNeverCallsOnWarn()
    {
        var onWarnCalls = 0;
        var onResultCalls = 0;
        string? gotBody = null;
        string? gotEtag = null;

        ContentKindFetcher.DeliverResult("body", "etag", warn: null,
            onResult: (b, e) => { onResultCalls++; gotBody = b; gotEtag = e; },
            onWarn: _ => onWarnCalls++);

        Assert.Equal(0, onWarnCalls);
        Assert.Equal(1, onResultCalls);
        Assert.Equal("body", gotBody);
        Assert.Equal("etag", gotEtag);
    }

    [Fact]
    public void DeliverResult_WithWarn_CallsOnWarnOnce_AndOnResultStillExactlyOnce()
    {
        var onWarnCalls = 0;
        string? gotWarn = null;
        var onResultCalls = 0;

        ContentKindFetcher.DeliverResult(null, null, warn: "trouble",
            onResult: (_, _) => onResultCalls++,
            onWarn: w => { onWarnCalls++; gotWarn = w; });

        Assert.Equal(1, onWarnCalls);
        Assert.Equal("trouble", gotWarn);
        Assert.Equal(1, onResultCalls);
    }

    [Fact]
    public void DeliverResult_ThrowingOnResult_DoesNotThrow_AndOnResultRanExactlyOnce()
    {
        var onResultCalls = 0;

        var ex = Record.Exception(() => ContentKindFetcher.DeliverResult(
            "body", "etag", warn: null,
            onResult: (_, _) => { onResultCalls++; throw new InvalidOperationException("boom"); },
            onWarn: _ => { }));

        Assert.Null(ex);
        Assert.Equal(1, onResultCalls);
    }

    [Fact]
    public void DeliverResult_ThrowingOnWarn_DoesNotThrow_AndOnResultStillRunsExactlyOnce()
    {
        var onResultCalls = 0;

        var ex = Record.Exception(() => ContentKindFetcher.DeliverResult(
            null, null, warn: "trouble",
            onResult: (_, _) => onResultCalls++,
            onWarn: _ => throw new InvalidOperationException("boom")));

        Assert.Null(ex);
        Assert.Equal(1, onResultCalls);
    }

    [Fact]
    public void DeliverResult_BothDelegatesThrow_DoesNotThrow()
    {
        var ex = Record.Exception(() => ContentKindFetcher.DeliverResult(
            null, null, warn: "trouble",
            onResult: (_, _) => throw new InvalidOperationException("result boom"),
            onWarn: _ => throw new InvalidOperationException("warn boom")));

        Assert.Null(ex);
    }
}
