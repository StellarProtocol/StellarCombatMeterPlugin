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
}
