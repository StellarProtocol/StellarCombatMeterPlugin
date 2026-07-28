using System;
using System.Threading.Tasks;
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

    // --- Regression: truncated/malformed "kinds" payloads must terminate, never hang. ----------------
    //
    // TryReadKinds' id-array loop (Stellar.CombatMeter.LogUpload.ContentKindMap.TryReadKinds) has a
    // single guard — `if (t != JsonTokenKind.Number) return false;` — that is the ONLY thing standing
    // between a malformed/truncated payload and an infinite loop. HistoryJsonReader.Next() never throws
    // and never advances past Eof/an unrecognised character: once the input runs out or the tokenizer
    // sees an unparseable byte, every subsequent Next() call returns the same sentinel token (Eof or
    // Error) forever at the same string position. Without the guard, the array loop happily calls
    // `target?.Add((int)r.NumberValue)` on that sentinel and spins calling Next() again — forever. That
    // would be a silent infinite loop while parsing a network response on a game client.
    //
    // These payloads specifically target that loop (truncated inside an id array, or a malformed token
    // inside one) plus a few adjacent malformed shapes for coverage. Every case must both return `false`
    // AND terminate quickly.

    /// <summary>
    /// Generous ceiling for <see cref="ContentKindMap.TryParse"/> on a small hand-crafted payload — real
    /// runs finish in well under a millisecond. Only a genuine infinite loop would ever approach this.
    /// </summary>
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs <see cref="ContentKindMap.TryParse"/> on a background <see cref="Task"/> and asserts it
    /// completes within <paramref name="timeout"/> before looking at the result. This indirection exists
    /// ONLY because of the infinite-loop risk documented above: if the id-array guard is ever removed or
    /// weakened, calling <c>ContentKindMap.TryParse</c> directly from a test body would not fail — it
    /// would hang the test (and wedge the whole CI run) forever, since the reader spins without ever
    /// throwing or returning. Wrapping the call in a bounded <see cref="Task.Wait(TimeSpan)"/> turns that
    /// failure mode into an ordinary red assertion instead of a stuck build. Do NOT "simplify" this back
    /// to a direct call — that would silently remove the regression coverage this test exists to provide.
    /// </summary>
    private static bool ParseWithTimeout(string? json, out ContentKindMap map, TimeSpan timeout)
    {
        ContentKindMap result = ContentKindMap.Empty;
        var ok = false;
        var task = Task.Run(() => { ok = ContentKindMap.TryParse(json, out result); });
        Assert.True(task.Wait(timeout), "ContentKindMap.TryParse did not terminate within the timeout — possible infinite loop in TryReadKinds.");
        map = result;
        return ok;
    }

    [Theory]
    [InlineData("{\"version\":1,\"kinds\":{\"dungeon\":[1150,")]                    // 1: truncated mid-id-array
    [InlineData("{\"version\":1,\"kinds\":{")]                                      // 2: truncated right after "kinds" opens
    [InlineData("{\"version\":1,\"kinds\":{\"dun")]                                 // 3: truncated mid-key
    [InlineData("{\"version\":1,\"kinds\":{\"dungeon\":{\"a\":1}}}")]               // 4: nested object where an id array is expected
    [InlineData("{\"version\":1,\"kinds\":{\"dungeon\":[1150,zzz]}}")]              // 5: malformed token inside an id array
    [InlineData("{\"version\":1,\"kinds\":{\"dungeon\":[\"1150\"]}}")]              // 6: string instead of a number in an id array
    public void TryParse_TruncatedOrMalformedKindsPayload_TerminatesAndReturnsFalse(string json)
    {
        var ok = ParseWithTimeout(json, out var map, ParseTimeout);

        Assert.False(ok);
        // Still yields a usable all-Other map, never null, so callers never null-check.
        Assert.NotNull(map);
        Assert.Equal(ContentKind.Other, map.KindOf(1151));
    }
}
