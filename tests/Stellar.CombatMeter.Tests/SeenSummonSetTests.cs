using Stellar.Abstractions.Domain;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Pins the novelty set behind the appear-sourced imagine-cast channel (Plugin.Capture.cs,
// TryRecordImagineCastFromAppear). Mirrors KilledBossTrackerTests — same bounded-id-set precedent,
// same headless rationale (Plugin cannot be instantiated in tests; the load-bearing state lives in
// its own pure class). The one semantic that differs from the tracker: MarkSeen RETURNS the novelty
// verdict (true = first sighting = cast candidate; false = re-appear = never a cast), because that
// verdict is exactly what DecideAppearCast consumes.
public class SeenSummonSetTests
{
    private static EntityId Id(long v) => new(v);

    [Fact]
    public void First_sighting_is_novel()
    {
        var s = new SeenSummonSet();
        Assert.True(s.MarkSeen(Id(1)));
    }

    [Fact]
    public void The_same_summon_reappearing_is_never_novel()
    {
        // AOI blink / owner running back into view: same entity uuid, must never mint a second cast.
        var s = new SeenSummonSet();
        Assert.True(s.MarkSeen(Id(1)));
        Assert.False(s.MarkSeen(Id(1)));
        Assert.False(s.MarkSeen(Id(1)));
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void A_fresh_summon_entity_is_novel_even_from_the_same_owner()
    {
        // A re-CAST spawns a NEW entity with a new uuid — that is the one shape that must pass.
        var s = new SeenSummonSet();
        Assert.True(s.MarkSeen(Id(1)));
        Assert.True(s.MarkSeen(Id(2)));
    }

    [Fact]
    public void At_capacity_the_oldest_mark_is_evicted_and_becomes_novel_again()
    {
        // FIFO, oldest out — keeps the NEWEST marks intact (the duplicate-appear hazard is always a
        // RECENT summon blinking in and out of AOI; see SeenSummonSet's doc for the fail direction).
        var s = new SeenSummonSet();
        for (long i = 0; i < SeenSummonSet.MaxEntries; i++) Assert.True(s.MarkSeen(Id(i)));

        Assert.True(s.MarkSeen(Id(SeenSummonSet.MaxEntries)));   // one past capacity → evicts Id(0)

        Assert.False(s.MarkSeen(Id(SeenSummonSet.MaxEntries)));  // newest mark held
        Assert.True(s.MarkSeen(Id(0)));                          // evicted oldest is novel again
    }

    [Fact]
    public void Count_never_exceeds_MaxEntries()
    {
        var s = new SeenSummonSet();
        for (long i = 0; i < SeenSummonSet.MaxEntries * 2; i++)
        {
            s.MarkSeen(Id(i));
            Assert.True(s.Count <= SeenSummonSet.MaxEntries);
        }
        Assert.Equal(SeenSummonSet.MaxEntries, s.Count);
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        // Run-boundary reset (ResetRunScopedTrackers): the next run's summons are all fresh spawns.
        var s = new SeenSummonSet();
        s.MarkSeen(Id(1));
        s.MarkSeen(Id(2));

        s.Clear();

        Assert.Equal(0, s.Count);
        Assert.True(s.MarkSeen(Id(1)));
    }
}
