using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Owner ruling 2026-07-28 (superseding spec § 2.3's 24h refresh): the kind map is fetched ONCE, cached
/// in prefs, and re-fetched only when the plugin VERSION changes — plus a manual refresh in the settings
/// pane. Rationale: on Cloudflare every request to a Worker route invokes (and bills) the Worker, and a
/// `Cache-Control` header does not avoid that. A 24h interval cost one request per install per day for a
/// table that changes about once per content patch; this costs ZERO in steady state.
///
/// Owner, verbatim: *"it costly when we use cloudflare as infra(waste request count)"* and *"why can't
/// user just download whole mapping data at first and cache inside the plugin? to prevent multiple
/// calling"*.
///
/// The accepted trade-off: between a content patch and the next plugin release, new content classifies as
/// `other`. Invisible under all-auto defaults; the manual refresh is the escape hatch.
/// </summary>
public class ContentKindRefreshTriggerTests
{
    [Fact]
    public void NeedsFetch_WhenNoMapHasEverBeenCached()
    {
        // First run, or the prefs cache was lost. Version match is irrelevant — there is nothing to use.
        Assert.True(ContentKindFetcher.NeedsFetch(cachedVersion: "1.1.0", currentVersion: "1.1.0", mapIsEmpty: true));
    }

    [Fact]
    public void DoesNotFetch_InSteadyState()
    {
        // THE point of the change: same plugin version + a usable cached map ⇒ zero requests, forever.
        Assert.False(ContentKindFetcher.NeedsFetch(cachedVersion: "1.1.0", currentVersion: "1.1.0", mapIsEmpty: false));
    }

    [Fact]
    public void NeedsFetch_WhenThePluginVersionChanged()
    {
        // A plugin update is the refresh trigger — it is how a content-patch taxonomy change reaches
        // installs without any polling.
        Assert.True(ContentKindFetcher.NeedsFetch(cachedVersion: "1.1.0", currentVersion: "1.2.0", mapIsEmpty: false));
        Assert.True(ContentKindFetcher.NeedsFetch(cachedVersion: "1.2.0", currentVersion: "1.1.0", mapIsEmpty: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NeedsFetch_WhenTheCachedVersionIsMissing(string? cached)
    {
        // A cache written by the 24h-interval build carries no version stamp. Re-fetch once to stamp it,
        // then it settles into the zero-request steady state.
        Assert.True(ContentKindFetcher.NeedsFetch(cachedVersion: cached, currentVersion: "1.1.0", mapIsEmpty: false));
    }

    [Fact]
    public void NeedsFetch_WhenTheCurrentVersionIsUnknown()
    {
        // Assembly version unreadable — prefer a fetch over silently pinning a possibly-stale map.
        Assert.True(ContentKindFetcher.NeedsFetch(cachedVersion: "1.1.0", currentVersion: null, mapIsEmpty: false));
    }

    [Fact]
    public void VersionComparisonIsExact_NotOrdered()
    {
        // Any difference triggers a fetch; there is no notion of "newer". A downgrade (rollback to a .bak
        // build) must re-fetch too, because the older build's map may predate a taxonomy fix.
        Assert.True(ContentKindFetcher.NeedsFetch("1.1.0", "1.1.0-dev", mapIsEmpty: false));
        Assert.False(ContentKindFetcher.NeedsFetch("1.1.0-dev", "1.1.0-dev", mapIsEmpty: false));
    }
}
