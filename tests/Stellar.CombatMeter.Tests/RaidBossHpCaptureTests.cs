using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Pure-decision tests for the 2026-08-26 raid-bosshp-capture-design plugin-side fixes (spec items
// 2/3/4): ReadHpPair's three-tier source order (Plugin.ReplayHp.cs's ResolveHpPair),
// tracked-union-stage boss doc membership (Plugin.BossHpMembership.cs's MergeBossMembership), and the
// MarkDead-on-native-zero OR decision (ShouldMarkBossDead). HpTimelineSampler's own sentinel-grid
// tests live in HpTimelineSamplerTests.cs.
public class RaidBossHpCaptureTests
{
    // Low 16 bits = 640 → EntityId.IsPlayer. Mirrors ImagineCastTests' PlayerA convention.
    private static EntityId Player(long uid) => new((uid << 16) | 640);
    // Low 16 bits = 64 → EntityId.IsMonster (one of the two monster markers).
    private static EntityId Monster(long uid) => new((uid << 16) | 64);

    private static EntityVitals Known(long hp, long maxHp) => new(hp, maxHp, IsKnown: true) { HasHpObservation = true };
    private static EntityVitals MaxHpOnly(long maxHp) => new(0, maxHp, IsKnown: true) { HasHpObservation = false };
    private static readonly EntityVitals Unknown = EntityVitals.Unknown;

    // ── ResolveHpPair (spec item 2 / recon §5.2 + L4 + false-0%) ───────────────────────────────────

    [Fact]
    public void ResolveHpPair_NativeTap_AlwaysWins_RepresentedAsPctOver100()
    {
        // Even when wire vitals disagree, the native tap is tier ① and wins outright.
        var (hp, maxHp, src) = Plugin.ResolveHpPair(hasNativeBlood: true, nativePct: 37, Known(1, 2), attrMaxHpTotal: 999);
        Assert.Equal((37L, 100L, "native"), (hp, maxHp, src));
    }

    [Fact]
    public void ResolveHpPair_NativeZero_IsUsable_NotSentinel()
    {
        // pct=0 from the native tap is a REAL, usable reading (maxHp=100 > 0) — the sampler will
        // record it as a sample, not a sentinel. Distinguishing a genuine 0% from "no observation" is
        // exactly ShouldMarkBossDead/IsNativeBossZero's job downstream, not ResolveHpPair's.
        var (hp, maxHp, src) = Plugin.ResolveHpPair(hasNativeBlood: true, nativePct: 0, Unknown, attrMaxHpTotal: 0);
        Assert.Equal((0L, 100L, "native"), (hp, maxHp, src));
    }

    [Fact]
    public void ResolveHpPair_WireVitals_UsedWhenNativeAbsent_AndHasHpObservation()
    {
        var (hp, maxHp, src) = Plugin.ResolveHpPair(hasNativeBlood: false, nativePct: 0, Known(40, 100), attrMaxHpTotal: 0);
        Assert.Equal((40L, 100L, "vitals"), (hp, maxHp, src));
    }

    [Fact]
    public void ResolveHpPair_MaxHpOnlyObservation_IsUnusable_RegardlessOfAttrFallback()
    {
        // The false-0% defect: HasHpObservation=false ("alive, HP unknown") must NEVER be read as a
        // usable pair — not even when the attr map happens to carry a maxHp fallback. Gated on
        // HasHpObservation FIRST, before either MaxHp source is even inspected.
        var (hp, maxHp, src) = Plugin.ResolveHpPair(hasNativeBlood: false, nativePct: 0, MaxHpOnly(500), attrMaxHpTotal: 500);
        Assert.Equal((0L, 0L, "none"), (hp, maxHp, src));
    }

    [Fact]
    public void ResolveHpPair_AttrFallback_PairsWithKnownHp_WhenWireMaxHpNeverArrived()
    {
        // Hp IS known (HasHpObservation true) but the vitals row's own MaxHp never arrived — tier ③
        // pairs the already-known Hp with the generic attr map's AttrMaxHpTotal(11321).
        var (hp, maxHp, src) = Plugin.ResolveHpPair(hasNativeBlood: false, nativePct: 0, Known(25, 0), attrMaxHpTotal: 200);
        Assert.Equal((25L, 200L, "attr11321"), (hp, maxHp, src));
    }

    [Fact]
    public void ResolveHpPair_NoSourceUsable_ReturnsUnusable()
    {
        var (hp, maxHp, src) = Plugin.ResolveHpPair(hasNativeBlood: false, nativePct: 0, Unknown, attrMaxHpTotal: 0);
        Assert.Equal((0L, 0L, "none"), (hp, maxHp, src));
    }

    [Fact]
    public void ResolveHpPair_KnownHpNoMaxHpAnywhere_ReturnsUnusable()
    {
        var (hp, maxHp, src) = Plugin.ResolveHpPair(hasNativeBlood: false, nativePct: 0, Known(10, 0), attrMaxHpTotal: 0);
        Assert.Equal((0L, 0L, "none"), (hp, maxHp, src));
    }

    // ── MergeBossMembership (spec item 3 / recon L3) ────────────────────────────────────────────────

    [Fact]
    public void MergeBossMembership_UnionsTrackedBossNotInStageSet()
    {
        var stageBosses = new List<(EntityId Id, int ConfigId, bool Killed)> { (Monster(1), 100, false) };
        var tracked = new long[] { Monster(1).Value, Monster(2).Value };   // Monster(2) fell out of the stage set
        var result = Plugin.MergeBossMembership(stageBosses, tracked,
            id => id == Monster(2) ? (true, 200) : null);

        Assert.Equal(2, result.Count);
        Assert.Contains((Monster(1), 100), result);
        Assert.Contains((Monster(2), 200), result);
    }

    [Fact]
    public void MergeBossMembership_NeverDuplicates_AnIdAlreadyInTheStageSet()
    {
        var stageBosses = new List<(EntityId Id, int ConfigId, bool Killed)> { (Monster(1), 100, false) };
        var tracked = new long[] { Monster(1).Value };
        var result = Plugin.MergeBossMembership(stageBosses, tracked, _ => (true, 999));

        Assert.Single(result);
        Assert.Equal((Monster(1), 100), result[0]);   // configId from the stage set, not the (unreached) lookup
    }

    [Fact]
    public void MergeBossMembership_ExcludesElites_EvenWhenTracked()
    {
        var stageBosses = new List<(EntityId Id, int ConfigId, bool Killed)>();
        var tracked = new long[] { Monster(5).Value };
        // lookupMonster reports a real monster but NOT a boss (isBoss:false) — the elite/non-boss case.
        var result = Plugin.MergeBossMembership(stageBosses, tracked, _ => (false, 500));

        Assert.Empty(result);
    }

    [Fact]
    public void MergeBossMembership_ExcludesPlayers_WithoutConsultingLookup()
    {
        var stageBosses = new List<(EntityId Id, int ConfigId, bool Killed)>();
        var tracked = new long[] { Player(7).Value };
        var lookupCalled = false;
        var result = Plugin.MergeBossMembership(stageBosses, tracked, _ => { lookupCalled = true; return (true, 1); });

        Assert.Empty(result);
        Assert.False(lookupCalled);   // players are filtered before the monster-info lookup ever runs
    }

    [Fact]
    public void MergeBossMembership_ExcludesUnresolvedMonsterInfo()
    {
        // A tracked non-player id the replay never snapshotted MonsterInfo for (lookupMonster
        // returns null) — must not be invented into the boss set.
        var stageBosses = new List<(EntityId Id, int ConfigId, bool Killed)>();
        var tracked = new long[] { Monster(9).Value };
        var result = Plugin.MergeBossMembership(stageBosses, tracked, _ => null);

        Assert.Empty(result);
    }

    [Fact]
    public void MergeBossMembership_PreservesStageSetOrder_ThenExtraTrackedOrder()
    {
        var stageBosses = new List<(EntityId Id, int ConfigId, bool Killed)>
        {
            (Monster(2), 20, false),
            (Monster(1), 10, false),
        };
        var tracked = new long[] { Monster(4).Value, Monster(3).Value };
        var result = Plugin.MergeBossMembership(stageBosses, tracked, _ => (true, 40));

        Assert.Equal(new[] { Monster(2), Monster(1), Monster(4), Monster(3) },
            new[] { result[0].id, result[1].id, result[2].id, result[3].id });
    }

    [Fact]
    public void MergeBossMembership_EmptyStageSetAndEmptyTracked_ReturnsEmpty()
    {
        var result = Plugin.MergeBossMembership(
            new List<(EntityId Id, int ConfigId, bool Killed)>(), System.Array.Empty<long>(), _ => (true, 1));
        Assert.Empty(result);
    }

    // ── ShouldMarkBossDead (spec item 4) ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ShouldMarkBossDead_IsAnOr(bool stickyKilled, bool nativeZero, bool expected)
        => Assert.Equal(expected, Plugin.ShouldMarkBossDead(stickyKilled, nativeZero));
}
