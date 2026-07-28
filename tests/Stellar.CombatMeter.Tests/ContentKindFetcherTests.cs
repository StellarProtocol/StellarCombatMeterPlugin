using System;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>Spec § 2.3: the cached kind map refreshes when older than 24h. Fire-and-forget — a stale
/// or absent map never blocks an archive or an upload, it just classifies as `other`.</summary>
public class ContentKindFetcherTests
{
    [Fact]
    public void NeverFetched_IsStale()
        => Assert.True(ContentKindFetcher.IsStale(fetchedAtMs: 0, nowMs: 1));

    [Fact]
    public void JustFetched_IsFresh()
        => Assert.False(ContentKindFetcher.IsStale(fetchedAtMs: 1_000_000, nowMs: 1_000_000));

    [Fact]
    public void WithinTwentyFourHours_IsFresh()
        => Assert.False(ContentKindFetcher.IsStale(1_000_000, 1_000_000 + ContentKindFetcher.RefreshIntervalMs - 1));

    [Fact]
    public void AtOrBeyondTwentyFourHours_IsStale()
    {
        Assert.True(ContentKindFetcher.IsStale(1_000_000, 1_000_000 + ContentKindFetcher.RefreshIntervalMs));
        Assert.True(ContentKindFetcher.IsStale(1_000_000, 1_000_000 + ContentKindFetcher.RefreshIntervalMs * 3));
    }

    [Fact]
    public void ClockWentBackwards_IsStale_SoAWrongClockCannotPinAStaleMapForever()
        => Assert.True(ContentKindFetcher.IsStale(fetchedAtMs: 5_000_000, nowMs: 1_000));

    // --- DeliverResult: the "onResult exactly once, on every path" invariant (review finding) ---

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
