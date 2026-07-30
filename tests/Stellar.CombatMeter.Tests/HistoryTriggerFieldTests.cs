using System;
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
        Assert.Equal("bosskill", Plugin.ArchiveReasonTag(AutoArchive.ArchiveReason.BossKill));
    }

    [Fact]
    public void TriggerSuffix_covers_every_auto_reason()
        // Finding 5 (review round 2026-07-27): Plugin.HistoryWindow's TriggerSuffix has its OWN
        // allow-list, separate from ArchiveReasonTag's switch above — a reason can be mapped there and
        // STILL render with no suffix in the in-game history if it's missing here (exactly what
        // happened to "bosskill": indistinguishable from a manual archive). Every AUTO reason (every
        // ArchiveReason except Manual and SceneChange, which intentionally stay untagged — pre-v10
        // default) must produce a non-empty suffix, so a future reason can't be forgotten here again.
    {
        foreach (AutoArchive.ArchiveReason reason in Enum.GetValues(typeof(AutoArchive.ArchiveReason)))
        {
            var tag = Plugin.ArchiveReasonTag(reason);
            bool isAuto = reason is not (AutoArchive.ArchiveReason.Manual or AutoArchive.ArchiveReason.SceneChange);
            Assert.True(isAuto == (Plugin.TriggerSuffix(tag) != ""),
                $"reason={reason} tag={tag} isAuto={isAuto} suffix='{Plugin.TriggerSuffix(tag)}'");
        }
    }
}
