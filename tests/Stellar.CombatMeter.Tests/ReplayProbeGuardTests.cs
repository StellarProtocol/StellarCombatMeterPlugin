using Stellar.Abstractions.Domain;
using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Guards the replay position probe against dereferencing a freed IL2CPP entity model. Reading a
// torn-down / out-of-AOI entity's live transform (ZModel.GetAttrGoPosition via reflection) is an
// UNCATCHABLE native access violation — a managed try/catch cannot stop it — and is the cause of the
// crash-on-line-switch / crash-on-dungeon-enter reports. Two pure gates make the probe safe.
public class ReplayProbeGuardTests
{
    private static EntityId Id(long v) => new EntityId(v);

    // --- liveness gate: probe self + party (always present with you) + any AOI-known entity;
    //     out-of-AOI others are skipped. Party is NOT gated on combat vitals, so the pre-combat
    //     dungeon walk-in is captured; an out-of-AOI party member is still safe because the game's
    //     GetEntity returns null for culled ids (a clean gap, not a deref). ---

    [Fact]
    public void Self_is_always_probed()
        => Assert.True(Plugin.ShouldProbeTransform(Id(7), Id(7), isPartyMember: false, aoiKnown: false));

    [Fact]
    public void Party_member_is_probed_during_walk_in_before_any_combat_vitals()
        => Assert.True(Plugin.ShouldProbeTransform(Id(9), Id(7), isPartyMember: true, aoiKnown: false));

    [Fact]
    public void Aoi_known_non_party_is_probed()   // a mob/boss that has entered combat
        => Assert.True(Plugin.ShouldProbeTransform(Id(9), Id(7), isPartyMember: false, aoiKnown: true));

    [Fact]
    public void Unknown_non_party_is_skipped()    // a not-yet-engaged / despawned mob
        => Assert.False(Plugin.ShouldProbeTransform(Id(9), Id(7), isPartyMember: false, aoiKnown: false));

    // --- I4 (2026-08-26 raid-bosshp-capture-design): IsStillAoiKnown — IsKnown alone stopped meaning
    //     "still in AOI" once the framework's LeftAoi fix landed (a Normal AOI-disappear now KEEPS the
    //     vitals row instead of evicting it). Holding the crash-risk probe gate open for a kept-but-
    //     left-AOI row is exactly the class of bug ShouldProbeTransform exists to prevent. ---

    private static EntityVitals Vitals(bool isKnown, bool leftAoi) => new(0, 0, isKnown) { LeftAoi = leftAoi };

    [Fact]
    public void IsStillAoiKnown_true_for_a_normal_known_row()
        => Assert.True(Plugin.IsStillAoiKnown(Vitals(isKnown: true, leftAoi: false)));

    [Fact]
    public void IsStillAoiKnown_false_for_an_unknown_row()
        => Assert.False(Plugin.IsStillAoiKnown(Vitals(isKnown: false, leftAoi: false)));

    [Fact]
    public void IsStillAoiKnown_false_for_a_kept_but_left_aoi_row()
        // The row is IsKnown=true (kept, not evicted) but LeftAoi=true — must NOT read as "still in
        // AOI", or the probe gate stays open for a possibly-freed IL2CPP model.
        => Assert.False(Plugin.IsStillAoiKnown(Vitals(isKnown: true, leftAoi: true)));

    // --- settle gate: skip the whole sample pass for ReplaySettleMs after a scene change ---

    [Fact]
    public void No_scene_change_seen_is_not_settling()
        => Assert.False(Plugin.IsWithinReplaySettle(nowMs: 1000, lastSceneChangeMs: 0));

    [Fact]
    public void Just_after_a_scene_change_is_settling()
        => Assert.True(Plugin.IsWithinReplaySettle(nowMs: 1000, lastSceneChangeMs: 500));

    [Fact]
    public void After_the_settle_window_resumes()
        => Assert.False(Plugin.IsWithinReplaySettle(nowMs: 500 + Plugin.ReplaySettleMs, lastSceneChangeMs: 500));

    [Fact]
    public void One_ms_before_window_end_is_still_settling()
        => Assert.True(Plugin.IsWithinReplaySettle(nowMs: 499 + Plugin.ReplaySettleMs, lastSceneChangeMs: 500));

    [Fact]
    public void Backwards_clock_does_not_wedge_in_settling()
        => Assert.False(Plugin.IsWithinReplaySettle(nowMs: 500, lastSceneChangeMs: 1000));
}
