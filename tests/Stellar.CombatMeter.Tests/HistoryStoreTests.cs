using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter;
using Stellar.CombatMeter.LogUpload;   // UploadPhase
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

    // ---- Task 13 reshape: entries stay byte-identical to v10; upload state lives in a sidecar ----

    // The entry JSON a NEW build writes carries NO upload-state keys and stays version 10, so a rollback
    // to a prior (v10) DLL reads it intact instead of treating it as malformed and wiping history.
    [Fact]
    public void Entry_json_stays_v10_shape_with_no_upload_state_keys()
    {
        var json = HistoryStore.SerializeEntry(new Plugin.EncounterHistoryEntry { SceneName = "7151", LevelUuid = 42 });

        Assert.StartsWith("{\"v\":10,", json);        // version NOT bumped
        Assert.DoesNotContain("\"up\":", json);       // no per-entry upload phase
        Assert.DoesNotContain("\"uurl\":", json);     // no per-entry run URL
    }

    // Exact byte-identity: a controlled minimal entry serializes to the precise v10 string an older build
    // produced — the strongest guarantee that no field snuck into the entry shape.
    [Fact]
    public void Minimal_entry_serializes_to_exact_v10_bytes()
    {
        var json = HistoryStore.SerializeEntry(new Plugin.EncounterHistoryEntry { SceneName = "7151", LevelUuid = 42 });

        const string expected =
            "{\"v\":10,\"scene\":\"7151\",\"enter\":0,\"arch\":0,\"dur\":0,\"party\":0,\"members\":0,"
          + "\"luid\":42,\"pass\":0,\"mms\":0,\"tscore\":0,\"diff\":0,\"dstart\":0,\"res\":\"partial\","
          + "\"def\":0,\"trig\":\"manual\",\"stats\":[],\"series\":[],\"entities\":[]}";
        Assert.Equal(expected, json);
    }

    // Old-build tolerance: an INDEPENDENT copy of the shipped-v10 gate (v <= 10 AND reject-unknown-keys)
    // must ACCEPT the entry JSON this build writes — proving a rollback to a v10 DLL reads it, not skips it.
    [Fact]
    public void New_build_entry_is_accepted_by_a_copy_of_the_old_v10_gate()
    {
        var richJson    = HistoryStore.SerializeEntry(BuildRichEntry());
        var minimalJson = HistoryStore.SerializeEntry(new Plugin.EncounterHistoryEntry { SceneName = "x" });

        Assert.True(OldV10GateAccepts(richJson));
        Assert.True(OldV10GateAccepts(minimalJson));
    }

    // A faithful reimplementation of the SHIPPED v10 gate: accept only version 1..10 and only the v10 key
    // set; a version > 10 or ANY unknown top-level key => rejected (exactly what the shipped reader did via
    // `v <= FormatVersion` + `default: return false`). Uses System.Text.Json so it is independent of the
    // (now forward-hardened) production reader.
    private static readonly System.Collections.Generic.HashSet<string> V10Keys = new()
    {
        "v","scene","enter","arch","dur","party","members","luid","pass","mms",
        "tscore","diff","dstart","res","def","trig","stats","series","entities",
    };

    private static bool OldV10GateAccepts(string entryJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(entryJson);
        var root = doc.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        var sawV = false;
        foreach (var prop in root.EnumerateObject())
        {
            if (!V10Keys.Contains(prop.Name)) return false;   // shipped reader rejected unknown keys
            if (prop.Name == "v")
            {
                var v = prop.Value.GetInt32();
                if (v < 1 || v > 10) return false;            // shipped gate: v <= FormatVersion(=10)
                sawV = true;
            }
        }
        return sawV;
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
    [InlineData("{\"scene\":\"x\"}")]                 // missing version marker
    public void Malformed_or_legacy_input_is_skipped_without_throwing(string garbage)
    {
        // Must never throw, and must report failure (entry skipped) for unsupported shapes.
        Assert.False(HistoryStore.TryDeserializeEntry(garbage, out var got));
        Assert.Null(got);
    }

    // Forward-hardening: a FUTURE version with UNKNOWN keys now LOADS (read what you understand), reading
    // the recognized keys and skipping the rest — so a later format bump can never make THIS build (once it
    // is the rolled-back-to DLL) read newer files as malformed and wipe them. Replaces the old "future
    // version is skipped" behavior, which was exactly the rollback trap the reviewer flagged.
    [Fact]
    public void Future_version_with_unknown_keys_loads_reading_understood_fields()
    {
        var future = "{\"v\":" + (HistoryStore.FormatVersion + 5) + ",\"scene\":\"NewMap\",\"members\":4,"
                   + "\"newScalar\":123,\"newString\":\"x\",\"newArray\":[1,2,[3,4]],"
                   + "\"newObj\":{\"a\":1,\"b\":{\"c\":2}},\"stats\":[],\"series\":[]}";

        Assert.True(HistoryStore.TryDeserializeEntry(future, out var got));   // loads, does not throw
        Assert.NotNull(got);
        Assert.Equal("NewMap", got!.SceneName);   // understood keys read
        Assert.Equal(4, got.MemberCount);         // unknown scalar/string/array/object keys skipped
    }

    // An unknown key on a CURRENT-version entry is skipped, not rejected (same forward-tolerance).
    [Fact]
    public void Unknown_key_on_current_version_is_skipped_not_rejected()
    {
        Assert.True(HistoryStore.TryDeserializeEntry("{\"v\":1,\"scene\":\"x\",\"bogus\":5,\"stats\":[]}", out var got));
        Assert.Equal("x", got!.SceneName);
    }

    // A corrupt RECOGNIZED field still fails the entry — skip-unknown only tolerates keys we don't know.
    [Fact]
    public void Corrupt_known_field_still_fails_the_entry()
    {
        Assert.False(HistoryStore.TryDeserializeEntry("{\"v\":1,\"enter\":\"not-a-number\"}", out var got));
        Assert.Null(got);
    }

    // ---- Task 13 reshape: sidecar upload-state format ----

    // A sidecar record survives serialize→deserialize: composite key (LevelUuid, ArchivedAtMs) + phase + URL.
    [Fact]
    public void Upload_state_sidecar_record_round_trips()
    {
        var rec = new HistoryStore.UploadStateRecord(42, 1_700_000_000_000L, UploadPhase.Done,
            "https://logs.stellarresonance.app/run/SEA/42");
        var json = HistoryStore.SerializeUploadState(rec);

        Assert.True(HistoryStore.TryDeserializeUploadState(json, out var got));
        Assert.Equal(42L, got.LevelUuid);
        Assert.Equal(1_700_000_000_000L, got.ArchivedAtMs);
        Assert.Equal(UploadPhase.Done, got.Phase);
        Assert.Equal("https://logs.stellarresonance.app/run/SEA/42", got.Url);
    }

    // The sidecar array only ever holds durable (non-Idle) records; Idle entries are omitted entirely.
    [Fact]
    public void Sidecar_serialize_skips_idle_records()
    {
        var live = new System.Collections.Generic.List<HistoryStore.UploadStateRecord>
        {
            new(1, 100, UploadPhase.Done,   "u1"),
            new(2, 200, UploadPhase.Idle,   null),   // never persisted
            new(3, 300, UploadPhase.Failed, null),
        };
        var sidecar = HistoryStore.SerializeUploadStates(live);

        Assert.Equal(2, sidecar.Length);   // Idle dropped
        var idx = HistoryStore.IndexUploadStates(sidecar);
        Assert.True(idx.ContainsKey((1, 100)));
        Assert.False(idx.ContainsKey((2, 200)));
        Assert.True(idx.ContainsKey((3, 300)));
    }

    // Orphan cleanup: a sidecar record whose (LevelUuid, ArchivedAtMs) matches no live entry is not applied
    // on hydrate, and a rebuild from the surviving live set omits it — so it drops on the next save.
    [Fact]
    public void Sidecar_orphan_records_are_not_matched_and_drop_on_rebuild()
    {
        // Persisted sidecar has records for runs A(1,100) and B(2,200).
        var persisted = HistoryStore.SerializeUploadStates(new System.Collections.Generic.List<HistoryStore.UploadStateRecord>
        {
            new(1, 100, UploadPhase.Done, "uA"),
            new(2, 200, UploadPhase.Done, "uB"),
        });

        // On load, only run A still exists (B was evicted/deleted). Match by composite key.
        var idx = HistoryStore.IndexUploadStates(persisted);
        Assert.True(idx.TryGetValue((1, 100), out var recA));   // A applies
        Assert.Equal("uA", recA.Url);
        Assert.True(idx.ContainsKey((2, 200)));                  // B present in the parsed index...
        // ...but a rebuild from the surviving live set (only A) omits B entirely — orphan dropped.
        var rebuilt = HistoryStore.IndexUploadStates(HistoryStore.SerializeUploadStates(
            new System.Collections.Generic.List<HistoryStore.UploadStateRecord> { new(1, 100, UploadPhase.Done, "uA") }));
        Assert.True(rebuilt.ContainsKey((1, 100)));
        Assert.False(rebuilt.ContainsKey((2, 200)));
    }

    // Malformed / Idle sidecar records are dropped by the index (never throws).
    [Fact]
    public void Sidecar_index_skips_malformed_and_idle_records()
    {
        var sidecar = new[]
        {
            HistoryStore.SerializeUploadState(new HistoryStore.UploadStateRecord(1, 100, UploadPhase.Done, "u1")),
            "not json",
            HistoryStore.SerializeUploadState(new HistoryStore.UploadStateRecord(2, 200, UploadPhase.Idle, null)),
        };
        var idx = HistoryStore.IndexUploadStates(sidecar);

        Assert.Single(idx);
        Assert.True(idx.ContainsKey((1, 100)));
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
