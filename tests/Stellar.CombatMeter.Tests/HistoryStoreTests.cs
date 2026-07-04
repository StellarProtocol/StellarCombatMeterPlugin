using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Round-trip + robustness tests for the reflection-free history serializer (<see cref="HistoryStore"/>). These
/// exercise serialize→deserialize deep equality across all scalars, dict keys/values and the three series
/// channels, plus the never-throw-on-garbage contract that protects a user's saved history from a single corrupt
/// entry.
/// </summary>
public sealed class HistoryStoreTests
{
    private static Plugin.EncounterHistoryEntry BuildRichEntry()
    {
        var e = new Plugin.EncounterHistoryEntry
        {
            SceneName        = "Stormwatch \"Keep\" \\ Wing\nB",   // exercises quote/backslash/newline escaping
            EnteredAtMs      = 1_700_000_000_000L,
            ArchivedAtMs     = 1_700_000_123_456L,
            CombatDurationMs = 123_456L,
            PartyType        = PartyType.Raid20,
            MemberCount      = 7,
            DifficultyLevel  = 6,   // dungeon challenge level (raw DungeonSceneInfo.difficulty)
            DungeonStartMs   = 1_699_999_990_000L,   // dungeon run-timer start (epoch ms)
        };

        var a = new EntityId(0x0000_0001_0000_0280L);   // player
        var b = new EntityId(0x0000_00AB_0000_0040L);   // monster

        var sa = new SourceStats
        {
            TotalDamage = 999_999, TotalHealing = 4242, TotalTaken = 7777,
            TopHit = 54321, Hits = 480, Crits = 91, Luckys = 64, Kills = 3,
            FirstHitMs = 1000, LastHitMs = 120000,
        };
        sa.BySkill[101] = new SkillStats { Total = 500000, HealTotal = 0, Hits = 200, Crits = 40, Luckys = 25, CritLuckys = 9, TopHit = 30000, MinHit = 120, Kills = 2 };
        sa.BySkill[102] = new SkillStats { Total = 0, HealTotal = 4242, HealHits = 12, HealCrits = 1, HealLuckys = 3, HealTop = 800 };
        sa.IncomingBySkill[900] = new IncomingSkillStats { Total = 7777, Hits = 30, TopHit = 1200 };
        e.Stats[a] = sa;

        e.Stats[b] = new SourceStats { TotalDamage = 12345, TopHit = 2000, Hits = 50, FirstHitMs = 500, LastHitMs = 60000 };

        e.Series[a] = new SourceSeries
        {
            BucketMs = 1000,
            Dealt   = new long[] { 100, 200, 0, 0, 50 },
            Healing = new long[] { 0, 30 },
            Taken   = new long[] { 0, 0, 70 },
        };
        e.Series[b] = new SourceSeries
        {
            BucketMs = 1000,
            Dealt   = new long[] { 5, 5, 5 },
            Healing = System.Array.Empty<long>(),
            Taken   = new long[] { 1 },
        };

        // v2 per-player entity snapshot (issue #5). Only the player id (a) carries one — the monster (b) does not,
        // mirroring SnapshotEntities() which skips non-player sources.
        e.Entities[a] = new EntitySnapshot
        {
            Name       = "Momoko \"の\"",   // exercises quote + non-ASCII escaping in the name
            FightPoint = 248_000,
            Hp         = 150_000,
            MaxHp      = 181_411,
            TeamId     = 42,
            AttrIds    = new[] { 11330, 11710, 220 },
            AttrValues = new long[] { 48_500, 4196, 3 },
            GearSlots  = new[] { 200, 201, 202 },
            GearItemIds = new[] { 90001, 90002, 90003 },
            SkillIds   = new[] { 101, 102 },
            SkillLevels = new[] { 6, 4 },
            SkillTiers = new[] { 2, 1 },
            FashionSlots = new[] { 1, 2 },
            FashionIds = new[] { 7001, 7002 },
            FashionDyeCounts = new[] { 2, 0 },
            FashionDyes = new[] { 0.5f, 0.25f, 0.125f, 1f, 0.9f, 0.8f, 0.7f, 1f },   // 2 colours for entry 0
            // v4 self-only gear detail: two pieces, first with 2 rolls, second with 1.
            GdSlots      = new[] { 200, 201 },
            GdQuality    = new[] { 4, 5 },
            GdRefine     = new[] { 15, 12 },
            GdItemLv     = new[] { 170, 160 },
            GdPerfVal    = new[] { 100, 87 },
            GdPerfMax    = new[] { 100, 100 },
            GdEnchantId  = new[] { 55001, 0 },
            GdEnchantLv  = new[] { 3, 0 },
            GdRollCounts = new[] { 2, 1 },
            GdRolls      = new[] { 1, 9001, 95, 1,  1, 9002, 40, 0,  0, 9100, 100, 0 },
        };
        return e;
    }

    private static void AssertSnapshotEqual(EntitySnapshot want, EntitySnapshot got)
    {
        Assert.Equal(want.Name, got.Name);
        Assert.Equal(want.FightPoint, got.FightPoint);
        Assert.Equal(want.Hp, got.Hp);
        Assert.Equal(want.MaxHp, got.MaxHp);
        Assert.Equal(want.TeamId, got.TeamId);
        Assert.Equal(want.AttrIds, got.AttrIds);
        Assert.Equal(want.AttrValues, got.AttrValues);
        Assert.Equal(want.GearSlots, got.GearSlots);
        Assert.Equal(want.GearItemIds, got.GearItemIds);
        Assert.Equal(want.SkillIds, got.SkillIds);
        Assert.Equal(want.SkillLevels, got.SkillLevels);
        Assert.Equal(want.SkillTiers, got.SkillTiers);
        Assert.Equal(want.FashionSlots, got.FashionSlots);
        Assert.Equal(want.FashionIds, got.FashionIds);
        Assert.Equal(want.FashionDyeCounts, got.FashionDyeCounts);
        Assert.Equal(want.FashionDyes, got.FashionDyes);
        Assert.Equal(want.GdSlots, got.GdSlots);
        Assert.Equal(want.GdQuality, got.GdQuality);
        Assert.Equal(want.GdRefine, got.GdRefine);
        Assert.Equal(want.GdItemLv, got.GdItemLv);
        Assert.Equal(want.GdPerfVal, got.GdPerfVal);
        Assert.Equal(want.GdPerfMax, got.GdPerfMax);
        Assert.Equal(want.GdEnchantId, got.GdEnchantId);
        Assert.Equal(want.GdEnchantLv, got.GdEnchantLv);
        Assert.Equal(want.GdRollCounts, got.GdRollCounts);
        Assert.Equal(want.GdRolls, got.GdRolls);
    }

    [Fact]
    public void Round_trips_all_scalars_dicts_and_three_series_channels()
    {
        var src = BuildRichEntry();
        var json = HistoryStore.SerializeEntry(src);

        Assert.True(HistoryStore.TryDeserializeEntry(json, out var got));
        Assert.NotNull(got);

        Assert.Equal(src.SceneName, got!.SceneName);
        Assert.Equal(src.EnteredAtMs, got.EnteredAtMs);
        Assert.Equal(src.ArchivedAtMs, got.ArchivedAtMs);
        Assert.Equal(src.CombatDurationMs, got.CombatDurationMs);
        Assert.Equal(src.PartyType, got.PartyType);
        Assert.Equal(src.MemberCount, got.MemberCount);
        Assert.Equal(src.DifficultyLevel, got.DifficultyLevel);
        Assert.Equal(src.DungeonStartMs, got.DungeonStartMs);

        Assert.Equal(src.Stats.Count, got.Stats.Count);
        foreach (var (id, s) in src.Stats)
        {
            Assert.True(got.Stats.TryGetValue(id, out var d));
            Assert.Equal(s.TotalDamage, d!.TotalDamage);
            Assert.Equal(s.TotalHealing, d.TotalHealing);
            Assert.Equal(s.TotalTaken, d.TotalTaken);
            Assert.Equal(s.TopHit, d.TopHit);
            Assert.Equal(s.Hits, d.Hits);
            Assert.Equal(s.Crits, d.Crits);
            Assert.Equal(s.Luckys, d.Luckys);
            Assert.Equal(s.Kills, d.Kills);
            Assert.Equal(s.FirstHitMs, d.FirstHitMs);
            Assert.Equal(s.LastHitMs, d.LastHitMs);

            Assert.Equal(s.BySkill.Count, d.BySkill.Count);
            foreach (var (sid, sk) in s.BySkill)
            {
                Assert.True(d.BySkill.TryGetValue(sid, out var dk));
                Assert.Equal(sk.Total, dk!.Total);
                Assert.Equal(sk.HealTotal, dk.HealTotal);
                Assert.Equal(sk.Hits, dk.Hits);
                Assert.Equal(sk.Crits, dk.Crits);
                Assert.Equal(sk.Luckys, dk.Luckys);
                Assert.Equal(sk.CritLuckys, dk.CritLuckys);
                Assert.Equal(sk.TopHit, dk.TopHit);
                Assert.Equal(sk.MinHit, dk.MinHit);
                Assert.Equal(sk.Kills, dk.Kills);
                Assert.Equal(sk.HealHits, dk.HealHits);
                Assert.Equal(sk.HealCrits, dk.HealCrits);
                Assert.Equal(sk.HealLuckys, dk.HealLuckys);
                Assert.Equal(sk.HealTop, dk.HealTop);
            }
            Assert.Equal(s.IncomingBySkill.Count, d.IncomingBySkill.Count);
            foreach (var (sid, inc) in s.IncomingBySkill)
            {
                Assert.True(d.IncomingBySkill.TryGetValue(sid, out var di));
                Assert.Equal(inc.Total, di!.Total);
                Assert.Equal(inc.Hits, di.Hits);
                Assert.Equal(inc.TopHit, di.TopHit);
            }
        }

        Assert.Equal(src.Series.Count, got.Series.Count);
        foreach (var (id, sr) in src.Series)
        {
            Assert.True(got.Series.TryGetValue(id, out var dsr));
            Assert.Equal(sr.BucketMs, dsr.BucketMs);
            Assert.Equal(sr.Dealt, dsr.Dealt);
            Assert.Equal(sr.Healing, dsr.Healing);
            Assert.Equal(sr.Taken, dsr.Taken);
        }

        Assert.Equal(src.Entities.Count, got.Entities.Count);
        foreach (var (id, snap) in src.Entities)
        {
            Assert.True(got.Entities.TryGetValue(id, out var dsnap));
            AssertSnapshotEqual(snap, dsnap!);
        }
    }

    // v2 entity snapshot survives a full serialize→deserialize cycle, every scalar + parallel array intact
    // (the mandatory v2-round-trip reader test, spec §3.3).
    [Fact]
    public void V2_entity_snapshot_round_trips_all_parallel_arrays()
    {
        var src = BuildRichEntry();
        var json = HistoryStore.SerializeEntry(src);

        Assert.True(HistoryStore.TryDeserializeEntry(json, out var got));
        Assert.NotNull(got);
        Assert.Single(got!.Entities);   // only the player id carries a snapshot

        var wantSnap = System.Linq.Enumerable.First(src.Entities.Values);
        var gotSnap  = System.Linq.Enumerable.First(got.Entities.Values);
        AssertSnapshotEqual(wantSnap, gotSnap);
    }

    // A v1 entry (no "entities" key) STILL LOADS under the v2 reader — backward compatible (the version-bump
    // trap, spec §3.3). Entities loads empty; everything else intact.
    [Fact]
    public void V1_entry_without_entities_still_loads_with_empty_snapshot_map()
    {
        // A hand-rolled v1 string: version 1, no "entities" key (exactly what the v1 writer produced).
        const string v1 = "{\"v\":1,\"scene\":\"Old Keep\",\"enter\":100,\"arch\":200,\"dur\":50,"
                        + "\"party\":0,\"members\":3,"
                        + "\"stats\":[{\"id\":4294967936,\"td\":500,\"th\":0,\"tk\":0,\"top\":120,"
                        + "\"h\":10,\"c\":2,\"k\":1,\"fh\":100,\"lh\":150,\"sk\":[],\"in\":[]}],"
                        + "\"series\":[]}";

        Assert.True(HistoryStore.TryDeserializeEntry(v1, out var got));
        Assert.NotNull(got);
        Assert.Equal("Old Keep", got!.SceneName);
        Assert.Equal(3, got.MemberCount);
        Assert.Single(got.Stats);
        Assert.Empty(got.Entities);   // v1 carried no entities → empty map, no throw
    }

    // A v6 entry (no "dstart" key) STILL LOADS under the v7 reader — DungeonStartMs defaults to 0
    // (the version-bump trap: older entries lack the newer key, must load with the default).
    [Fact]
    public void V6_entry_without_dstart_loads_with_zero_dungeon_start()
    {
        const string v6 = "{\"v\":6,\"scene\":\"Old Keep\",\"enter\":100,\"arch\":200,\"dur\":50,"
                        + "\"party\":0,\"members\":3,\"luid\":42,\"pass\":0,\"mms\":0,\"diff\":6,\"res\":\"partial\","
                        + "\"stats\":[],\"series\":[],\"entities\":[]}";

        Assert.True(HistoryStore.TryDeserializeEntry(v6, out var got));
        Assert.NotNull(got);
        Assert.Equal(6, got!.DifficultyLevel);
        Assert.Equal(0L, got.DungeonStartMs);   // absent in v6 → defaults to 0 (unknown)
    }

    // A truncated / mismatched entities payload degrades — the reader clamps the parallel arrays to their
    // shortest member rather than throwing or mis-indexing (spec §3.3 truncated-degrades).
    [Fact]
    public void V2_entity_with_mismatched_parallel_arrays_clamps_to_shortest_without_throwing()
    {
        // 3 attr ids but only 2 values; 2 skill ids but 1 level + 0 tiers; 1 fashion entry but truncated dyes.
        const string json = "{\"v\":2,\"scene\":\"x\",\"enter\":0,\"arch\":0,\"dur\":0,\"party\":0,\"members\":1,"
                        + "\"stats\":[],\"series\":[],"
                        + "\"entities\":[{\"id\":640,\"nm\":\"Frag\",\"fp\":1,\"hp\":2,\"mhp\":3,\"tm\":4,"
                        + "\"ai\":[11330,11710,220],\"av\":[48500,4196],"   // 3 ids, 2 values
                        + "\"gs\":[200,201],\"gi\":[90001],"                // 2 slots, 1 item
                        + "\"si\":[101,102],\"sl\":[6],\"st\":[],"          // 2 ids, 1 level, 0 tiers
                        + "\"fs\":[1],\"fi\":[7001],\"fc\":[2],\"fd\":[0.5,0.25,0.125]}]}";   // dyes truncated (3, not a multiple of 4)

        Assert.True(HistoryStore.TryDeserializeEntry(json, out var got));   // must not throw, must load
        Assert.NotNull(got);
        var s = System.Linq.Enumerable.First(got!.Entities.Values);

        Assert.Equal(2, s.AttrIds.Length);     Assert.Equal(2, s.AttrValues.Length);   // clamped to 2
        Assert.Single(s.GearSlots);            Assert.Single(s.GearItemIds);           // clamped to 1
        Assert.Empty(s.SkillIds);              Assert.Empty(s.SkillLevels);            // clamped to 0 (tiers empty)
        Assert.Empty(s.SkillTiers);
        Assert.Single(s.FashionSlots);
        Assert.Empty(s.FashionDyes);           // 3 floats → not a multiple of 4 → trimmed to 0 (no partial colour)
    }

    [Fact]
    public void Empty_entry_round_trips()
    {
        var src = new Plugin.EncounterHistoryEntry { SceneName = null, MemberCount = 0 };
        var json = HistoryStore.SerializeEntry(src);

        Assert.True(HistoryStore.TryDeserializeEntry(json, out var got));
        Assert.NotNull(got);
        Assert.Equal("", got!.SceneName);   // null SceneName serializes as empty string
        Assert.Empty(got.Stats);
        Assert.Empty(got.Series);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("{\"v\":1,\"scene\":\"x")]            // truncated string
    [InlineData("{\"v\":1,\"stats\":[{\"id\":")]      // truncated number
    [InlineData("[]")]                                 // array, not the expected object
    [InlineData("{\"v\":1,\"bogus\":5}")]             // unknown key
    [InlineData("{\"scene\":\"x\"}")]                 // missing version marker
    [InlineData("{\"v\":8,\"scene\":\"x\"}")]         // unsupported FUTURE version (>FormatVersion)
    public void Malformed_or_legacy_input_is_skipped_without_throwing(string garbage)
    {
        // Must never throw, and must report failure (entry skipped) for unsupported shapes.
        Assert.False(HistoryStore.TryDeserializeEntry(garbage, out var got));
        Assert.Null(got);
    }

    [Fact]
    public void TrimToCapacity_evicts_oldest_first_and_caps_at_50()
    {
        var history = new List<Plugin.EncounterHistoryEntry>();
        for (var i = 0; i < 60; i++)
            history.Add(new Plugin.EncounterHistoryEntry { MemberCount = i });   // i = age marker (0 = oldest)

        var evicted = Plugin.TrimToCapacity(history);

        Assert.Equal(50, history.Count);
        // Oldest (0..9) evicted from the front; newest (10..59) retained in order.
        Assert.Equal(10, history[0].MemberCount);
        Assert.Equal(59, history[^1].MemberCount);
        // The 10 evicted entries are returned oldest-first so the caller can drop their upload status.
        Assert.Equal(10, evicted.Count);
        Assert.Equal(0, evicted[0].MemberCount);
        Assert.Equal(9, evicted[^1].MemberCount);
    }

    [Fact]
    public void TrimToCapacity_is_a_noop_under_the_cap()
    {
        var history = new List<Plugin.EncounterHistoryEntry>();
        for (var i = 0; i < 5; i++) history.Add(new Plugin.EncounterHistoryEntry());
        var evicted = Plugin.TrimToCapacity(history);
        Assert.Equal(5, history.Count);
        Assert.Empty(evicted);   // nothing evicted under the cap
    }

    [Fact]
    public void Serializer_round_trips_a_floods_of_entries_without_drift()
    {
        // Sanity: serialize a batch and re-read; each must come back identical.
        for (var n = 0; n < 5; n++)
        {
            var e = BuildRichEntry();
            e.MemberCount = n;
            var json = HistoryStore.SerializeEntry(e);
            Assert.True(HistoryStore.TryDeserializeEntry(json, out var got));
            Assert.Equal(n, got!.MemberCount);
        }
    }
}
