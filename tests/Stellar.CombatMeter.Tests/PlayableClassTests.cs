using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Pins the base-class resolution (owner ruling 2026-09-25: <i>"Class #14 is from battle imagine using which
/// transform user temporary and it change user class. it suppose not to show on stats, it suppose to show
/// original class."</i>). A Battle Imagine transform rewrites attr 220 to a non-class ProfessionSystemTable
/// row (14 Lucy, 15 Natsu, 8, 10, …); a run banked while transformed uploaded that as the actor's class —
/// measured prod run 233654948275945472, uid 1032773, plugin 2.10.0: professionId=15, classSpans=[12,15] —
/// and the self actor lost its loadout/modules/talents because the lookup keyed on 15.
/// Never weaken these pins.
/// </summary>
public class PlayableClassTests
{
    [Fact]
    public void PlayableSet_is_exactly_the_nine_classes()
        => Assert.Equal(new[] { 1, 2, 3, 4, 5, 9, 11, 12, 13 }, PlayableClass.All.OrderBy(x => x).ToArray());

    [Theory]
    [InlineData(0)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(10)]
    [InlineData(14)] [InlineData(15)] [InlineData(-12)]
    public void Transforms_and_unknown_are_not_playable(int id) => Assert.False(PlayableClass.IsPlayable(id));

    [Fact]
    public void EndedTransformed_Natsu_resolves_to_the_last_playable_span()   // the measured prod run
        => Assert.Equal(12, PlayableClass.ResolveBaseProfession(15, new long[] { 12, 15 }));

    [Fact]
    public void Transform_with_no_timeline_is_unknown_never_a_guess()
    {
        Assert.Equal(0, PlayableClass.ResolveBaseProfession(14, null));
        Assert.Equal(0, PlayableClass.ResolveBaseProfession(14, new long[0]));
        Assert.Equal(0, PlayableClass.ResolveBaseProfession(14, new long[] { 14, 15 }));   // only transforms seen
    }

    [Fact]
    public void Repeated_transforms_keep_the_base_class()
        => Assert.Equal(5, PlayableClass.ResolveBaseProfession(5, new long[] { 5, 14, 5, 14, 5 }));

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(9)] [InlineData(11)] [InlineData(12)] [InlineData(13)]
    public void Playable_attr220_passes_through(int id)
    {
        Assert.Equal(id, PlayableClass.ResolveBaseProfession(id, null));
        // A real class switch: attr 220 is the class being played now, even when the timeline started elsewhere.
        Assert.Equal(id, PlayableClass.ResolveBaseProfession(id, new long[] { 2, id }));
    }

    [Fact]
    public void Real_class_switch_then_transform_reports_the_class_switched_to()
        => Assert.Equal(2, PlayableClass.ResolveBaseProfession(15, new long[] { 5, 2, 15 }));

    private static EntitySnapshot TransformedSnap() => new()
    {
        AttrIds       = new[] { 10000, 220 },
        AttrValues    = new long[] { 60, 15 },          // attr 220 = Natsu at archive
        ClassSpanProf = new long[] { 12, 15 },
        ClassSpanStart = new long[] { 0, 5_000 },
        ClassSpanEnd   = new long[] { 5_000, 9_000 },
    };

    [Fact]
    public void Snapshot_resolution_reads_attr220_and_spans_without_mutating_raw_capture()
    {
        var snap = TransformedSnap();
        Assert.Equal(12, PlayableClass.ResolveActorProfession(snap));
        Assert.Equal(15, snap.AttrValues[1]);                                  // raw attr 220 untouched
        Assert.Equal(new long[] { 12, 15 }, snap.ClassSpanProf);               // raw spans untouched
        Assert.Equal(2, CombatLogAssembler.BuildActorClassSpans(snap)!.Count); // both spans still upload
    }

    private static CapturedLoadout Loadout(int prof, int talentStageId, int moduleConfigId) => new(
        ProfessionId:  prof,
        ProjectName:   $"class {prof}",
        TalentStageId: talentStageId,
        Gear:          new List<int[]> { new[] { 200, prof } },
        GearDetail:    new List<GearDetail>(),
        Skills:        new List<int[]> { new[] { 1241, 30, 6 } },
        Fashion:       new List<Fashion>(),
        Modules:       new List<CapturedModule> { new(0, moduleConfigId, 5, new List<int[]>()) },
        TalentNodes:   new[] { talentStageId },
        AbilityScore:  prof * 1000L);

    [Fact]
    public void EndedTransformed_self_keeps_the_base_class_loadout_and_equipment()
    {
        var runLoadouts = new List<CapturedLoadout> { Loadout(12, talentStageId: 1201, moduleConfigId: 777) };
        var prof = PlayableClass.ResolveActorProfession(TransformedSnap());

        var (loadouts, modules, talentStageId, talentNodes) =
            CombatLogAssembler.ResolveLoadoutFields(isLocal: true, prof, runLoadouts);
        Assert.NotNull(loadouts);
        Assert.Equal(777, modules!.Single().ConfigId);
        Assert.Equal(1201, talentStageId);
        Assert.Equal(new[] { 1201 }, talentNodes);

        var fromSnapshot = ((IReadOnlyList<int[]>)new List<int[]>(), (IReadOnlyList<GearDetail>?)null,
            (IReadOnlyList<int[]>)new List<int[]>(), 0L);
        var equip = CombatLogAssembler.ResolveSelfEquipment(isLocal: true, prof, runLoadouts, fromSnapshot);
        Assert.Equal(12_000L, equip.AbilityScore);          // the Shield Knight entry, not the snapshot fallback
        Assert.Equal(12, equip.Gear.Single()[1]);
    }
}
