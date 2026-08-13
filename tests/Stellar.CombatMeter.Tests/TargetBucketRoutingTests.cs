using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>Pure target-bucket precedence (Spec B, 2026-08-14-per-boss-statistics-design §3.1): a
/// tracked stage boss wins, else a tracked elite (its OWN store — elites never reach boss surfaces),
/// else <c>Other</c>. Pinned headless because <c>Plugin</c> cannot be instantiated in tests (repo
/// pattern — see <c>ImagineCastTests</c> on <c>ObserveBurstHit</c>).</summary>
public class TargetBucketRoutingTests
{
    [Theory]
    [InlineData(true, 102800, false, 0,      false, 102800)] // stage boss wins
    [InlineData(false, 0,     true,  55001,  true,  55001)]  // elite → elite store
    [InlineData(false, 0,     false, 0,      false, TargetBucketStats.OtherKey)]
    [InlineData(true, 102800, true,  55001,  false, 102800)] // boss beats elite (re-typed overlap)
    public void Route_precedence(bool boss, int bossId, bool elite, int eliteId, bool expectElite, int expectKey)
    {
        var (isElite, key) = Plugin.RouteTargetBucket(boss, bossId, elite, eliteId);
        Assert.Equal((expectElite, expectKey), (isElite, key));
    }

    /// <summary>No-loss invariant §7.2: an unrouted target is never dropped — it lands in the real
    /// <c>Other</c> bucket of the BOSS store (never the elite one, which would invent an elite).</summary>
    [Fact]
    public void Unknown_target_routes_to_Other_on_the_boss_store()
    {
        var (isElite, key) = Plugin.RouteTargetBucket(false, 0, false, 0);
        Assert.False(isElite);
        Assert.Equal(TargetBucketStats.OtherKey, key);
    }
}
