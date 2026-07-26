using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Pure state for the killed-boss mark (review round, 2026-07-26 — see KilledBossTracker's own doc for
// the loop this closes and why the cap evicts the OLDEST mark, not the newest). Extracted from a
// Plugin field to a standalone class so the load-bearing mark/consult/evict wiring is unit-testable
// headless — Plugin cannot be instantiated in tests (it needs the IL2CPP service surface). The one
// piece this suite does NOT cover — that BossStatus calls MarkKilled strictly BEFORE clearing
// _autoArchiveBossId — is a documented, accepted residual (see the comment on BossStatus in
// Plugin.AutoArchive.cs); everything the tracker itself decides is covered here.
public class KilledBossTrackerTests
{
    private static EntityId Id(long v) => new(v);

    [Fact]
    public void A_marked_boss_reads_killed()
    {
        var t = new KilledBossTracker();
        t.MarkKilled(Id(1));
        Assert.True(t.IsKilled(Id(1)));
    }

    [Fact]
    public void An_unmarked_boss_reads_not_killed()
    {
        var t = new KilledBossTracker();
        Assert.False(t.IsKilled(Id(1)));
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var t = new KilledBossTracker();
        t.MarkKilled(Id(1));
        t.MarkKilled(Id(2));

        t.Clear();

        Assert.False(t.IsKilled(Id(1)));
        Assert.False(t.IsKilled(Id(2)));
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void Remarking_the_same_boss_is_idempotent()
        // No growth, no re-queue — this matters for eviction order: a corpse still emitting events
        // after its death must not be treated as "freshly marked" and jump the eviction line.
    {
        var t = new KilledBossTracker();

        Assert.Null(t.MarkKilled(Id(1)));
        Assert.Null(t.MarkKilled(Id(1)));   // already marked

        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void At_capacity_a_new_mark_evicts_the_oldest_and_returns_it()
        // FIFO, oldest out — this is the whole point of the fix (review finding): the loop is only
        // reachable through a corpse still emitting combat events, which is always a RECENT kill, so
        // dropping the newest (the earlier, fails-open version) would leave that exact corpse
        // re-adoptable. Evicting the oldest instead keeps every recent mark intact.
    {
        var t = new KilledBossTracker();
        for (long i = 0; i < KilledBossTracker.MaxEntries; i++)
            Assert.Null(t.MarkKilled(Id(i)));
        Assert.Equal(KilledBossTracker.MaxEntries, t.Count);

        var evicted = t.MarkKilled(Id(KilledBossTracker.MaxEntries));   // one mark past capacity

        Assert.Equal(Id(0), evicted);   // id 0 was the OLDEST mark
    }

    [Fact]
    public void After_eviction_the_newest_mark_is_present_and_the_evicted_one_is_not()
    {
        var t = new KilledBossTracker();
        for (long i = 0; i < KilledBossTracker.MaxEntries; i++) t.MarkKilled(Id(i));
        var newest = Id(KilledBossTracker.MaxEntries);

        t.MarkKilled(newest);

        Assert.True(t.IsKilled(newest));
        Assert.False(t.IsKilled(Id(0)));   // the evicted, oldest mark is re-adoptable again
    }

    [Fact]
    public void Count_never_exceeds_MaxEntries()
    {
        var t = new KilledBossTracker();
        for (long i = 0; i < KilledBossTracker.MaxEntries * 2; i++)
        {
            t.MarkKilled(Id(i));
            Assert.True(t.Count <= KilledBossTracker.MaxEntries);
        }
        Assert.Equal(KilledBossTracker.MaxEntries, t.Count);
    }
}
