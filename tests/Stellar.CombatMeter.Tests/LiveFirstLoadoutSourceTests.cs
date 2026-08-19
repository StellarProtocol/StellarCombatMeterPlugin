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
}
