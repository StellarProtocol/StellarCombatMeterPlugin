using System.Collections.Generic;
using Stellar.Abstractions.Domain.Loadout;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// PINNED regression tests — owner rulings 2026-08-05 + 2026-08-19 ("I don't want any loadout, I want
/// what user currently is using"): the uploaded snapshot is live-first, loadout-never. Root-cause run
/// sea/YcVuYojHoD: with several same-class saved loadouts (Frostbeam / Icicle / tank, all Frost Mage),
/// the old first-match-by-profession pick shipped a SIBLING plan's saved modules as the build. These
/// tests pin the two pure decision seams (<see cref="Plugin.ResolveGearSource"/> /
/// <see cref="Plugin.PickSlot"/>) so no refactor can quietly re-route the snapshot through a saved
/// plan while live or at-play data exists. Do not weaken.
/// </summary>
public class LiveFirstLoadoutSourceTests
{
    // --- ResolveGearSource: which source fills gear/modules at archive ---

    [Fact]
    public void ActiveClass_WithLiveData_UsesLive_NeverASavedPlan()
        => Assert.Equal(LoadoutGearSource.Live, Plugin.ResolveGearSource(
            isActiveClass: true, liveHasData: true, capturedHasData: true));

    [Fact]
    public void ActiveClass_WithLiveData_UsesLive_EvenWhenNothingWasCapturedYet()
        => Assert.Equal(LoadoutGearSource.Live, Plugin.ResolveGearSource(
            isActiveClass: true, liveHasData: true, capturedHasData: false));

    [Fact]
    public void ActiveClass_LiveUnresolved_KeepsTheAtPlayCapture()
        => Assert.Equal(LoadoutGearSource.Captured, Plugin.ResolveGearSource(
            isActiveClass: true, liveHasData: false, capturedHasData: true));

    [Fact]
    public void SwappedAwayClass_KeepsItsFrozenAtPlayCapture_LiveBelongsToAnotherClass()
        => Assert.Equal(LoadoutGearSource.Captured, Plugin.ResolveGearSource(
            isActiveClass: false, liveHasData: true, capturedHasData: true));

    [Fact]
    public void SavedSlotIsStrictlyLastResort_OnlyWhenNoLiveAndNoCaptureExist()
    {
        Assert.Equal(LoadoutGearSource.SavedSlot, Plugin.ResolveGearSource(
            isActiveClass: true, liveHasData: false, capturedHasData: false));
        Assert.Equal(LoadoutGearSource.SavedSlot, Plugin.ResolveGearSource(
            isActiveClass: false, liveHasData: false, capturedHasData: false));
    }

    // --- PickSlot: which loadout entry describes a class (talents/name + last-resort gear) ---

    private static LoadoutSlot Slot(int index, int professionId, bool isCurrent = false, string? name = null)
        => new(index, name ?? $"Plan {index}", isCurrent, professionId);

    [Fact]
    public void PickSlot_TheCurrentPlanBeatsAnEarlierSameClassSibling()
    {
        // The sea/YcVuYojHoD shape: Frostbeam (first), Icicle, tank (current) — all one class.
        var slots = new List<LoadoutSlot>
        {
            Slot(1, professionId: 2, name: "Frostbeam"),
            Slot(2, professionId: 2, name: "Icicle"),
            Slot(3, professionId: 2, isCurrent: true, name: "tank"),
        };
        Assert.Equal("tank", Plugin.PickSlot(slots, professionId: 2)!.Name);
    }

    [Fact]
    public void PickSlot_TheLiveSynthesizedEntryBeatsEverything()
    {
        var slots = new List<LoadoutSlot>
        {
            Slot(1, professionId: 2, isCurrent: true, name: "saved-plan"),
            Slot(-1, professionId: 2, name: "Current"),   // framework live-line entry IS the live state
        };
        Assert.Equal(-1, Plugin.PickSlot(slots, professionId: 2)!.Index);
    }

    [Fact]
    public void PickSlot_FirstMatchOnlyWhenNoCurrentAndNoLiveEntryExists()
    {
        var slots = new List<LoadoutSlot>
        {
            Slot(7, professionId: 5, name: "other-class-current"),
            Slot(1, professionId: 2, name: "only-fallback"),
            Slot(2, professionId: 2, name: "later-sibling"),
        };
        Assert.Equal("only-fallback", Plugin.PickSlot(slots, professionId: 2)!.Name);
    }

    [Fact]
    public void PickSlot_NoSameClassEntry_ReturnsNull()
        => Assert.Null(Plugin.PickSlot(new List<LoadoutSlot> { Slot(1, professionId: 5) }, professionId: 2));

    [Fact]
    public void PickSlot_ACurrentPlanOfAnotherClassNeverLeaksIn()
    {
        var slots = new List<LoadoutSlot>
        {
            Slot(9, professionId: 5, isCurrent: true, name: "wrong-class"),
            Slot(1, professionId: 2, name: "right-class"),
        };
        Assert.Equal("right-class", Plugin.PickSlot(slots, professionId: 2)!.Name);
    }

    // --- ResolveTalents: live talents beat any saved plan's (owner rule; Phase 2) ---

    [Fact]
    public void ResolveTalents_LiveStateForTheActiveClass_BeatsTheSlot()
    {
        var live = new LiveLoadoutState(2, 205, new[] { 9, 9, 9 });
        var slot = Slot(1, professionId: 2, isCurrent: true) with { TalentStageId = 201, TalentNodes = new[] { 1 } };
        var (stage, nodes) = Plugin.ResolveTalents(live, slot, professionId: 2);
        Assert.Equal(205, stage);
        Assert.Equal(new[] { 9, 9, 9 }, nodes);
    }

    [Fact]
    public void ResolveTalents_LiveOfAnotherClass_FallsBackToSlot()
    {
        var live = new LiveLoadoutState(5, 505, new[] { 7 });
        var slot = Slot(1, professionId: 2) with { TalentStageId = 201, TalentNodes = new[] { 1, 2 } };
        var (stage, nodes) = Plugin.ResolveTalents(live, slot, professionId: 2);
        Assert.Equal(201, stage);
        Assert.Equal(new[] { 1, 2 }, nodes);
    }

    [Fact]
    public void ResolveTalents_NoLiveNoSlot_IsEmpty()
    {
        var (stage, nodes) = Plugin.ResolveTalents(null, null, professionId: 2);
        Assert.Equal(0, stage);
        Assert.Null(nodes);
    }

    [Fact]
    public void ResolveTalents_LiveWithZeroStage_StillWins_NeverMixedWithSlot()
    {
        // A live read that resolved the class but not the stage must NOT splice in a saved plan's
        // stage — half-live half-plan talents would mislabel the spec.
        var live = new LiveLoadoutState(2, 0, null);
        var slot = Slot(1, professionId: 2) with { TalentStageId = 201, TalentNodes = new[] { 1 } };
        var (stage, nodes) = Plugin.ResolveTalents(live, slot, professionId: 2);
        Assert.Equal(0, stage);
        Assert.Null(nodes);
    }

    // --- PreferNonEmpty: component-wise fill must never overwrite non-empty captured data with an
    // empty fresh read. PINNED regression — owner-verified 2026-08-19 in-game bug:
    // IInventory.GetLiveEquipped() returned a stale method-21-latched Modules set PLUS an EMPTY Gear
    // set on two different-class runs; ApplyLiveEquipment's old liveHasData OR-check (any component
    // non-empty ⇒ "live has data") treated that as live and overwrote a populated captured Gear with
    // []. PreferNonEmpty (Plugin.LoadoutCapture.cs) is the pure per-component seam that fixes this: a
    // freshly-read component only replaces the captured one when the fresh read is ITSELF non-empty.
    // Do not weaken — a future refactor that goes back to an OR'd whole-record swap reopens this bug.

    [Fact]
    public void ActiveClass_ComponentNeverOverwrittenByEmptySource()
    {
        var kept = new[] { new[] { 1, 100 }, new[] { 2, 200 } };
        var emptyFresh = System.Array.Empty<int[]>();
        Assert.Same(kept, Plugin.PreferNonEmpty(emptyFresh, kept));
    }

    [Fact]
    public void PreferNonEmpty_NonEmptyFresh_ReplacesKept()
    {
        var kept = new[] { new[] { 1, 100 } };
        var fresh = new[] { new[] { 2, 200 }, new[] { 3, 300 } };
        Assert.Same(fresh, Plugin.PreferNonEmpty(fresh, kept));
    }

    [Fact]
    public void PreferNonEmpty_BothEmpty_ReturnsKept()
    {
        var kept = System.Array.Empty<int[]>();
        var fresh = System.Array.Empty<int[]>();
        Assert.Same(kept, Plugin.PreferNonEmpty(fresh, kept));
    }
}
