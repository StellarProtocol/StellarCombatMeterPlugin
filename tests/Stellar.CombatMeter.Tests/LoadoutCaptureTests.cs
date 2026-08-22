using System.Collections.Generic;
using System.Linq;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Owner design 2026-08-02 (per-class loadout capture): a class the player PLAYED was ACTIVE at some
/// point, and IInventory gives the ACTIVE class rich data that broadcast APIs never carry once you've
/// swapped away — so the plugin snapshots whichever class is active on a profession change and stores
/// it keyed by professionId. Reset only at RUN start (not per encounter/archive), so a player who
/// played 2 classes this run keeps BOTH captured loadouts across every archive in the run.
///
/// SPEC EVOLVED 2026-08-22 (owner run <c>B47O8jx6wp</c>): the accumulator was a plain latest-wins
/// upsert — a class already seen this run was ALWAYS replaced by its newest capture, with no regard
/// for whether the earlier setup had actually been fought with. That let a post-fight equipment edit
/// (5-module Frostbeam fought, then one module removed) silently overwrite the fought-with gear before
/// any archive banked it, so the upload carried only the 4-module setup for a fight that used 5. Owner,
/// verbatim: <em>"when any equipment change such as module,talents,equipments... and use have a combat
/// with that setup it require plugin to take snapshot of it even class has no change."</em>
///
/// The tests below that pre-date this evolution (<see cref="SameContent_Revisit_TheLatestCaptureWins"/>,
/// <see cref="SnapshotHoldsOneEntryPerDistinctClassPlayed"/>) still pass under the new contract because
/// their fixtures only vary <c>ProjectName</c> (never gear/modules/talents), which is a SAME-CONTENT
/// refresh either way — but they no longer demonstrate a blanket "latest always wins" rule, so their
/// names/docs are updated to say exactly what they pin. The new fought-with-vs-unfought-draft behavior
/// is pinned by the tests in the second region below.
///
/// These tests exercise the pure accumulator (<see cref="LoadoutCapture"/>) and the run-boundary gate
/// (<see cref="Plugin.IsNewLoadoutRun"/>) with plain fake <see cref="CapturedLoadout"/> inputs — no
/// IPluginServices/IL2CPP mock involved, matching the plugin's existing pure-data test style (see
/// ReplayCaptureGateTests / SelfNamePersistenceTests). The live-service reads that BUILD a
/// CapturedLoadout (PollLocalProfession / CaptureActiveClassLoadout in Plugin.LoadoutCapture.cs) are
/// deliberately thin and untested here — only in-game verification can exercise IInventory/ILoadout.
/// </summary>
public class LoadoutCaptureTests
{
    // gearItemId defaults to professionId (matching the ORIGINAL fixture shape) so pre-existing tests
    // that never pass it keep comparing byte-identical content across "revisits" — only tests that
    // need a genuinely DIFFERENT setup pass a distinct value.
    private static CapturedLoadout Fake(int professionId, string tag, int? gearItemId = null) => new(
        ProfessionId:  professionId,
        ProjectName:   tag,
        TalentStageId: professionId * 100,
        Gear:          new List<int[]> { new[] { 200, gearItemId ?? professionId } },
        GearDetail:    new List<GearDetail>(),
        Skills:        new List<int[]>(),
        Fashion:       new List<Fashion>(),
        Modules:       new List<CapturedModule>());

    [Fact]
    public void SnapshotHoldsOneEntryPerDistinctClassPlayed()
    {
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "first-2"), combatMarker: 0);
        capture.Capture(Fake(5, "only-5"), combatMarker: 0);
        capture.Capture(Fake(2, "second-2"), combatMarker: 0);   // revisits class 2, SAME content

        var professions = capture.Snapshot().Select(l => l.ProfessionId).OrderBy(p => p);
        Assert.Equal(new[] { 2, 5 }, professions);
    }

    [Fact]
    public void SameContent_Revisit_TheLatestCaptureWins()
    {
        // Renamed from "RevisitingAClass_TheLatestCaptureWins" — that name implied a blanket rule that
        // no longer holds (see class doc). This fixture's "revisit" never changes gear/modules/talents,
        // so it exercises the SAME-CONTENT refresh branch specifically, not append-vs-replace.
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "first-2"), combatMarker: 0);
        capture.Capture(Fake(5, "only-5"), combatMarker: 0);
        capture.Capture(Fake(2, "second-2"), combatMarker: 0);

        var class2 = capture.Snapshot().Single(l => l.ProfessionId == 2);
        Assert.Equal("second-2", class2.ProjectName);
        // Class 5's own entry is untouched by the class-2 revisit.
        Assert.Equal("only-5", capture.Snapshot().Single(l => l.ProfessionId == 5).ProjectName);
    }

    [Fact]
    public void ResetForRun_ClearsEveryCapturedClass_EvenWithMultipleEntriesPerClass()
    {
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "5-module", gearItemId: 500), combatMarker: 0);
        capture.Capture(Fake(2, "4-module", gearItemId: 400), combatMarker: 4);   // fought-with -> appended
        capture.Capture(Fake(5, "only-5"), combatMarker: 4);

        capture.ResetForRun();

        Assert.Empty(capture.Snapshot());
    }

    [Fact]
    public void CaptureIsANoOpOnAnEmptyAccumulatorBeforeAnyPoll()
        => Assert.Empty(new LoadoutCapture().Snapshot());

    [Fact]
    public void SnapshotIsFrozen_LaterCaptureAndResetDoNotMutateIt()
    {
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "first-2"), combatMarker: 0);
        capture.Capture(Fake(5, "only-5"), combatMarker: 0);

        var frozen = capture.Snapshot();

        // Mutate the live accumulator AFTER the snapshot was taken.
        capture.Capture(Fake(9, "third-9"), combatMarker: 0);
        capture.ResetForRun();

        var professions = frozen.Select(l => l.ProfessionId).OrderBy(p => p);
        Assert.Equal(new[] { 2, 5 }, professions);
        Assert.Equal("first-2", frozen.Single(l => l.ProfessionId == 2).ProjectName);
        Assert.Equal("only-5", frozen.Single(l => l.ProfessionId == 5).ProjectName);
    }

    // --- run-boundary gate (Plugin.IsNewLoadoutRun) — reset only at true run START ---

    [Theory]
    [InlineData(0, 100, true)]     // town/boot -> entering a run: fresh accumulator for this run
    [InlineData(100, 200, true)]   // different run without going through 0 (crash / re-enter)
    [InlineData(100, 100, false)]  // same run, repeated poll: no-op
    [InlineData(100, 0, false)]    // leaving to town: KEEP data — the dungeon->town archive still reads it
    [InlineData(0, 0, false)]      // still not in a run
    public void IsNewLoadoutRun_MatchesRunBoundarySemantics(long previous, long next, bool expected)
        => Assert.Equal(expected, Plugin.IsNewLoadoutRun(previous, next));

    // -------------------------------------------------------------------------
    // Fought-with-setup preservation (owner run B47O8jx6wp) — the new append-vs-replace decision.
    // See LoadoutCapture.Capture's doc for the full table this pins.
    // -------------------------------------------------------------------------

    [Fact]
    public void FoughtWithSetup_ThenChanged_PreservesBothEntriesInOrder()
    {
        // The B47O8jx6wp shape: equip the 5-module setup (marker=0), fight with it (marker advances to
        // 3 by the time of the next capture), THEN remove a module. The old accumulator lost the
        // 5-module entry here; the fix must append instead of replacing it.
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "5-module", gearItemId: 500), combatMarker: 0);
        capture.Capture(Fake(2, "4-module", gearItemId: 400), combatMarker: 3);

        var entries = capture.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal(500, entries[0].Gear[0][1]);
        Assert.Equal("5-module", entries[0].ProjectName);
        Assert.Equal(400, entries[1].Gear[0][1]);
        Assert.Equal("4-module", entries[1].ProjectName);
    }

    [Fact]
    public void UnfoughtDraft_DifferentContent_NoCombatSince_Replaces()
    {
        // Same marker on both calls (0 -> 0): no combat happened between capturing the first draft and
        // capturing the second, different one — this is gear-browsing before a pull, not a fought-with
        // setup, so it must be replaced, not appended.
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "draft-A", gearItemId: 500), combatMarker: 0);
        capture.Capture(Fake(2, "draft-B", gearItemId: 400), combatMarker: 0);

        var entries = capture.Snapshot();
        Assert.Single(entries);
        Assert.Equal(400, entries[0].Gear[0][1]);
        Assert.Equal("draft-B", entries[0].ProjectName);
    }

    [Fact]
    public void SameContent_NoCombatSince_RefreshesInPlace()
    {
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "first-2", gearItemId: 500), combatMarker: 5);
        capture.Capture(Fake(2, "second-2", gearItemId: 500), combatMarker: 5);   // identical content

        var entries = capture.Snapshot();
        Assert.Single(entries);
        Assert.Equal("second-2", entries[0].ProjectName);
    }

    [Fact]
    public void SameContent_CombatAdvancedSince_StillRefreshesInPlace_NeverAppends()
    {
        // Content identity is checked BEFORE the marker: re-equipping the IDENTICAL setup after
        // fighting with it must never mint a second entry for the same physical gear.
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "first-2", gearItemId: 500), combatMarker: 0);
        capture.Capture(Fake(2, "second-2", gearItemId: 500), combatMarker: 9);   // same content, marker moved

        var entries = capture.Snapshot();
        Assert.Single(entries);
        Assert.Equal("second-2", entries[0].ProjectName);
    }

    [Fact]
    public void AppendedEntry_LaterUnfoughtEdit_ReplacesOnlyTheNewestEntry()
    {
        // Three-step chain: fight setup A (appends nothing yet, it's the first entry) -> fight it
        // (marker advances) -> change to B, fought-with A preserved, B appended -> immediately tweak B
        // again with no combat since (marker unchanged) -> B's entry (the newest one) is replaced; A
        // stays untouched.
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "A", gearItemId: 500), combatMarker: 0);
        capture.Capture(Fake(2, "B", gearItemId: 400), combatMarker: 3);   // A fought-with -> appended
        capture.Capture(Fake(2, "B-tweak", gearItemId: 401), combatMarker: 3);   // no combat since B -> replace B

        var entries = capture.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal(500, entries[0].Gear[0][1]);
        Assert.Equal("A", entries[0].ProjectName);
        Assert.Equal(401, entries[1].Gear[0][1]);
        Assert.Equal("B-tweak", entries[1].ProjectName);
    }

    // -------------------------------------------------------------------------
    // SameSetup identity — pure content check. Probed by inverting each assertion once (flip a value,
    // confirm it flips the result) so these fixtures cannot pass regardless of the implementation.
    // -------------------------------------------------------------------------

    private static CapturedLoadout WithModule(CapturedLoadout baseline, int slot, int configId, int quality, params int[] partsFlat)
    {
        var parts = new List<int[]>();
        for (var i = 0; i < partsFlat.Length; i += 2) parts.Add(new[] { partsFlat[i], partsFlat[i + 1] });
        return baseline with { Modules = new List<CapturedModule> { new(slot, configId, quality, parts) } };
    }

    [Fact]
    public void SameSetup_GearOrderIndependent_PermutedPairsAreEqual()
    {
        var a = Fake(2, "x") with { Gear = new List<int[]> { new[] { 200, 2 }, new[] { 201, 9 } } };
        var b = Fake(2, "x") with { Gear = new List<int[]> { new[] { 201, 9 }, new[] { 200, 2 } } };
        Assert.True(LoadoutCapture.SameSetup(a, b));

        var c = a with { Gear = new List<int[]> { new[] { 201, 9 }, new[] { 200, 3 } } };   // itemId differs
        Assert.False(LoadoutCapture.SameSetup(a, c));
    }

    [Fact]
    public void SameSetup_TalentNodeOrderIndependent_PermutedNodesAreEqual()
    {
        var a = Fake(2, "x") with { TalentNodes = new List<int> { 1, 2, 3 } };
        var b = Fake(2, "x") with { TalentNodes = new List<int> { 3, 1, 2 } };
        Assert.True(LoadoutCapture.SameSetup(a, b));

        var c = a with { TalentNodes = new List<int> { 1, 2, 4 } };
        Assert.False(LoadoutCapture.SameSetup(a, c));
    }

    [Fact]
    public void SameSetup_ModuleQualityChange_IsADifferentSetup()
    {
        var a = WithModule(Fake(2, "x"), slot: 0, configId: 5500102, quality: 5, partsFlat: new[] { 1110, 5 });
        var b = WithModule(Fake(2, "x"), slot: 0, configId: 5500102, quality: 6, partsFlat: new[] { 1110, 5 });
        Assert.False(LoadoutCapture.SameSetup(a, b));

        var same = WithModule(Fake(2, "x"), slot: 0, configId: 5500102, quality: 5, partsFlat: new[] { 1110, 5 });
        Assert.True(LoadoutCapture.SameSetup(a, same));
    }

    [Fact]
    public void SameSetup_GearDetailDifference_IsIgnored()
    {
        var a = Fake(2, "x") with { GearDetail = new List<GearDetail>() };
        var b = Fake(2, "x") with
        {
            GearDetail = new List<GearDetail> { new(0, 5, 30, 100, 100, 1, 1, new List<int[]>(), 1, 0) },
        };
        Assert.True(LoadoutCapture.SameSetup(a, b));
    }

    [Fact]
    public void SameSetup_TalentStageIdDifference_IsADifferentSetup()
    {
        var a = Fake(2, "x");
        var b = a with { TalentStageId = a.TalentStageId + 1 };
        Assert.False(LoadoutCapture.SameSetup(a, b));
    }
}
