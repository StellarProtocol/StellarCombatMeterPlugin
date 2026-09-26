using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>Spec upload design 2026-09-26 items 4 + 5: framework ≥ 2.11.0 seeds an entity's COMPLETE buff set on
/// appear (<see cref="CombatEvent.EntityBuffsSeeded"/>). The live set tracks which (target, uuid) pairs are
/// seed-only so the spool can restore the 2.14.2 upload shape (first Refreshed → applied, seed-only Removed →
/// disk only), replaces a target's entries on a seed (snapshot semantics), and stays bounded with O(1)
/// insertion-ordered eviction. The existing <see cref="LiveBuffSetTests"/> pin the unchanged live-row
/// semantics.</summary>
public sealed class LiveBuffSetSeedTests
{
    static readonly EntityId Mate = new(0x0000_0002_0000_0280), Self = new(0x0000_0001_0000_0280);
    static LiveBuff Row(long ms, string kind, int uuid, int durMs, string tgt = "1") =>
        new(new BuffEvent(ms, tgt, uuid, 55333, kind, 1, 1, durMs, "2", 0, 2327), Mate, tgt == "1" ? Self : Mate);
    static ActiveBuff Seeded(int uuid) => new(uuid, 55333, 1, Mate, 1, 1, 0, 10_000);

    [Fact]
    public void A_seeded_uuid_is_taken_exactly_once()
    {
        var set = new LiveBuffSet();
        set.Seed("1", new[] { Seeded(7) });
        Assert.True(set.TakeSeeded("1", 7));
        Assert.False(set.TakeSeeded("1", 7));                 // consumed by its first live delta
        Assert.False(set.TakeSeeded("1", 8));                 // never seeded
        Assert.False(set.TakeSeeded("2", 7));                 // per target
    }

    // A seed-only buff is NOT restated by the keyframe: 2.14.2 never knew it until its first live delta, and the
    // uploaded buff rows must not change (owner ruling 2026-09-26). It is not a live row either (no cap pressure).
    [Fact]
    public void A_seed_only_buff_is_not_keyframed_and_holds_no_live_row()
    {
        var set = new LiveBuffSet();
        set.Seed("1", new[] { Seeded(7), Seeded(8) });
        Assert.Equal(0, set.Count);
        Assert.Empty(set.Keyframe(5_000));
    }

    // Snapshot semantics (per target): a seed REPLACES the target — live rows it does not list are gone, listed live
    // rows stay (the keyframe keeps restating them exactly like 2.14.2 did), and every listed uuid is seed-pending.
    // Other targets are untouched.
    [Fact]
    public void A_seed_replaces_only_that_targets_entries()
    {
        var set = new LiveBuffSet();
        set.Apply(Row(1000, "applied", 1, 0, "1"));
        set.Apply(Row(1000, "applied", 2, 0, "1"));
        set.Apply(Row(1000, "applied", 1, 0, "2"));
        set.Seed("1", new[] { Seeded(2), Seeded(3) });

        var kf = set.Keyframe(5_000);
        Assert.DoesNotContain(kf, l => l.Row.Tgt == "1" && l.Row.Uuid == 1);   // not listed → gone
        Assert.Contains(kf, l => l.Row.Tgt == "1" && l.Row.Uuid == 2);         // listed live row → still restated
        Assert.Contains(kf, l => l.Row.Tgt == "2" && l.Row.Uuid == 1);         // other target untouched
        Assert.True(set.TakeSeeded("1", 2));
        Assert.True(set.TakeSeeded("1", 3));
        Assert.False(set.TakeSeeded("1", 1));
    }

    [Fact]
    public void A_re_seed_replaces_the_pending_marks_of_that_target()
    {
        var set = new LiveBuffSet();
        set.Seed("1", new[] { Seeded(7) });
        set.Seed("1", new[] { Seeded(9) });
        Assert.False(set.TakeSeeded("1", 7));
        Assert.True(set.TakeSeeded("1", 9));
    }

    // Departure cleanup: an EMPTY seed (the framework's "this entity now holds nothing") drops everything held for
    // the target — its live rows and its pending marks.
    [Fact]
    public void DropTarget_and_an_empty_seed_remove_every_entry_of_the_target()
    {
        var set = new LiveBuffSet();
        set.Apply(Row(1000, "applied", 1, 0, "1"));
        set.Apply(Row(1000, "applied", 1, 0, "2"));
        set.Seed("1", new[] { Seeded(5) });
        set.Seed("1", System.Array.Empty<ActiveBuff>());
        Assert.False(set.TakeSeeded("1", 5));
        Assert.Equal(1, set.Count);

        set.Seed("2", new[] { Seeded(6) });                   // target 2's live uuid 1 is not listed → gone
        set.Apply(Row(1000, "applied", 4, 0, "2"));
        set.DropTarget("2");
        Assert.Equal(0, set.Count);
        Assert.False(set.TakeSeeded("2", 6));
        Assert.Empty(set.Keyframe(5_000));
    }

    // Scene change clears LIVE rows only: the framework's EnterScene seeds (self, early appears) can be drained
    // BEFORE the plugin's SceneChanged handler runs, and their pending marks must survive it or a seeded buff's
    // expiry would be uploaded + counted as a game event.
    [Fact]
    public void Clear_drops_live_rows_but_keeps_pending_seed_marks()
    {
        var set = new LiveBuffSet();
        set.Apply(Row(1000, "applied", 1, 0, "1"));
        set.Seed("1", new[] { Seeded(1), Seeded(2) });
        set.Clear();
        Assert.Equal(0, set.Count);
        Assert.True(set.TakeSeeded("1", 2));
    }

    // O(1) eviction backstop: insertion-ordered, and an UPDATE (refreshed/applied for a present key) moves the key to
    // the young end — so a buff that keeps refreshing is never the one evicted while a stale one lingers.
    [Fact]
    public void Eviction_is_insertion_ordered_and_a_refresh_makes_the_key_young_again()
    {
        var set = new LiveBuffSet();
        set.Apply(Row(1, "applied", 1, 0, "3"));
        set.Apply(Row(2, "applied", 2, 0, "3"));
        for (var i = 0; i < LiveBuffSet.MaxEntries - 2; i++) set.Apply(Row(10 + i, "applied", 100 + i, 0, "3"));
        set.Apply(Row(99_999, "refreshed", 1, 0, "3"));        // uuid 1 is young again
        set.Apply(Row(100_000, "applied", 50_000, 0, "3"));    // full + new key → evicts the oldest: uuid 2
        Assert.Equal(LiveBuffSet.MaxEntries, set.Count);
        var kf = set.Keyframe(200_000);
        Assert.Contains(kf, l => l.Row.Uuid == 1);
        Assert.DoesNotContain(kf, l => l.Row.Uuid == 2);
        Assert.Contains(kf, l => l.Row.Uuid == 50_000);
    }

    // The per-target index stays consistent through eviction, expiry and removal (a stale index entry would make
    // a later seed "replace" a key that is already gone, or keep a removed one alive).
    [Fact]
    public void The_per_target_index_follows_eviction_expiry_and_removal()
    {
        var set = new LiveBuffSet();
        set.Apply(Row(1000, "applied", 1, 1_000, "1"));        // expires at 2000
        set.Apply(Row(1000, "applied", 2, 0, "1"));
        set.Apply(Row(1500, "removed", 2, 0, "1"));
        Assert.Empty(set.Keyframe(3_000));
        set.Seed("1", new[] { Seeded(1) });                    // nothing live left to keep or drop
        Assert.Equal(0, set.Count);
        set.Apply(Row(4000, "applied", 3, 0, "1"));
        Assert.Single(set.Keyframe(5_000));
    }

    // Perf fix round 6: re-seeding a held target reuses its mark set AND makes it the youngest seed — so a player
    // re-seeded recently is not the one dropped when the cap is hit.
    [Fact]
    public void A_re_seed_makes_the_target_the_youngest_seed()
    {
        var set = new LiveBuffSet();
        for (var i = 0; i < LiveBuffSet.MaxSeedTargets; i++) set.Seed("t" + i, new[] { Seeded(1) });
        set.Seed("t0", new[] { Seeded(2) });                   // t0 re-seeded → youngest
        set.Seed("new", new[] { Seeded(3) });                  // full → evicts the oldest: t1, not t0
        Assert.True(set.TakeSeeded("t0", 2));
        Assert.False(set.TakeSeeded("t0", 1));                 // the re-seed replaced its marks
        Assert.False(set.TakeSeeded("t1", 1));
        Assert.True(set.TakeSeeded("new", 3));
    }

    // Perf fix round 7: the cap is 256 targets (worst case well under 1 MB of marks).
    [Fact]
    public void The_seed_target_cap_is_256()
        => Assert.Equal(256, LiveBuffSet.MaxSeedTargets);

    // Pending marks are bounded too (a town crowd seeds every player in view): past MaxSeedTargets the oldest SEEDED
    // target's marks are dropped.
    [Fact]
    public void Pending_seed_marks_are_bounded_by_target()
    {
        var set = new LiveBuffSet();
        for (var i = 0; i < LiveBuffSet.MaxSeedTargets + 3; i++) set.Seed("t" + i, new[] { Seeded(1) });
        Assert.Equal(LiveBuffSet.MaxSeedTargets, set.SeedTargetCount);
        Assert.False(set.TakeSeeded("t0", 1));
        Assert.True(set.TakeSeeded("t" + (LiveBuffSet.MaxSeedTargets + 2), 1));
    }
}
