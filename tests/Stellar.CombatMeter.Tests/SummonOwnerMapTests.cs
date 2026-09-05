using Stellar.Abstractions.Domain;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>Summon → summoner map behind `srcOwner` (spec § 6.8): a Battle Imagine casts under its OWN
/// entity id (Tina 3000033 owned by uid 20854 fired Lower CD 2110034), so credit must resolve owner(src).
/// Bounded like SeenSummonSet; unknown ids resolve to themselves; run-scoped clear.</summary>
public sealed class SummonOwnerMapTests
{
    static readonly EntityId Mate  = new(0x0000_0002_0000_0280);
    static readonly EntityId Tina  = new(0x0000_0009_0000_0040);
    static readonly EntityId Mob   = new(0x0000_0008_0000_0040);

    [Fact]
    public void Known_summon_resolves_to_its_owner()
    {
        var m = new SummonOwnerMap();
        m.Record(Tina, Mate);
        Assert.Equal(Mate, m.OwnerOf(Tina));
    }

    [Fact]
    public void Unknown_id_resolves_to_itself()
    {
        Assert.Equal(Mob, new SummonOwnerMap().OwnerOf(Mob));
        Assert.Equal(Mate, new SummonOwnerMap().OwnerOf(Mate));
    }

    [Fact]
    public void Re_recording_a_summon_updates_the_owner_without_growing()
    {
        var m = new SummonOwnerMap();
        m.Record(Tina, Mate); m.Record(Tina, new EntityId(0x0000_0003_0000_0280));
        Assert.Equal(1, m.Count);
        Assert.Equal(new EntityId(0x0000_0003_0000_0280), m.OwnerOf(Tina));
    }

    [Fact]
    public void Bounded_evicts_the_oldest()
    {
        var m = new SummonOwnerMap();
        for (var i = 1; i <= SummonOwnerMap.MaxEntries + 1; i++) m.Record(new EntityId(((long)i << 32) | 0x40), Mate);
        Assert.Equal(SummonOwnerMap.MaxEntries, m.Count);
        var oldest = new EntityId((1L << 32) | 0x40);
        Assert.Equal(oldest, m.OwnerOf(oldest));   // evicted → resolves to itself
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var m = new SummonOwnerMap();
        m.Record(Tina, Mate); m.Clear();
        Assert.Equal(0, m.Count);
        Assert.Equal(Tina, m.OwnerOf(Tina));
    }
}
