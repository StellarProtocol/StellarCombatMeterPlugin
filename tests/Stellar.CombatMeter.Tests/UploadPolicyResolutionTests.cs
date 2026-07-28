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
}
