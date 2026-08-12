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
        Assert.Equal("boundary", Plugin.ArchiveReasonTag(AutoArchive.ArchiveReason.RunBoundary));
    }

    // The pre-boss archive banked at the inline boss cut (ArchiveReason.BossPhase) carries NO boss
    // combat by construction — the cut fires at the FIRST boss hit, which goes into the NEXT segment.
    // So "boss" was always the wrong chip for it (owner 2026-08-03: "if before current bossphase
    // player didn't attack boss at all it shouldn't name that archive as boss"). Name it for its
    // CONTENT: "clear" when the party actually fought (dealt damage), "prepare" when only healing
    // happened (a heal-up before the pull). All-zero archives are suppressed upstream, so the
    // damage-less+heal-less fallback ("clear") is defensive only.
    [Theory]
    [InlineData(100, 0, "clear")]     // dealt damage → trash clear
    [InlineData(100, 50, "clear")]    // damage present wins over healing
    [InlineData(0, 50, "prepare")]    // no damage, only healing → heal-up before the pull
    [InlineData(0, 0, "clear")]       // fallback (should not reach here — all-zero is suppressed)
    public void PreBossPhaseTag_names_by_content(long dmg, long heal, string expected)
        => Assert.Equal(expected, Plugin.PreBossPhaseTag(dmg, heal));

    [Fact]
    public void TriggerSuffix_shows_clear_and_prepare()
    {
        // The content-derived pre-boss tags must render a suffix in the session list too (else a
        // "clear"/"prepare" archive would look like an untagged manual one).
        Assert.Equal(" · clear", Plugin.TriggerSuffix("clear"));
        Assert.Equal(" · prepare", Plugin.TriggerSuffix("prepare"));
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
