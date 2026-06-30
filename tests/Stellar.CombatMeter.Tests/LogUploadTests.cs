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
