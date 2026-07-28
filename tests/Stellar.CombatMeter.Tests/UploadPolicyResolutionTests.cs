using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>Spec test 6 — a re-upload resolves the kind from the STORED encounter header
/// (EncounterHistoryEntry.SceneName, persisted as JSON key "scene"), never from the live scene.</summary>
public class UploadPolicyResolutionTests
{
    [Theory]
    [InlineData("1151", 1151)]
    [InlineData("13021", 13021)]
    [InlineData("7152", 7152)]
    public void ParseMapId_ReadsTheStoredSceneName(string sceneName, int expected)
        => Assert.Equal(expected, Plugin.ParseMapId(sceneName));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TownScene")]
    [InlineData("11 51")]
    public void ParseMapId_Unparseable_IsZero_WhichClassifiesAsOther(string? sceneName)
    {
        // Same fallback CombatLogAssembler.BuildEncounter uses (sceneMapId = 0).
        Assert.Equal(0, Plugin.ParseMapId(sceneName));
        Assert.Equal(ContentKind.Other, ContentKindMap.Empty.KindOf(Plugin.ParseMapId(sceneName)));
    }

    [Fact]
    public void StoredSceneName_ResolvesItsOwnKind_NotWhateverSceneIsLiveNow()
    {
        Assert.True(ContentKindMap.TryParse(
            "{\"version\":1,\"kinds\":{\"dungeon\":[1151],\"raid\":[13021],\"worldboss\":[7152],\"other\":[]}}",
            out var map));

        // A raid archived earlier still resolves `raid` even though the player is now in a dungeon.
        Assert.Equal(ContentKind.Raid, map.KindOf(Plugin.ParseMapId("13021")));
        Assert.Equal(ContentKind.Dungeon, map.KindOf(Plugin.ParseMapId("1151")));
    }

    // Finding 2: exercise the actual production seam (Plugin.ResolveKind's static delegate target)
    // rather than hand-composing map.KindOf(ParseMapId(...)). A broken instance ResolveKind that reads
    // the LIVE scene (_services.ClientState.CurrentSceneName) instead of the archived entry's stored
    // SceneName would still pass every map.KindOf(...) test above; it can only be caught by calling
    // the seam with a real archived entry.
    [Fact]
    public void StaticResolveKind_UsesTheEntrysStoredSceneName()
    {
        Assert.True(ContentKindMap.TryParse(
            "{\"version\":1,\"kinds\":{\"dungeon\":[1151],\"raid\":[13021],\"worldboss\":[7152],\"other\":[]}}",
            out var map));

        var raidEntry = new Plugin.EncounterHistoryEntry { SceneName = "13021" };
        Assert.Equal(ContentKind.Raid, Plugin.ResolveKind(map, raidEntry));
    }

    // The discriminating case: two entries with different stored SceneName values, resolved through
    // the SAME map, must resolve to DIFFERENT kinds. An implementation that ignores the entry (e.g.
    // reads a single live scene instead) would return the same kind for both and fail here.
    [Fact]
    public void StaticResolveKind_DifferentEntries_SameMap_ResolveToDifferentKinds()
    {
        Assert.True(ContentKindMap.TryParse(
            "{\"version\":1,\"kinds\":{\"dungeon\":[1151],\"raid\":[13021],\"worldboss\":[7152],\"other\":[]}}",
            out var map));

        var dungeonEntry = new Plugin.EncounterHistoryEntry { SceneName = "1151" };
        var raidEntry = new Plugin.EncounterHistoryEntry { SceneName = "13021" };

        var dungeonKind = Plugin.ResolveKind(map, dungeonEntry);
        var raidKind = Plugin.ResolveKind(map, raidEntry);

        Assert.Equal(ContentKind.Dungeon, dungeonKind);
        Assert.Equal(ContentKind.Raid, raidKind);
        Assert.NotEqual(dungeonKind, raidKind);
    }

    // Finding 3: pin ParseMapId's NumberStyles.Integer + InvariantCulture contract against a sloppy
    // int.TryParse(s, out id) (CurrentCulture, NumberStyles.Integer|AllowThousands), which would pass
    // every test above but diverges on group separators.
    [Fact]
    public void ParseMapId_GroupSeparator_IsNotPermitted_ByNumberStylesInteger()
        // NumberStyles.Integer does NOT include AllowThousands, so a comma group separator makes the
        // string unparseable ⇒ falls back to 0. A default int.TryParse(s, out id) DOES allow thousands
        // separators and would return 1151 here instead.
        => Assert.Equal(0, Plugin.ParseMapId("1,151"));

    [Fact]
    public void ParseMapId_LeadingAndTrailingWhitespace_IsPermitted_ByNumberStylesInteger()
        // NumberStyles.Integer includes AllowLeadingWhite | AllowTrailingWhite.
        => Assert.Equal(1151, Plugin.ParseMapId(" 1151 "));

    [Fact]
    public void ParseMapId_LeadingSign_IsPermitted_ByNumberStylesInteger()
        // NumberStyles.Integer includes AllowLeadingSign.
        => Assert.Equal(1151, Plugin.ParseMapId("+1151"));

    [Fact]
    public void ParseMapId_HexNotation_IsNotPermitted_ByNumberStylesInteger()
        // NumberStyles.Integer does NOT include AllowHexSpecifier, and "0x47F" is not a valid decimal
        // integer literal either way ⇒ falls back to 0.
        => Assert.Equal(0, Plugin.ParseMapId("0x47F"));
}
