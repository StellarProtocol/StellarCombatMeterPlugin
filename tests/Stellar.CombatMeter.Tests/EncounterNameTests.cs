using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Closes the long-standing `Name: null // TODO(enrich-later): GetScene(sceneMapId)?.Name` in
/// CombatLogAssembler. Owner observation 2026-07-29: the plugin's own History window already shows
/// "Floor 30", resolving it via `_services.GameData.World.GetScene(id)?.Name`
/// (Plugin.HistoryWindow.cs) — so the name IS available at upload time and was simply not being sent.
///
/// Consequence of not sending it: `encounter.name` was null in every upload, and the worker's R2 meta
/// mirror copies that field (`encounterName: encounter.name`), so runs could not be enumerated by name
/// for ops backfills. The site was unaffected because it derives names from `mapId` via
/// names.generated.json — which is why the UI looked right while the stored data was empty.
/// </summary>
public class EncounterNameTests
{
    [Fact]
    public void BuildEncounter_CarriesTheResolvedSceneName()
    {
        var entry = new Plugin.EncounterHistoryEntry { SceneName = "32130", LevelUuid = 1 };
        var enc = CombatLogAssembler.BuildEncounter(entry, bossConfigId: 0, sceneDisplayName: "Floor 30");
        Assert.Equal("Floor 30", enc.Name);
        // mapId is still parsed from the scene token — the name is additive, never a substitute.
        Assert.Equal(32130, enc.MapId);
    }

    [Fact]
    public void BuildEncounter_OmittedNameStaysNull_SoOldCallSitesAreUnchanged()
    {
        // Plugin.Replay.cs builds an encounter for the replay doc and has no use for the name.
        var entry = new Plugin.EncounterHistoryEntry { SceneName = "32130", LevelUuid = 1 };
        Assert.Null(CombatLogAssembler.BuildEncounter(entry).Name);
    }

    [Fact]
    public void BuildEncounter_BlankNameIsNormalisedToNull_NotAnEmptyString()
    {
        // The server/site fall back to their own mapId lookup on null; an empty string would defeat that.
        var entry = new Plugin.EncounterHistoryEntry { SceneName = "32130", LevelUuid = 1 };
        Assert.Null(CombatLogAssembler.BuildEncounter(entry, 0, "").Name);
        Assert.Null(CombatLogAssembler.BuildEncounter(entry, 0, "   ").Name);
    }
}
