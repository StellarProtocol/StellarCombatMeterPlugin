using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class HistoryTriggerFieldTests
{
    [Fact]
    public void Trigger_roundtrips()
    {
        var e = new Plugin.EncounterHistoryEntry { SceneName = "X", Trigger = "wipe" };
        var json = HistoryStore.SerializeEntry(e);
        Assert.True(HistoryStore.TryDeserializeEntry(json, out var back));
        Assert.Equal("wipe", back!.Trigger);
    }

    [Fact]
    public void Legacy_entry_without_trig_defaults_to_manual()
    {
        // Minimal v1-shaped entry: the reader requires only the version marker.
        Assert.True(HistoryStore.TryDeserializeEntry("{\"v\":1}", out var e));
        Assert.Equal("manual", e!.Trigger);
    }

    [Fact]
    public void ArchiveReasonTag_maps_every_reason()
    {
        Assert.Equal("manual", Plugin.ArchiveReasonTag(AutoArchive.ArchiveReason.Manual));
        Assert.Equal("scene",  Plugin.ArchiveReasonTag(AutoArchive.ArchiveReason.SceneChange));
        Assert.Equal("wipe",   Plugin.ArchiveReasonTag(AutoArchive.ArchiveReason.Wipe));
        Assert.Equal("boss",   Plugin.ArchiveReasonTag(AutoArchive.ArchiveReason.BossPhase));
        Assert.Equal("idle",   Plugin.ArchiveReasonTag(AutoArchive.ArchiveReason.Idle));
        Assert.Equal("stage",  Plugin.ArchiveReasonTag(AutoArchive.ArchiveReason.StageChange));
    }
}
