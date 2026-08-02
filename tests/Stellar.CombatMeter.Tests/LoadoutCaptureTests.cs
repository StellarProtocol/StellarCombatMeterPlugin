using System.Collections.Generic;
using System.Linq;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Owner design 2026-08-02 (per-class loadout capture): a class the player PLAYED was ACTIVE at some
/// point, and IInventory gives the ACTIVE class rich data that broadcast APIs never carry once you've
/// swapped away — so the plugin snapshots whichever class is active on a profession change and stores
/// it keyed by professionId, latest-wins. Reset only at RUN start (not per encounter/archive), so a
/// player who played 2 classes this run keeps BOTH captured loadouts across every archive in the run.
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
    private static CapturedLoadout Fake(int professionId, string tag) => new(
        ProfessionId:  professionId,
        ProjectName:   tag,
        TalentStageId: professionId * 100,
        Gear:          new List<int[]> { new[] { 200, professionId } },
        GearDetail:    new List<GearDetail>(),
        Skills:        new List<int[]>(),
        Fashion:       new List<Fashion>(),
        Modules:       new List<CapturedModule>());

    [Fact]
    public void SnapshotHoldsOneEntryPerDistinctClassPlayed()
    {
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "first-2"));
        capture.Capture(Fake(5, "only-5"));
        capture.Capture(Fake(2, "second-2"));   // revisits class 2

        var professions = capture.Snapshot().Select(l => l.ProfessionId).OrderBy(p => p);
        Assert.Equal(new[] { 2, 5 }, professions);
    }

    [Fact]
    public void RevisitingAClass_TheLatestCaptureWins()
    {
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "first-2"));
        capture.Capture(Fake(5, "only-5"));
        capture.Capture(Fake(2, "second-2"));

        var class2 = capture.Snapshot().Single(l => l.ProfessionId == 2);
        Assert.Equal("second-2", class2.ProjectName);
        // Class 5's own entry is untouched by the class-2 revisit.
        Assert.Equal("only-5", capture.Snapshot().Single(l => l.ProfessionId == 5).ProjectName);
    }

    [Fact]
    public void ResetForRun_ClearsEveryCapturedClass()
    {
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "first-2"));
        capture.Capture(Fake(5, "only-5"));

        capture.ResetForRun();

        Assert.Empty(capture.Snapshot());
    }

    [Fact]
    public void CaptureIsANoOpOnAnEmptyAccumulatorBeforeAnyPoll()
        => Assert.Empty(new LoadoutCapture().Snapshot());

    // --- run-boundary gate (Plugin.IsNewLoadoutRun) — reset only at true run START ---

    [Theory]
    [InlineData(0, 100, true)]     // town/boot -> entering a run: fresh accumulator for this run
    [InlineData(100, 200, true)]   // different run without going through 0 (crash / re-enter)
    [InlineData(100, 100, false)]  // same run, repeated poll: no-op
    [InlineData(100, 0, false)]    // leaving to town: KEEP data — the dungeon->town archive still reads it
    [InlineData(0, 0, false)]      // still not in a run
    public void IsNewLoadoutRun_MatchesRunBoundarySemantics(long previous, long next, bool expected)
        => Assert.Equal(expected, Plugin.IsNewLoadoutRun(previous, next));
}
