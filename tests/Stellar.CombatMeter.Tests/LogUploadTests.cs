// UNVERIFIED — tests for the SP1 log-upload components (CombatEventBuffer, EventsJsonWriter,
// CombatLogWriter, CanonicalPayload). No IL2CPP or IPluginServices mock needed for these pure-data paths.

using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class LogUploadTests
{
    // Minimal in-memory IConfigSection used to pin pref defaults without constructing a full Plugin.
    // Mirrors the framework contract: a missing key returns the caller-supplied default.
    private sealed class FakeConfigSection : IConfigSection
    {
        private readonly Dictionary<string, object?> _store = new();
        public T? Get<T>(string key, T? defaultValue)
            => _store.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
        public void Set<T>(string key, T value) => _store[key] = value;
        public void Save() { }
        public void SaveQuiet() { }
    }

    [Fact]
    public void AutoUpload_defaults_on()
    {
        var prefs = new FakeConfigSection();
        Assert.True(prefs.Get("logUpload.autoUpload", true));   // default true when unset
    }

    // -------------------------------------------------------------------------
    // UploadStatusTable: per-entry upload-state machine (drives the history button).
    // Tested directly (services-free) rather than via a heavy Plugin ctor.
    // -------------------------------------------------------------------------

    [Fact]
    public void Upload_status_defaults_idle_for_unknown_entry()
    {
        var table = new UploadStatusTable();
        var entry = new Plugin.EncounterHistoryEntry { LevelUuid = 1 };
        Assert.Equal(UploadPhase.Idle, table.PhaseFor(entry));
        Assert.Null(table.UrlFor(entry));
    }

    [Fact]
    public void Upload_status_tracks_phase_and_url_per_entry()
    {
        var table = new UploadStatusTable();
        var a = new Plugin.EncounterHistoryEntry { LevelUuid = 1 };
        var b = new Plugin.EncounterHistoryEntry { LevelUuid = 2 };

        table.Set(a, UploadPhase.InFlight, "https://example/run/1");
        Assert.Equal(UploadPhase.InFlight, table.PhaseFor(a));
        Assert.Equal("https://example/run/1", table.UrlFor(a));
        Assert.Equal(UploadPhase.Idle, table.PhaseFor(b));   // distinct entry untouched

        table.Set(a, UploadPhase.Done, "https://example/run/1");
        Assert.Equal(UploadPhase.Done, table.PhaseFor(a));
        Assert.Equal("https://example/run/1", table.UrlFor(a));
    }

    [Fact]
    public void Upload_status_Forget_drops_one_entry_and_Clear_empties_all()
    {
        var table = new UploadStatusTable();
        var a = new Plugin.EncounterHistoryEntry { LevelUuid = 1 };
        var b = new Plugin.EncounterHistoryEntry { LevelUuid = 2 };
        table.Set(a, UploadPhase.Done, "https://example/run/1");
        table.Set(b, UploadPhase.Done, "https://example/run/2");

        table.Forget(a);
        Assert.Equal(UploadPhase.Idle, table.PhaseFor(a));   // forgotten → back to default
        Assert.Null(table.UrlFor(a));
        Assert.Equal(UploadPhase.Done, table.PhaseFor(b));   // sibling untouched

        table.Forget(a);                                     // forgetting an unknown entry is a no-op

        table.Clear();
        Assert.Equal(UploadPhase.Idle, table.PhaseFor(b));   // cleared wholesale
    }

    // -------------------------------------------------------------------------
    // CombatEventBuffer
    // -------------------------------------------------------------------------

    [Fact]
    public void Buffer_AccumulatesEvents()
    {
        var buf = new CombatEventBuffer();
        buf.Add(new CombatEvent.DamageDealt(1000L, new EntityId(1), new EntityId(2), 99,
            500, 480, 0, false, false, false, false,
            DamageElement.Fire, DamageSourceKind.Skill));
        buf.Add(new CombatEvent.SkillUsed(1001L, new EntityId(1), 99, SkillEventPhase.Begin));
        Assert.Equal(2, buf.Count);
    }

    [Fact]
    public void Buffer_FlushClearsAndReturnsEvents()
    {
        var buf = new CombatEventBuffer();
        buf.Add(new CombatEvent.SkillUsed(2000L, new EntityId(5), 42, SkillEventPhase.SkillEnd));
        var flushed = buf.Flush();
        Assert.Single(flushed);
        Assert.Equal(0, buf.Count);
    }

    [Fact]
    public void Buffer_ConvertsDamageEvent()
    {
        var buf = new CombatEventBuffer();
        buf.Add(new CombatEvent.DamageDealt(3000L, new EntityId(10), new EntityId(20), 7,
            1234, 1000, 50, true, false, false, false,
            DamageElement.Water, DamageSourceKind.Buff));
        var events = buf.Flush();

        var de = Assert.IsType<DamageEvent>(events[0]);
        Assert.Equal(3000L, de.Ms);
        Assert.Equal("10", de.Src);
        Assert.Equal("20", de.Tgt);
        Assert.Equal(7, de.Skill);
        Assert.Equal(1234L, de.Amt);
        Assert.Equal(1000L, de.Act);
        Assert.Equal(50L, de.Shield);
        Assert.True(de.Crit);
        Assert.False(de.Lucky);
        Assert.False(de.Heal);
        Assert.False(de.Dead);
        Assert.Equal((int)DamageElement.Water, de.Elem);
        Assert.Equal((int)DamageSourceKind.Buff, de.Kind);
    }

    [Fact]
    public void Buffer_ConvertsSkillEvent()
    {
        var buf = new CombatEventBuffer();
        buf.Add(new CombatEvent.SkillUsed(5000L, new EntityId(3), 88, SkillEventPhase.StageBegin));
        var events = buf.Flush();

        var se = Assert.IsType<SkillEvent>(events[0]);
        Assert.Equal(5000L, se.Ms);
        Assert.Equal("3", se.Src);
        Assert.Equal(88, se.Skill);
        Assert.Equal((int)SkillEventPhase.StageBegin, se.Phase);
    }

    [Fact]
    public void Buffer_ConvertsBuffEvent()
    {
        var buf = new CombatEventBuffer();
        buf.Add(new CombatEvent.BuffChanged(6000L, new EntityId(99), 12345, 500,
            BuffChangeKind.Applied, 2, 0, 30000));
        var events = buf.Flush();

        var be = Assert.IsType<BuffEvent>(events[0]);
        Assert.Equal(6000L, be.Ms);
        Assert.Equal("99", be.Tgt);
        Assert.Equal(12345, be.Uuid);
        Assert.Equal(500, be.Base);
        Assert.Equal("applied", be.Kind);
        Assert.Equal(2, be.Stacks);
        Assert.Equal(0, be.Layer);
        Assert.Equal(30000, be.DurMs);
    }

    [Fact]
    public void Buffer_BuffRemovedMapsKindCorrectly()
    {
        var buf = new CombatEventBuffer();
        buf.Add(new CombatEvent.BuffChanged(7000L, new EntityId(1), 1, 1,
            BuffChangeKind.Removed, 0, 0, 0));
        var be = Assert.IsType<BuffEvent>(buf.Flush()[0]);
        Assert.Equal("removed", be.Kind);
    }

    [Fact]
    public void Buffer_BuffRefreshedMapsKindCorrectly()
    {
        var buf = new CombatEventBuffer();
        buf.Add(new CombatEvent.BuffChanged(8000L, new EntityId(2), 2, 2,
            BuffChangeKind.Refreshed, 1, 0, 10000));
        var be = Assert.IsType<BuffEvent>(buf.Flush()[0]);
        Assert.Equal("refreshed", be.Kind);
    }

    [Fact]
    public void Buffer_DamageRingDropsOldestOnOverflow()
    {
        // dmg/skill ring caps at MaxDamageEvents; once full, oldest is overwritten (count stays at cap).
        var buf = new CombatEventBuffer();
        var totalToAdd = CombatEventBuffer.MaxDamageEvents + 2;
        for (var i = 0; i < totalToAdd; i++)
            buf.Add(new CombatEvent.SkillUsed(i, new EntityId((long)i), i, SkillEventPhase.Begin));
        Assert.Equal(CombatEventBuffer.MaxDamageEvents, buf.Count);
    }

    private static CombatEvent.BuffChanged MakeBuff(int i) =>
        new CombatEvent.BuffChanged(i, new EntityId(1), 1, 1, BuffChangeKind.Applied, 1, 0, 1000);

    private static CombatEvent.DamageDealt MakeDamage(int i) =>
        new CombatEvent.DamageDealt(i, new EntityId(1), new EntityId(2), 99,
            100, 100, 0, false, false, false, false, DamageElement.Fire, DamageSourceKind.Skill);

    [Fact]
    public void Buff_volume_does_not_evict_damage_and_does_not_flag_truncation()
    {
        var buf = new CombatEventBuffer();
        // Flood buffs past their cap, plus a modest number of damage events under the dmg cap.
        for (int i = 0; i < CombatEventBuffer.MaxBuffEvents + 50_000; i++) buf.Add(MakeBuff(i));
        for (int i = 0; i < 1000; i++) buf.Add(MakeDamage(i));
        var events = buf.Flush();
        Assert.Equal(1000, events.Count(e => e is DamageEvent));  // all damage retained despite buff flood
        Assert.False(buf.Truncated);                              // buff overflow is NOT flagged (nothing renders buffs)
    }

    [Fact]
    public void Damage_overflow_flags_truncation()
    {
        var buf = new CombatEventBuffer();
        for (int i = 0; i < CombatEventBuffer.MaxDamageEvents + 100; i++) buf.Add(MakeDamage(i));
        Assert.True(buf.Truncated);                               // dmg/skill forensic ring overflowed
    }

    [Fact]
    public void Flush_merges_rings_in_chronological_order()
    {
        var buf = new CombatEventBuffer();
        buf.Add(MakeDamage(5000));
        buf.Add(MakeBuff(1000));
        buf.Add(MakeDamage(3000));
        var events = buf.Flush();
        Assert.Equal(new long[] { 1000, 3000, 5000 }, events.Select(e => e.Ms).ToArray());
    }

    // -------------------------------------------------------------------------
    // DerivedBuilder (B3): aggregates derived from the meter's uncapped stats/series/deaths.
    // -------------------------------------------------------------------------

    [Fact]
    public void Derived_perActor_totals_match_stats()
    {
        var id = new EntityId(123L << 16);
        var entry = new Plugin.EncounterHistoryEntry
        {
            CombatDurationMs = 10_000,
            Stats = new()
            {
                [id] = new SourceStats
                {
                    TotalDamage = 5000, TotalHealing = 200, TotalTaken = 300,
                    Hits = 10, Crits = 4, Luckys = 1, Deaths = 1, TopHit = 900,
                    FirstHitMs = 1000, LastHitMs = 9000,
                    BySkill = new() { [1] = new SkillStats { Total = 5000, HealTotal = 200, Hits = 10, Crits = 4 } },
                    IncomingBySkill = new() { [2] = new IncomingSkillStats { Total = 300, Hits = 3 } },
                },
            },
            Series = new()
            {
                [id] = new SourceSeries { BucketMs = 1000, Dealt = new long[] { 2000, 3000 }, Healing = new long[] { 200, 0 }, Taken = new long[] { 100, 200 } },
            },
            DeathLog = new() { new DeathEntry(5000, id, 2) },
        };
        var d = DerivedBuilder.Build(entry, truncatedEvents: false);
        var key = (123L << 16).ToString();
        Assert.Equal(5000, d.PerActor[key].Damage);
        Assert.Equal(1, d.PerActor[key].Deaths);
        Assert.Equal(10_000, d.CombatDurationMs);
        Assert.Equal(1000, d.Series.BucketMs);
        Assert.Equal(new long[] { 2000, 3000 }, d.Series.PerActor[key].Dealt);
        Assert.Single(d.PerActorSkills[key]);          // skill 1 (damage)
        Assert.Single(d.PerActorHealSkills[key]);       // skill 1 had HealTotal>0
        Assert.Single(d.PerActorTakenSkills[key]);      // skill 2 incoming
        Assert.Single(d.Deaths);                        // one killing blow
        Assert.Equal(key, d.Deaths[0].Victim);
        Assert.Equal(2, d.Deaths[0].Skill);
    }

    // -------------------------------------------------------------------------
    // BuildEncounter: run-identity comes from the archived entry, NOT live IDungeonState.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildEncounter_uses_entry_identity_not_live_state()
    {
        var entry = new Plugin.EncounterHistoryEntry
        {
            SceneName = "7151", EnteredAtMs = 1000, ArchivedAtMs = 349535, CombatDurationMs = 169000,
            PartyType = PartyType.Raid20, LevelUuid = 146960651154096128L,
            PassTime = 169, MasterModeScore = 980, Result = "kill",
        };
        var enc = CombatLogAssembler.BuildEncounter(entry);
        Assert.Equal(146960651154096128L, enc.LevelUuid);
        Assert.Equal("raid", enc.Kind);          // Raid20 → raid
        Assert.Equal(7151, enc.MapId);            // parsed from SceneName
        Assert.Equal("kill", enc.Result);
        Assert.Equal(169, enc.PassTime);
        Assert.Equal(980, enc.MasterModeScore);
        Assert.Equal(1000, enc.StartMs);
        Assert.Equal(349535, enc.EndMs);
        Assert.Equal(348535, enc.DurationMs);     // EndMs - StartMs
    }

    // -------------------------------------------------------------------------
    // Manual-upload offline serialize smoke: a CombatLog built from an entry's
    // aggregates with EMPTY events serializes correctly — every rendered number
    // rides on `derived`, `encounter.levelUuid` is the entry's snapshotted id, and
    // events serialize as []. (Schema validation runs out-of-band via ajv on the
    // emitted JSON — see the dev report's offline-smoke result.)
    // -------------------------------------------------------------------------

    [Fact]
    public void ManualUpload_emptyEvents_serializes_derived_from_entry()
    {
        var id = new EntityId(123L << 16);
        var entry = new Plugin.EncounterHistoryEntry
        {
            SceneName = "7151", EnteredAtMs = 1000, ArchivedAtMs = 11_000, CombatDurationMs = 10_000,
            PartyType = PartyType.Raid20, LevelUuid = 146960651154096128L,
            PassTime = 169, MasterModeScore = 980, Result = "kill",
            Stats = new()
            {
                [id] = new SourceStats
                {
                    TotalDamage = 5000, TotalHealing = 200, TotalTaken = 300,
                    Hits = 10, Crits = 4, Luckys = 1, Deaths = 1, TopHit = 900,
                    FirstHitMs = 1000, LastHitMs = 9000,
                    BySkill = new() { [1] = new SkillStats { Total = 5000, HealTotal = 200, Hits = 10, Crits = 4 } },
                    IncomingBySkill = new() { [2] = new IncomingSkillStats { Total = 300, Hits = 3 } },
                },
            },
            Series = new()
            {
                [id] = new SourceSeries { BucketMs = 1000, Dealt = new long[] { 2000, 3000 }, Healing = new long[] { 200, 0 }, Taken = new long[] { 100, 200 } },
            },
            DeathLog = new() { new DeathEntry(5000, id, 2) },
        };

        var key = (123L << 16).ToString();
        var encounter = CombatLogAssembler.BuildEncounter(entry);
        var derived   = DerivedBuilder.Build(entry, truncatedEvents: true);

        // events == [] : the manual path uploads aggregates only.
        var events = (IReadOnlyList<CombatLogEvent>)Array.Empty<CombatLogEvent>();

        var actor = new Actor("Tester", "player", 1L, true, 123L, 1, 60, 0L, 100_000L,
            Array.Empty<long[]>(), Array.Empty<int[]>(), Array.Empty<int[]>(), Array.Empty<Fashion>());
        var header = new LogHeader("cm-smoke", 11_000L, "2.11", "SEA", "1.9.0", "1.1.0", "unlisted",
            encounter, new Uploader(123L, "sig", "nonce"));
        var log = new CombatLog(1, header, new Dictionary<string, Actor> { [key] = actor }, events, derived);

        var json = CombatLogWriter.Write(log);

        // events serialize as an empty array.
        Assert.Contains("\"events\":[]", json);
        // encounter.levelUuid == entry.LevelUuid (emitted as a string for int64 precision).
        Assert.Contains("\"levelUuid\":\"146960651154096128\"", json);
        // derived.perActor totals come straight off entry.Stats.
        Assert.Equal(5000, derived.PerActor[key].Damage);
        Assert.Equal(200,  derived.PerActor[key].Healing);
        Assert.Equal(300,  derived.PerActor[key].DamageTaken);
        Assert.Equal(1,    derived.PerActor[key].Deaths);
        Assert.Contains("\"derived\":", json);

        // Persist the artifact so the out-of-band ajv schema check can validate it.
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cm-manual-upload-smoke.json"), json);
    }

    // Worst-case 1-hour, 20-player fight: series coalesced to the 1800-bucket cap (2s buckets), 40 skills/player.
    // Proves the upload blob stays bounded (~1-2MB) and schema-valid regardless of fight length — the whole point
    // of aggregates-on-the-wire. The worker failed at ~10MB/70k-events; this must stay far below.
    [Fact]
    public void Stress_oneHour_twentyPlayer_blob_stays_bounded_and_valid()
    {
        const int players = 20, buckets = 1800, skillsPer = 40;
        var stats  = new Dictionary<EntityId, SourceStats>();
        var series = new Dictionary<EntityId, SourceSeries>();
        var actors = new Dictionary<string, Actor>();
        var deaths = new List<DeathEntry>();

        long[] Ramp() { var a = new long[buckets]; for (var i = 0; i < buckets; i++) a[i] = 100_000 + i; return a; }

        for (var p = 0; p < players; p++)
        {
            var id = new EntityId(((long)(1000 + p)) << 16);
            var key = id.Value.ToString();
            var bySkill = new Dictionary<int, SkillStats>();
            for (var s = 0; s < skillsPer; s++)
                bySkill[100_000 + s] = new SkillStats { Total = 1_000_000 + s, HealTotal = 0, Hits = 500, Crits = 200, Luckys = 10, TopHit = 50_000 };
            var inc = new Dictionary<int, IncomingSkillStats>();
            for (var s = 0; s < 20; s++)
                inc[900_000 + s] = new IncomingSkillStats { Total = 500_000, Hits = 100, TopHit = 20_000 };
            stats[id] = new SourceStats
            {
                TotalDamage = 700_000_000L + p, TotalHealing = p % 3 == 0 ? 300_000_000L : 0, TotalTaken = 40_000_000L,
                Hits = 20_000, Crits = 8_000, Luckys = 300, Deaths = 3, TopHit = 1_800_000,
                FirstHitMs = 1_000, LastHitMs = 3_600_000, BySkill = bySkill, IncomingBySkill = inc,
            };
            series[id] = new SourceSeries { BucketMs = 2_000, Dealt = Ramp(), Healing = Ramp(), Taken = Ramp() };
            actors[key] = new Actor("Player" + p, "player", 1L, p == 0, 1000L + p, (p % 13) + 1, 60, 180_000, 1_800_000,
                Array.Empty<long[]>(), Array.Empty<int[]>(), Array.Empty<int[]>(), Array.Empty<Fashion>());
            if (p < 15) deaths.Add(new DeathEntry(1_000 + p * 1_000, id, 900_000));
        }

        var entry = new Plugin.EncounterHistoryEntry
        {
            SceneName = "6333", EnteredAtMs = 1_000, ArchivedAtMs = 3_601_000, CombatDurationMs = 3_600_000,
            PartyType = PartyType.Raid20, LevelUuid = 663181291675451392L, PassTime = 3_600, MasterModeScore = 0, Result = "kill",
            Stats = stats, Series = series, DeathLog = deaths,
        };

        var derived = DerivedBuilder.Build(entry, truncatedEvents: true);
        var header = new LogHeader("cm-stress-1hr", 3_601_000L, "2.11", "SEA", "1.9.0", "1.1.0", "unlisted",
            CombatLogAssembler.BuildEncounter(entry), new Uploader(1000L, "sig", "nonce"));
        var log = new CombatLog(1, header, actors, (IReadOnlyList<CombatLogEvent>)Array.Empty<CombatLogEvent>(), derived);

        var json = CombatLogWriter.Write(log);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(json);
        var outPath = System.Environment.GetEnvironmentVariable("CM_STRESS_OUT")
                      ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cm-stress-1hr.json");
        System.IO.File.WriteAllText(outPath, json);

        Assert.Equal(players, derived.PerActor.Count);
        Assert.Equal(2_000, derived.Series.BucketMs);
        Assert.Equal(buckets, derived.Series.PerActor[(1000L << 16).ToString()].Dealt.Count);
        // Must stay far below the ~10MB point where the ingest worker 503'd.
        Assert.True(bytes < 4_000_000, $"1-hour blob was {bytes} bytes (expected < 4MB)");
    }

    // -------------------------------------------------------------------------
    // EventsJsonWriter / CombatLogWriter round-trip correctness
    // -------------------------------------------------------------------------

    [Fact]
    public void EventsJsonWriter_ProducesSameOutputAsCombatLogWriter()
    {
        var events = new List<CombatLogEvent>
        {
            new SkillEvent(100L, "1", 10, 101),
            new DamageEvent(200L, "1", "2", 10, 500, 490, 0, true, false, false, false, 1, 0, 0),
            new BuffEvent(300L, "2", 999, 5, "applied", 1, 0, 5000),
        };

        var eventsJson = EventsJsonWriter.Write(events);

        // The output should start and end with array delimiters.
        Assert.StartsWith("[", eventsJson);
        Assert.EndsWith("]", eventsJson);
        // Spot-check a few field names to confirm correct serialization.
        Assert.Contains("\"t\":\"skill\"", eventsJson);
        Assert.Contains("\"t\":\"dmg\"", eventsJson);
        Assert.Contains("\"t\":\"buff\"", eventsJson);
    }

    [Fact]
    public void CombatLogWriter_ProducesValidJson()
    {
        var actors = new Dictionary<string, Actor>
        {
            ["1"] = new Actor("TestPlayer", "player", 1L, true, 42L,
                1, 60, 100000L, 200000L,
                Array.Empty<long[]>(), Array.Empty<int[]>(), Array.Empty<int[]>(),
                Array.Empty<Fashion>()),
        };
        var enc = new Encounter("dungeon", 0L, null, 100, 0, null, 0, null, null, 0,
            "partial", 1000L, 2000L, 1000L, 0);
        var upl = new Uploader(42L, "sig", "nonce");
        var hdr = new LogHeader("test-log-id", 2000L, "2.11", "SEA", "1.8.0", "1.1.0", "unlisted", enc, upl);
        var events = new List<CombatLogEvent>
        {
            new DamageEvent(1500L, "1", "enemy", 5, 999, 990, 0, false, false, false, false, 0, 0, 0),
        };
        var log = new CombatLog(1, hdr, actors, events);

        var json = CombatLogWriter.Write(log);

        Assert.Contains("\"v\":1", json);
        Assert.Contains("\"logId\":\"test-log-id\"", json);
        Assert.Contains("\"t\":\"dmg\"", json);
        Assert.Contains("\"kind\":\"player\"", json);
    }

    // -------------------------------------------------------------------------
    // CanonicalPayload format
    // -------------------------------------------------------------------------

    [Fact]
    public void CanonicalPayload_FormatMatchesServiceSpec()
    {
        // Verifies the canonical payload format: logId|levelUuid|localUid|startMs|endMs|nonce|sha256hex(events)
        var actors = new Dictionary<string, Actor>();
        var enc = new Encounter("dungeon", 77L, null, 100, 0, null, 0, null, null, 0,
            "kill", 1000L, 2000L, 1000L, 0);
        var upl = new Uploader(55L, "", "abc123nonce");
        var hdr = new LogHeader("my-log-id", 2000L, "2.11", "SEA", null, null, "public", enc, upl);
        var log = new CombatLog(1, hdr, actors, new List<CombatLogEvent>());

        var payload = CanonicalPayload.Build(log);

        // Must start with logId|levelUuid|localUid|startMs|endMs|nonce|
        Assert.StartsWith("my-log-id|77|55|1000|2000|abc123nonce|", payload);
        // The last segment must be a 64-char lowercase hex SHA-256 hash.
        var parts = payload.Split('|');
        Assert.Equal(7, parts.Length);
        Assert.Equal(64, parts[6].Length);
        Assert.Matches("^[0-9a-f]{64}$", parts[6]);
    }
}
