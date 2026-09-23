using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Belt-cut content gate (Plugin.RunBoundary.cs, ResolveArmedBoundaryBelt). A same-instance loading-flash
// resolved by a combat event banks a segment CUT — that path cuts the raid scripted-death stage seam and
// nothing else does (spec 2026-08-26, cuts PRESERVED). But a map-asset teleport in a DUNGEON (owner report
// 2026-09-24, Void - Tina's Mindrealm, run 90078997639069696: a combat-belt-cut split the fight at 96.4%
// boss HP) raises the SAME loading flash while the boss is still alive, and must NOT split the fight —
// dungeons have the game's own stage/settlement clear signal. So the belt cut is suppressed for CONFIRMED
// non-raid content (Dungeon/WorldBoss/Vault); Raid — and unknown/unfetched (Other) — keep the cut
// (fail-safe: an unfetched raid map must never lose a stage). Raids are byte-unchanged.
public class SegmentCutContentGateTests
{
    [Fact] public void Raid_keeps_the_belt_cut()
        => Assert.True(Plugin.SegmentCutAllowedForKind(ContentKind.Raid));

    [Fact] public void Dungeon_suppresses_the_belt_cut()
        => Assert.False(Plugin.SegmentCutAllowedForKind(ContentKind.Dungeon));

    [Fact] public void WorldBoss_suppresses_the_belt_cut()
        => Assert.False(Plugin.SegmentCutAllowedForKind(ContentKind.WorldBoss));

    [Fact] public void Vault_suppresses_the_belt_cut()
        => Assert.False(Plugin.SegmentCutAllowedForKind(ContentKind.Vault));

    [Fact] public void Other_unknown_keeps_the_belt_cut_failsafe()
        => Assert.True(Plugin.SegmentCutAllowedForKind(ContentKind.Other));
}
