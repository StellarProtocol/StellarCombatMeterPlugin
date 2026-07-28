using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>Spec test 3 — an unknown / unresolved mapId resolves to `other`, and the endpoint payload
/// parses with the IL2CPP-safe hand-rolled reader (no System.Text.Json).</summary>
public class ContentKindMapTests
{
    // Trimmed real payload shape from GET /api/site/content-kinds.
    private const string Payload =
        "{\"version\":1,\"kinds\":{\"dungeon\":[1150,1151,1152],\"raid\":[13021,13022,13023]," +
        "\"worldboss\":[7150,7151,7152],\"other\":[]}}";

    [Fact]
    public void TryParse_ClassifiesEveryKindFromTheEndpointPayload()
    {
        Assert.True(ContentKindMap.TryParse(Payload, out var map));
        Assert.Equal(ContentKind.Dungeon,   map.KindOf(1151));
        Assert.Equal(ContentKind.Raid,      map.KindOf(13022));
        Assert.Equal(ContentKind.WorldBoss, map.KindOf(7152));
    }

    [Theory]
    [InlineData(0)]        // unparseable scene name → mapId 0
    [InlineData(99999)]    // content the site does not rank
    [InlineData(-1)]
    public void KindOf_UnknownMapId_IsOther(int mapId)
    {
        Assert.True(ContentKindMap.TryParse(Payload, out var map));
        Assert.Equal(ContentKind.Other, map.KindOf(mapId));
    }

    [Fact]
    public void Empty_ClassifiesEverythingAsOther_SoAnUnreachableEndpointDegradesSafely()
    {
        Assert.True(ContentKindMap.Empty.IsEmpty);
        Assert.Equal(ContentKind.Other, ContentKindMap.Empty.KindOf(1151));
        Assert.Equal(ContentKind.Other, ContentKindMap.Empty.KindOf(0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"version\":1}")]                    // no kinds object
    [InlineData("{\"version\":1,\"kinds\":{}}")]       // kinds present but empty
    public void TryParse_GarbageOrEmpty_ReturnsFalse(string? json)
    {
        Assert.False(ContentKindMap.TryParse(json, out var map));
        // Always yields a usable all-Other map so callers never null-check.
        Assert.Equal(ContentKind.Other, map.KindOf(1151));
    }

    [Fact]
    public void Ids_RoundTripsThroughThePrefsArrayForm()
    {
        Assert.True(ContentKindMap.TryParse(Payload, out var parsed));
        var revived = ContentKindMap.FromIds(
            parsed.Ids(ContentKind.Dungeon),
            parsed.Ids(ContentKind.Raid),
            parsed.Ids(ContentKind.WorldBoss));

        Assert.Equal(ContentKind.Dungeon,   revived.KindOf(1150));
        Assert.Equal(ContentKind.Raid,      revived.KindOf(13021));
        Assert.Equal(ContentKind.WorldBoss, revived.KindOf(7150));
        Assert.Equal(ContentKind.Other,     revived.KindOf(99999));
    }

    [Fact]
    public void FromIds_NullArrays_YieldAnEmptyMap()
        => Assert.True(ContentKindMap.FromIds(null, null, null).IsEmpty);
}
