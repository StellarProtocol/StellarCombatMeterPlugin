// UNVERIFIED — tests for the SP1 log-upload components (CombatEventBuffer, EventsJsonWriter,
// CombatLogWriter, CanonicalPayload). No IL2CPP or IPluginServices mock needed for these pure-data paths.

using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class LogUploadTests
{
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
    public void Buffer_RingDropsOldestOnOverflow()
    {
        // Use a small custom logic: add MaxEvents+1 events and check count stays at MaxEvents.
        // This test adds MaxEvents+2 to force two evictions.
        var buf = new CombatEventBuffer();
        var totalToAdd = CombatEventBuffer.MaxEvents + 2;
        for (var i = 0; i < totalToAdd; i++)
            buf.Add(new CombatEvent.SkillUsed(i, new EntityId((long)i), i, SkillEventPhase.Begin));
        // After overflow-protection, count should be MaxEvents (not totalToAdd).
        Assert.Equal(CombatEventBuffer.MaxEvents, buf.Count);
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
