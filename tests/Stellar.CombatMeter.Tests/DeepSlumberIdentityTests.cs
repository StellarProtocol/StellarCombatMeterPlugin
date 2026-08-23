using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// PINNED — Deep-Slumber (Psychoscope) membership in the per-setup capture identity.
///
/// <para>Owner ruling (CLAUDE.md, verbatim): <em>"when any equipment change such as
/// module,talents,equipments,slumberdream etc., and use have a combat with that setup it require plugin
/// to take snapshot of it even class has no change."</em> Deep-Slumber was never wired into that
/// identity, so the psychoscope was the one "slumberdream" change that could not mint a setup.</para>
///
/// <para><b>THE BUG</b> (owner staging run <c>sea/dXkw1PSyOG</c>, 2026-08-23): archive 1 was fought with
/// a Deep-Slumber factor UNEQUIPPED, archive 2 with it RE-EQUIPPED. Measured on the upload,
/// <c>actors[81789846144].loadouts.length == 1</c> with a single activation stamp — one setup for two
/// materially different builds, and no per-setup psychoscope on the wire at all.</para>
/// </summary>
public class DeepSlumberIdentityTests
{
    // ── Fixtures: one line, two areas, mirroring the owner's real container shape ─────────────

    private static DeepSlumberState Slumber(int middleFactorItemId, bool areaFiveActive = false, long score = 46, int seasonLevel = 100) =>
        new(
            new[] { new[] { 2, seasonLevel }, new[] { 3, 65 } },
            new[]
            {
                new DeepSlumberLine(2, 800522, new[]
                {
                    new DeepSlumberArea(1, true, score,
                        new[] { new[] { 24, 3950 }, new[] { 25, 3905 } },
                        new[] { new[] { 100, middleFactorItemId }, new[] { 101, 20010930 } },
                        new[] { new[] { 1008, 1 }, new[] { 1001, 1 } }),
                    new DeepSlumberArea(5, areaFiveActive, 20,
                        Array.Empty<int[]>(),
                        new[] { new[] { 140, 20010881 } },
                        new[] { new[] { 1403, 1 } }),
                }),
                new DeepSlumberLine(2, 800523, new[]
                {
                    new DeepSlumberArea(20002, true, 0,
                        Array.Empty<int[]>(),
                        new[] { new[] { 193, 20010224 } },
                        new[] { new[] { 5105, 1 } }),
                }),
            });

    // The SAME psychoscope, serialized in a different order at every level — exactly what Lua `pairs`
    // over the game's zcontainer maps legitimately produces between two reads.
    private static DeepSlumberState SlumberReordered(int middleFactorItemId) =>
        new(
            new[] { new[] { 3, 65 }, new[] { 2, 100 } },
            new[]
            {
                new DeepSlumberLine(2, 800523, new[]
                {
                    new DeepSlumberArea(20002, true, 0,
                        Array.Empty<int[]>(),
                        new[] { new[] { 193, 20010224 } },
                        new[] { new[] { 5105, 1 } }),
                }),
                new DeepSlumberLine(2, 800522, new[]
                {
                    new DeepSlumberArea(5, false, 20,
                        Array.Empty<int[]>(),
                        new[] { new[] { 140, 20010881 } },
                        new[] { new[] { 1403, 1 } }),
                    new DeepSlumberArea(1, true, 46,
                        new[] { new[] { 25, 3905 }, new[] { 24, 3950 } },
                        new[] { new[] { 101, 20010930 }, new[] { 100, middleFactorItemId } },
                        new[] { new[] { 1001, 1 }, new[] { 1008, 1 } }),
                }),
            });

    private const int FactorEquipped = 20010940;
    private const int FactorRemoved = 0;

    private static CapturedLoadout Fake(int professionId, string tag, DeepSlumberState? slumber = null) => new(
        ProfessionId:  professionId,
        ProjectName:   tag,
        TalentStageId: professionId * 100,
        Gear:          new List<int[]> { new[] { 200, professionId } },
        GearDetail:    new List<GearDetail>(),
        Skills:        new List<int[]>(),
        Fashion:       new List<Fashion>(),
        Modules:       new List<CapturedModule>(),
        DeepSlumber:   slumber);

    // ── The pure identity projection ──────────────────────────────────────────────────────────

    [Fact]
    public void ReorderedSerializationOfTheSamePsychoscope_IsNotADifference()
        => Assert.False(DeepSlumberIdentity.Differs(Slumber(FactorEquipped), SlumberReordered(FactorEquipped)));

    [Fact]
    public void UnEquippingAMiddleNodeFactor_IsADifference()
        => Assert.True(DeepSlumberIdentity.Differs(Slumber(FactorEquipped), Slumber(FactorRemoved)));

    [Fact]
    public void EnablingAnArea_IsADifference()
        => Assert.True(DeepSlumberIdentity.Differs(
            Slumber(FactorEquipped, areaFiveActive: false),
            Slumber(FactorEquipped, areaFiveActive: true)));

    /// <summary>Score is DERIVED from the allocation and season level is PROGRESSION — neither is
    /// something the player equips, so neither may split one setup into two. (The framework's own change
    /// event deliberately reports both; over-reporting there costs a re-capture, this is where it stops.)</summary>
    [Fact]
    public void DerivedScoreAndSeasonLevel_AreNotIdentity()
    {
        Assert.False(DeepSlumberIdentity.Differs(Slumber(FactorEquipped, score: 46), Slumber(FactorEquipped, score: 77)));
        Assert.False(DeepSlumberIdentity.Differs(Slumber(FactorEquipped, seasonLevel: 100), Slumber(FactorEquipped, seasonLevel: 101)));
    }

    /// <summary>NO-SIGNAL, both directions: an unread (null) or error-empty psychoscope may neither mint
    /// a setup nor block one. Same rule as the Imagine sentinel.</summary>
    [Fact]
    public void AnUnreadOrEmptyPsychoscope_IsNoSignal()
    {
        var empty = new DeepSlumberState(Array.Empty<int[]>(), Array.Empty<DeepSlumberLine>());
        Assert.False(DeepSlumberIdentity.HasSignal(null));
        Assert.False(DeepSlumberIdentity.HasSignal(empty));
        Assert.False(DeepSlumberIdentity.Differs(null, Slumber(FactorEquipped)));
        Assert.False(DeepSlumberIdentity.Differs(Slumber(FactorEquipped), null));
        Assert.False(DeepSlumberIdentity.Differs(empty, Slumber(FactorEquipped)));
        Assert.False(DeepSlumberIdentity.Differs(Slumber(FactorEquipped), empty));
    }

    // ── The identity digest (the owner's in-town, no-run proof term) ──────────────────────────

    [Fact]
    public void TheIdentityDigest_MovesWhenThePsychoscopeMoves()
    {
        var equipped = LoadoutCapture.IdentityDigest(Fake(2, "a", Slumber(FactorEquipped)));
        var removed = LoadoutCapture.IdentityDigest(Fake(2, "a", Slumber(FactorRemoved)));
        Assert.NotEqual(equipped, removed);
    }

    [Fact]
    public void TheIdentityDigest_IsStableAcrossAReorderedRead()
        => Assert.Equal(
            LoadoutCapture.IdentityDigest(Fake(2, "a", Slumber(FactorEquipped))),
            LoadoutCapture.IdentityDigest(Fake(2, "a", SlumberReordered(FactorEquipped))));

    /// <summary>A no-signal psychoscope folds NOTHING, so the digest agrees with
    /// <c>SameSetup</c> across an unread→read heal: neither reports a change.</summary>
    [Fact]
    public void ANoSignalPsychoscope_FoldsNothingIntoTheDigest()
        => Assert.Equal(
            LoadoutCapture.IdentityDigest(Fake(2, "a", null)),
            LoadoutCapture.IdentityDigest(Fake(2, "a", new DeepSlumberState(Array.Empty<int[]>(), Array.Empty<DeepSlumberLine>()))));

    // ── THE OWNER SCENARIO, through the accumulator ───────────────────────────────────────────

    /// <summary>Run <c>sea/dXkw1PSyOG</c>, end to end: fight with the factor equipped, UNEQUIP it and
    /// fight again → a SECOND setup is minted (the fought-with first one is preserved); RE-EQUIP it and
    /// fight again → the earlier setup is RE-ACTIVATED, not duplicated, and moves to the class's active
    /// slot. Before this fix all three captures compared identical and the run uploaded ONE setup.</summary>
    [Fact]
    public void UnEquippingAFactorMintsASecondSetup_AndReEquippingRematchesTheFirst()
    {
        var capture = new LoadoutCapture();

        Assert.Equal(CaptureDecision.Minted,
            capture.Capture(Fake(2, "with-factor", Slumber(FactorEquipped)), combatMarker: 1, nowMs: 1000));

        // Combat happened with that setup (marker advanced), then the factor came off.
        Assert.Equal(CaptureDecision.Minted,
            capture.Capture(Fake(2, "no-factor", Slumber(FactorRemoved)), combatMarker: 2, nowMs: 2000));

        // Combat happened with THAT setup too, then the factor went back on — a swap-back.
        Assert.Equal(CaptureDecision.Rematched,
            capture.Capture(Fake(2, "with-factor-again", Slumber(FactorEquipped)), combatMarker: 3, nowMs: 3000));

        var entries = capture.Snapshot().Where(l => l.ProfessionId == 2).ToList();
        Assert.Equal(2, entries.Count);
        // Last = the class's ACTIVE setup: the re-equipped one, carrying BOTH its activation stamps.
        Assert.Equal(new long[] { 1000, 3000 }, entries[^1].Activations!.ToArray());
        Assert.Equal(new long[] { 2000 }, entries[0].Activations!.ToArray());
        Assert.False(DeepSlumberIdentity.Differs(entries[^1].DeepSlumber, Slumber(FactorEquipped)));
        Assert.False(DeepSlumberIdentity.Differs(entries[0].DeepSlumber, Slumber(FactorRemoved)));
    }

    /// <summary>An UNFOUGHT psychoscope edit (no combat since the last capture) still replaces the draft
    /// in place — the existing draft rule, unchanged by Deep-Slumber joining the identity.</summary>
    [Fact]
    public void AnUnfoughtPsychoscopeEdit_ReplacesTheDraft()
    {
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "draft", Slumber(FactorEquipped)), combatMarker: 7, nowMs: 1000);

        Assert.Equal(CaptureDecision.ReplacedDraft,
            capture.Capture(Fake(2, "edited", Slumber(FactorRemoved)), combatMarker: 7, nowMs: 2000));
        Assert.Single(capture.Snapshot());
    }

    /// <summary>A capture whose psychoscope read has not landed can NEVER mint: it is no-signal, so the
    /// setup compares identical and refreshes in place. This is the property that stops the framework's
    /// first DS read (or a failed cultivate walk) from inventing a phantom second setup.</summary>
    [Fact]
    public void ADeepSlumberAbsentCapture_NeverMints()
    {
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "fought", Slumber(FactorEquipped)), combatMarker: 1, nowMs: 1000);

        Assert.Equal(CaptureDecision.RefreshedSame,
            capture.Capture(Fake(2, "ds-not-read-yet", null), combatMarker: 2, nowMs: 2000));
        Assert.Single(capture.Snapshot());
    }

    /// <summary>…and the fought-with psychoscope is never BLANKED by that unresolved refresh — the same
    /// never-wipe rule the Imagine pair follows in <c>RefreshFought</c>.</summary>
    [Fact]
    public void AnUnresolvedRefresh_NeverBlanksAFoughtPsychoscope()
    {
        var capture = new LoadoutCapture();
        capture.Capture(Fake(2, "fought", Slumber(FactorEquipped)), combatMarker: 1, nowMs: 1000);
        capture.Capture(Fake(2, "ds-not-read-yet", null), combatMarker: 2, nowMs: 2000);

        Assert.False(DeepSlumberIdentity.Differs(capture.Snapshot()[0].DeepSlumber, Slumber(FactorEquipped)));
    }

    /// <summary>The archive-time live overlay follows the same never-blank rule: an unresolved live read
    /// keeps the psychoscope the fight was captured with.</summary>
    [Fact]
    public void PreferReadDeepSlumber_NeverOverwritesAReadStateWithAnUnreadOne()
    {
        var read = Slumber(FactorEquipped);
        var empty = new DeepSlumberState(Array.Empty<int[]>(), Array.Empty<DeepSlumberLine>());
        Assert.Same(read, Plugin.PreferReadDeepSlumber(null, read));
        Assert.Same(read, Plugin.PreferReadDeepSlumber(empty, read));
        Assert.Same(read, Plugin.PreferReadDeepSlumber(read, null));
    }

    // ── The wire: per-setup `deepSlumber`, same shape as the actor-level block ────────────────

    [Fact]
    public void EachSetupCarriesItsOwnDeepSlumberBlock_OnTheWire()
    {
        var runLoadouts = new List<CapturedLoadout>
        {
            Fake(2, "with-factor", Slumber(FactorEquipped)),
            Fake(2, "no-factor", Slumber(FactorRemoved)),
        };

        var entries = CombatLogAssembler.BuildLoadoutEntries(runLoadouts)!;
        Assert.Equal(2, entries.Count);
        Assert.NotNull(entries[0].DeepSlumber);
        Assert.NotNull(entries[1].DeepSlumber);
        // Same shape as the actor-level block: line/subType variants preserved 1:1.
        Assert.Equal(2, entries[0].DeepSlumber!.Lines.Count);
        Assert.Equal(FactorEquipped, entries[0].DeepSlumber!.Lines[0].Areas[0].Mid[0][1]);
        Assert.Equal(FactorRemoved, entries[1].DeepSlumber!.Lines[0].Areas[0].Mid[0][1]);
    }

    [Fact]
    public void ASetupCapturedBeforeTheDeepSlumberReadLanded_OmitsTheKey()
    {
        var entries = CombatLogAssembler.BuildLoadoutEntries(new List<CapturedLoadout> { Fake(2, "no-ds", null) })!;
        Assert.Null(entries[0].DeepSlumber);
    }
}
