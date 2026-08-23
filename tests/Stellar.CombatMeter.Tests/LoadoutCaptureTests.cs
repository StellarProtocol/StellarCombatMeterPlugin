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
    // need a genuinely DIFFERENT setup pass a distinct value. imagines defaults to null (unsynced) —
    // matching every pre-existing fixture's implicit "no Imagine data" shape.
    private static CapturedLoadout Fake(int professionId, string tag, int? gearItemId = null, IReadOnlyList<int>? imagines = null) => new(
        ProfessionId:  professionId,
        ProjectName:   tag,
        TalentStageId: professionId * 100,
        Gear:          new List<int[]> { new[] { 200, gearItemId ?? professionId } },
        GearDetail:    new List<GearDetail>(),
        Skills:        new List<int[]>(),
        Fashion:       new List<Fashion>(),
        Modules:       new List<CapturedModule>(),
        Imagines:      imagines);

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
    public void MidRunClassSwitch_FoughtEntrySkillsAndAbilityScore_NeverRewrittenBySameIdentityRefresh()
    {
        // Owner staging run sea/ZdTH3UwZQ6 (the chimera setup): during a class-switch's
        // SelfGearChanged burst, TickGearRecapture ran while attr 220 still read the OLD profession —
        // the slot-keyed gear/talents were still frost, so SameSetup was true — while the LIVE self
        // reads had already flipped to the new class (GetSkillLevels served the tank list,
        // GetFightPoint its 34840 score), and the wholesale in-place refresh poisoned the FOUGHT
        // frost entry with tank skills/AS. Once fought, an entry's Skills/AbilityScore/Attributes
        // stay frozen at capture; only unfought drafts keep refreshing wholesale.
        var frostSkills = new List<int[]> { new[] { 1801, 5, 1 }, new[] { 1802, 5, 0 } };
        var tankSkills  = new List<int[]> { new[] { 2901, 4, 0 } };
        var frostAttrs  = new List<long[]> { new long[] { 220, 12 } };
        var tankAttrs   = new List<long[]> { new long[] { 220, 9 } };

        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "frost") with { Skills = frostSkills, AbilityScore = 53966, Attributes = frostAttrs }, combatMarker: 0);
        // The fight advanced the marker; the switch-burst recapture carries the SAME identity
        // (gear/modules/talents unchanged) but the NEW class's live skills/AS/attrs.
        capture.Capture(Fake(2, "frost") with { Skills = tankSkills, AbilityScore = 34840, Attributes = tankAttrs }, combatMarker: 7);

        var entry = capture.Snapshot().Single();
        Assert.Equal(frostSkills, entry.Skills);       // fought skills kept — never the switch-burst tank list
        Assert.Equal(53966, entry.AbilityScore);       // fought per-class score kept
        Assert.Equal(frostAttrs, entry.Attributes);    // fought attribute sheet kept
    }

    [Fact]
    public void MidRunClassSwitch_FoughtEntry_EmptyToPopulatedImagineBackfill_StillWorks()
    {
        // The ONE refresh a fought entry may still take (empty-is-no-signal pin): the 1 Hz resonance
        // poll landing after the fight backfills Imagines []→populated — while the frozen fields
        // stay frozen at their fought values.
        var frostSkills = new List<int[]> { new[] { 1801, 5, 1 } };
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "frost", imagines: System.Array.Empty<int>()) with { Skills = frostSkills, AbilityScore = 53966 }, combatMarker: 0);
        capture.Capture(Fake(2, "frost", imagines: new[] { 10084, 10085 }) with { Skills = new List<int[]>(), AbilityScore = 0 }, combatMarker: 7);

        var entry = capture.Snapshot().Single();
        Assert.Equal(new[] { 10084, 10085 }, entry.Imagines);   // backfill still lands on a fought entry
        Assert.Equal(frostSkills, entry.Skills);                // frozen fields stay frozen
        Assert.Equal(53966, entry.AbilityScore);
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
    // Per-setup ACTIVATION TIMELINE (owner-approved feature, 2026-08-23): a ServerNowMs stamp (the
    // classSpans timebase) is appended when a setup BECOMES the equipped identity — at mint, on a
    // draft replacement (the survivor), and on a swap-back re-match — never on a same-identity
    // refresh of the already-active entry. The SWAP moment, not first-fought (owner ruling: players
    // swap pre-run and between clear and boss phases; the span must start at the swap).
    // -------------------------------------------------------------------------

    [Fact]
    public void Activation_MintStampsExactlyOnce()
    {
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "A"), combatMarker: 0, nowMs: 1000);

        Assert.Equal(new long[] { 1000 }, capture.Snapshot().Single().Activations);
    }

    [Fact]
    public void Activation_SameIdentityRefreshWhileActive_StampsNothing()
    {
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "A"), combatMarker: 0, nowMs: 1000);
        capture.Capture(Fake(2, "A-refresh"), combatMarker: 0, nowMs: 2000);    // unfought refresh
        capture.Capture(Fake(2, "A-refresh2"), combatMarker: 4, nowMs: 3000);   // fought refresh

        Assert.Equal(new long[] { 1000 }, capture.Snapshot().Single().Activations);
    }

    [Fact]
    public void Activation_SwapBack_ReactivatesTheOriginalEntry_BStampedBetween()
    {
        // A (mint t=1000) -> fight -> B (mint t=5000) -> fight -> back to A (t=9000): A's SINGLE
        // entry carries both activations (no duplicate A entry minted), B keeps its one, and the
        // re-activated A becomes the class's LAST entry — the active slot the top-level mirrors
        // (ResolveLoadoutFields / ResolveSelfEquipment) read as "currently equipped".
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "A", gearItemId: 500), combatMarker: 0, nowMs: 1000);
        capture.Capture(Fake(2, "B", gearItemId: 400), combatMarker: 3, nowMs: 5000);
        capture.Capture(Fake(2, "A2", gearItemId: 500), combatMarker: 7, nowMs: 9000);

        var entries = capture.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal(400, entries[0].Gear[0][1]);
        Assert.Equal(new long[] { 5000 }, entries[0].Activations);         // B — stamped between
        Assert.Equal(500, entries[1].Gear[0][1]);
        Assert.Equal(new long[] { 1000, 9000 }, entries[1].Activations);   // A re-activated, now last
    }

    [Fact]
    public void Activation_DraftReplacement_StampsTheSurvivorOnly_DeadDraftStampsDie()
    {
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "draft-A", gearItemId: 500), combatMarker: 0, nowMs: 1000);
        capture.Capture(Fake(2, "draft-B", gearItemId: 400), combatMarker: 0, nowMs: 2000);   // replaces A

        var entry = capture.Snapshot().Single();
        Assert.Equal(400, entry.Gear[0][1]);
        Assert.Equal(new long[] { 2000 }, entry.Activations);   // only the survivor's stamp — A's died with it
    }

    [Fact]
    public void Activation_FoughtFreeze_DoesNotBlockActivationAppends()
    {
        // FIX B (owner run sea/ZdTH3UwZQ6) freezes a fought entry's Skills/AbilityScore/Attributes on
        // a same-identity refresh — the swap-back RE-ACTIVATION must still append its stamp while the
        // frozen fields stay frozen at the fought capture.
        var frostSkills = new List<int[]> { new[] { 1801, 5, 1 } };
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "A", gearItemId: 500) with { Skills = frostSkills, AbilityScore = 53966 }, combatMarker: 0, nowMs: 1000);
        capture.Capture(Fake(2, "B", gearItemId: 400), combatMarker: 3, nowMs: 5000);
        capture.Capture(Fake(2, "A2", gearItemId: 500) with { Skills = new List<int[]>(), AbilityScore = 0 }, combatMarker: 7, nowMs: 9000);

        var a = capture.Snapshot().Single(l => l.Gear[0][1] == 500);
        Assert.Equal(new long[] { 1000, 9000 }, a.Activations);   // activation appended
        Assert.Equal(frostSkills, a.Skills);                       // frozen fields stay frozen
        Assert.Equal(53966, a.AbilityScore);
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

    // -------------------------------------------------------------------------
    // Equipped Battle Imagines join the setup identity (owner gap, run B47O8jx6wp retest,
    // 2026-08-22): swapping the equipped pair (e.g. Predator Spider -> Muku Chief) is a content
    // change exactly like a gear/module/talent edit, so it goes through the SAME fought-with-vs-
    // unfought-draft decision table in LoadoutCapture.Capture — these fixtures mirror the
    // gear-based ones above but vary Imagines only.
    // -------------------------------------------------------------------------

    [Fact]
    public void SameSetup_ImagineOrderMatters_PermutedPairIsADifferentSetup()
    {
        // Slot X and slot Z are distinct positions — Imagines is the one component in SameSetup that
        // is ORDER-SENSITIVE (gear/talent-node sets are permutation-tolerant).
        var a = Fake(2, "x", imagines: new[] { 10084, 10085 });
        var b = Fake(2, "x", imagines: new[] { 10085, 10084 });   // same ids, swapped slots
        Assert.False(LoadoutCapture.SameSetup(a, b));

        var same = Fake(2, "x", imagines: new[] { 10084, 10085 });
        Assert.True(LoadoutCapture.SameSetup(a, same));
    }

    [Fact]
    public void SameSetup_ImagineDifference_IsADifferentSetup_EvenWithIdenticalGear()
    {
        var a = Fake(2, "x", imagines: new[] { 10084, 10085 });   // Predator Spider, Muku Chief
        var b = Fake(2, "x", imagines: new[] { 10084, 10086 });   // slot Z swapped to a third Imagine
        Assert.False(LoadoutCapture.SameSetup(a, b));
    }

    [Fact]
    public void ImagineSwap_FoughtWith_ThenSwapped_PreservesBothEntriesInOrder()
    {
        // Fight with Predator Spider+Muku Chief (marker=0), then swap to Muku Chief+a third Imagine
        // AFTER combat happened (marker advances to 3) — mirrors FoughtWithSetup_ThenChanged above,
        // but the only thing that differs between the two captures is Imagines.
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "A", imagines: new[] { 10084, 10085 }), combatMarker: 0);
        capture.Capture(Fake(2, "B", imagines: new[] { 10085, 10086 }), combatMarker: 3);

        var entries = capture.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal(new[] { 10084, 10085 }, entries[0].Imagines);
        Assert.Equal(new[] { 10085, 10086 }, entries[1].Imagines);
    }

    [Fact]
    public void ImagineSwap_NoCombatSince_ReplacesRatherThanAppending()
    {
        // Same marker on both calls: swapping Imagines while just browsing (no fight in between) is
        // an unfought draft, not a fought-with setup — mirrors UnfoughtDraft_DifferentContent above.
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "A", imagines: new[] { 10084, 10085 }), combatMarker: 0);
        capture.Capture(Fake(2, "B", imagines: new[] { 10085, 10086 }), combatMarker: 0);

        var entries = capture.Snapshot();
        Assert.Single(entries);
        Assert.Equal(new[] { 10085, 10086 }, entries[0].Imagines);
    }

    [Fact]
    public void ImagineSwap_SamePairRecaptured_RefreshesInPlace_NeverAppends()
    {
        // Re-equipping the IDENTICAL Imagine pair (e.g. a refresh from ApplyLiveEquipment / a
        // no-op tick poll) must never mint a second entry — content identity is unchanged.
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "A", imagines: new[] { 10084, 10085 }), combatMarker: 0);
        capture.Capture(Fake(2, "B", imagines: new[] { 10084, 10085 }), combatMarker: 9);   // marker moved, same pair

        var entries = capture.Snapshot();
        Assert.Single(entries);
        Assert.Equal("B", entries[0].ProjectName);
        Assert.Equal(new[] { 10084, 10085 }, entries[0].Imagines);
    }

    // -------------------------------------------------------------------------
    // Login-order race (review finding, 2026-08-22): IResonanceState.Installed starts [] right after
    // login and only populates via a 1 Hz latched poll, while the combat marker can advance on the
    // very first hit. Without the empty-is-no-signal rule, run-start capture (Imagines=[]) + a fight
    // advancing the marker + the 1 Hz probe landing (Installed flips []->[real pair]) looked exactly
    // like "different content, marker advanced" -> APPEND, minting a phantom second setup that differs
    // from the first ONLY by empty->real Imagines, with no actual swap. Pinned here so it can never
    // regress; see LoadoutCapture.ImaginesDiffer / SameSetup docs for the rule.
    // -------------------------------------------------------------------------

    [Fact]
    public void ImagineSentinel_UnsyncedAtRunStart_ThenPopulatedAfterCombat_HealsInPlace_NeverAppends()
    {
        // Exact login sequence from the finding: capture with imagines=[] while marker is still at its
        // starting value, a fight advances the marker, then the 1 Hz resonance probe lands and the
        // recapture carries the real pair — everything else (gear/modules/talents) identical throughout.
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "A", imagines: System.Array.Empty<int>()), combatMarker: 0);
        capture.Capture(Fake(2, "A", imagines: new[] { 10084, 10085 }), combatMarker: 3);   // marker advanced by the fight

        var entries = capture.Snapshot();
        Assert.Single(entries);   // NOT two — the []->populated transition must never mint a phantom setup
        Assert.Equal(new[] { 10084, 10085 }, entries[0].Imagines);
    }

    [Fact]
    public void ImagineSentinel_NullAtRunStart_ThenPopulatedAfterCombat_HealsInPlace_NeverAppends()
    {
        // Same race, but the first capture's Imagines is null rather than an empty array (SameIntSequence
        // already treats null as empty; ImaginesDiffer must too).
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "A", imagines: null), combatMarker: 0);
        capture.Capture(Fake(2, "A", imagines: new[] { 10084, 10085 }), combatMarker: 3);

        var entries = capture.Snapshot();
        Assert.Single(entries);
        Assert.Equal(new[] { 10084, 10085 }, entries[0].Imagines);
    }

    [Fact]
    public void ImagineSentinel_BothSidesNonEmptyAndDiffering_AfterCombat_StillAppends()
    {
        // Guard against overcorrecting: a REAL swap (both sides non-empty, genuinely different) after
        // combat must still append a new entry — this is ImagineSwap_FoughtWith_ThenSwapped's exact
        // shape, re-pinned here alongside the empty-side fix so the two behaviors are compared side by
        // side and neither can quietly weaken the other.
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "A", imagines: new[] { 10084, 10085 }), combatMarker: 0);
        capture.Capture(Fake(2, "B", imagines: new[] { 10085, 10086 }), combatMarker: 3);

        var entries = capture.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal(new[] { 10084, 10085 }, entries[0].Imagines);
        Assert.Equal(new[] { 10085, 10086 }, entries[1].Imagines);
    }

    [Fact]
    public void ImaginesDiffer_EmptyEitherSide_IsNeverADifference()
    {
        Assert.False(LoadoutCapture.ImaginesDiffer(System.Array.Empty<int>(), new[] { 1, 2 }));
        Assert.False(LoadoutCapture.ImaginesDiffer(new[] { 1, 2 }, System.Array.Empty<int>()));
        Assert.False(LoadoutCapture.ImaginesDiffer(null, new[] { 1, 2 }));
        Assert.False(LoadoutCapture.ImaginesDiffer(null, null));
        Assert.False(LoadoutCapture.ImaginesDiffer(System.Array.Empty<int>(), System.Array.Empty<int>()));
    }

    [Fact]
    public void ImaginesDiffer_BothNonEmpty_MatchesSameIntSequence()
    {
        Assert.False(LoadoutCapture.ImaginesDiffer(new[] { 1, 2 }, new[] { 1, 2 }));
        Assert.True(LoadoutCapture.ImaginesDiffer(new[] { 1, 2 }, new[] { 2, 1 }));
        Assert.True(LoadoutCapture.ImaginesDiffer(new[] { 1 }, new[] { 1, 2 }));
    }

    // -------------------------------------------------------------------------
    // LastImagines — the cheap tick-time comparison seam TickImagineRecapture polls against.
    // -------------------------------------------------------------------------

    [Fact]
    public void LastImagines_NoEntryYet_IsEmpty()
        => Assert.Empty(new LoadoutCapture().LastImagines(2));

    [Fact]
    public void LastImagines_ReturnsTheNewestEntrysPair_NotAnEarlierOne()
    {
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "A", imagines: new[] { 10084, 10085 }), combatMarker: 0);
        capture.Capture(Fake(2, "B", imagines: new[] { 10085, 10086 }), combatMarker: 3);   // fought-with -> appended

        Assert.Equal(new[] { 10085, 10086 }, capture.LastImagines(2));
    }

    [Fact]
    public void SameIntSequence_OrderSensitive_NullTreatedAsEmpty()
    {
        Assert.True(LoadoutCapture.SameIntSequence(null, System.Array.Empty<int>()));
        Assert.True(LoadoutCapture.SameIntSequence(new[] { 1, 2 }, new[] { 1, 2 }));
        Assert.False(LoadoutCapture.SameIntSequence(new[] { 1, 2 }, new[] { 2, 1 }));
        Assert.False(LoadoutCapture.SameIntSequence(new[] { 1 }, new[] { 1, 2 }));
    }
}
