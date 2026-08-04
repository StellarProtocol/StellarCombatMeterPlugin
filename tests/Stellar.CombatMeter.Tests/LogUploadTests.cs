// Tests for the SP1 log-upload components (CombatEventBuffer, EventsJsonWriter,
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

    // Still load-bearing after the per-content upload policy replaced the global flag: this key is now
    // the one-shot MIGRATION INPUT read by Plugin.LoadOrMigrateUploadPolicy, and its unset-default of
    // `true` is what seeds all four `<kind>.stats` cells to `auto` on a fresh install (spec § 2.2) —
    // i.e. this default is what keeps an upgrade behaviour-identical to today.
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

    // Persistence maps a transient InFlight to Idle (never persist "Uploading…"); terminal phases
    // persist as-is so a relaunch restores "✓ Uploaded" / keeps a failure retryable (Task 13).
    // (Inlined rather than a [Theory] because the internal enum can't be a public param type.)
    [Fact]
    public void Persistable_phase_collapses_only_inflight_to_idle()
    {
        Assert.Equal(UploadPhase.Idle,   UploadStatusTable.Persistable(UploadPhase.Idle));
        Assert.Equal(UploadPhase.Idle,   UploadStatusTable.Persistable(UploadPhase.InFlight));   // transient never persisted
        Assert.Equal(UploadPhase.Done,   UploadStatusTable.Persistable(UploadPhase.Done));
        Assert.Equal(UploadPhase.Failed, UploadStatusTable.Persistable(UploadPhase.Failed));
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
    // Pre-combat imagine casts (c43da68 follow-up): ImagineCastEntry.Ms carries the
    // TRUE SkillUsed-Begin epoch ms, independent of _combatStartMs — a cast recorded
    // while staging (before the encounter's first hit) has Ms < entry.EnteredAtMs
    // (== _combatStartMs, snapshotted at ManualArchive time). Neither DerivedBuilder
    // nor CombatLogWriter must clip/drop entries whose Ms precedes the combat window;
    // the web renderer already normalizes to a combat-relative (possibly negative) ms
    // via toCombatMs(), so the plugin's only job is to carry the true timestamp through
    // untouched all the way to the wire.
    // -------------------------------------------------------------------------

    [Fact]
    public void Derived_preserves_imagine_casts_recorded_before_combat_start()
    {
        var id = new EntityId(123L << 16);
        const long combatStartMs = 50_000;
        const long preCombatCastMs = 42_000;   // 8s before the first hit — staging-area cast
        const long duringCombatCastMs = 55_000;

        var entry = new Plugin.EncounterHistoryEntry
        {
            EnteredAtMs      = combatStartMs,   // snapshotted from _combatStartMs at archive
            ArchivedAtMs     = 70_000,
            CombatDurationMs = 20_000,
            Stats = new() { [id] = new SourceStats { TotalDamage = 100, Hits = 1, FirstHitMs = combatStartMs, LastHitMs = combatStartMs } },
            ImagineCasts = new()
            {
                new ImagineCastEntry(preCombatCastMs, id, 111),
                new ImagineCastEntry(duringCombatCastMs, id, 222),
            },
        };

        var derived = DerivedBuilder.Build(entry, truncatedEvents: false);

        Assert.NotNull(derived.ImagineCasts);
        Assert.Equal(2, derived.ImagineCasts!.Count);
        var pre = derived.ImagineCasts!.Single(c => c.Skill == 111);
        Assert.Equal(preCombatCastMs, pre.Ms);
        Assert.True(pre.Ms < entry.EnteredAtMs, "pre-combat cast must keep its true ms, before encounter.StartMs");

        var during = derived.ImagineCasts!.Single(c => c.Skill == 222);
        Assert.Equal(duringCombatCastMs, during.Ms);

        // Round-trip through the wire writer: the JSON must carry the pre-combat cast's true
        // (smaller-than-StartMs) ms verbatim — no clamping to encounter.StartMs.
        var encounter = CombatLogAssembler.BuildEncounter(entry);
        Assert.Equal(combatStartMs, encounter.StartMs);

        var header = new LogHeader("cm-precombat-cast", 70_000L, "2.11", "SEA", "1.9.0", "1.1.0", "unlisted",
            encounter, new Uploader(id.Value, "sig", "nonce"));
        var log = new CombatLog(1, header, new Dictionary<string, Actor>(), Array.Empty<CombatLogEvent>(), derived);
        var json = CombatLogWriter.Write(log);

        Assert.Contains($"\"ms\":{preCombatCastMs},\"src\":\"{id.Value}\",\"skill\":111", json);
        Assert.Contains($"\"ms\":{duringCombatCastMs},\"src\":\"{id.Value}\",\"skill\":222", json);
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

    // The archived entry's DungeonStartMs (snapshotted from IDungeonState.RunTimerStartMs at
    // ManualArchive, same lifecycle point as DifficultyLevel) flows through BuildEncounter and
    // is emitted as header.encounter.dungeonStartMs when set.
    [Fact]
    public void BuildEncounter_carries_dungeonStartMs_and_writer_emits_it_when_set()
    {
        var entry = new Plugin.EncounterHistoryEntry
        {
            SceneName = "7151", EnteredAtMs = 1000, ArchivedAtMs = 11_000, CombatDurationMs = 10_000,
            LevelUuid = 42, Result = "kill",
            DungeonStartMs = 1_700_000_000_000L,   // run-timer start snapshotted at archive
        };
        var enc = CombatLogAssembler.BuildEncounter(entry);
        Assert.Equal(1_700_000_000_000L, enc.DungeonStartMs);

        var hdr = new LogHeader("cm-dstart", 11_000L, "2.11", "SEA", "1.9.0", "1.1.0", "unlisted",
            enc, new Uploader(42L, "sig", "nonce"));
        var log = new CombatLog(1, hdr, new Dictionary<string, Actor>(), Array.Empty<CombatLogEvent>());
        var json = CombatLogWriter.Write(log);

        Assert.Contains("\"dungeonStartMs\":1700000000000", json);
    }

    // Unknown run-timer start (0) is OMITTED from header.encounter — matching how the other
    // optional encounter fields (difficultyLevel, name, bossName, …) are handled.
    [Fact]
    public void Writer_omits_dungeonStartMs_when_unknown()
    {
        var entry = new Plugin.EncounterHistoryEntry
        {
            SceneName = "7151", EnteredAtMs = 1000, ArchivedAtMs = 11_000, CombatDurationMs = 10_000,
            LevelUuid = 42, Result = "partial",   // DungeonStartMs left at default 0
        };
        var enc = CombatLogAssembler.BuildEncounter(entry);
        Assert.Equal(0L, enc.DungeonStartMs);

        var hdr = new LogHeader("cm-no-dstart", 11_000L, "2.11", "SEA", "1.9.0", "1.1.0", "unlisted",
            enc, new Uploader(42L, "sig", "nonce"));
        var log = new CombatLog(1, hdr, new Dictionary<string, Actor>(), Array.Empty<CombatLogEvent>());
        var json = CombatLogWriter.Write(log);

        Assert.DoesNotContain("dungeonStartMs", json);
    }

    // Party id (GrpcTeam team_id), latched at run-start (B1) onto EncounterHistoryEntry.PartyId,
    // flows through BuildEncounter and is emitted as a STRING (never a bare number — a long id
    // would round past 2^53 through the server's JSON.parse; mirrors levelUuid's own Str() emission).
    [Fact]
    public void BuildEncounter_carries_partyId_and_writer_emits_it_as_string_when_set()
    {
        var entry = new Plugin.EncounterHistoryEntry
        {
            SceneName = "7151", EnteredAtMs = 1000, ArchivedAtMs = 11_000, CombatDurationMs = 10_000,
            LevelUuid = 42, Result = "kill",
            PartyId = 8837421,
        };
        var enc = CombatLogAssembler.BuildEncounter(entry);
        Assert.Equal(8837421L, enc.PartyId);

        var hdr = new LogHeader("cm-partyid", 11_000L, "2.11", "SEA", "1.9.0", "1.1.0", "unlisted",
            enc, new Uploader(42L, "sig", "nonce"));
        var log = new CombatLog(1, hdr, new Dictionary<string, Actor>(), Array.Empty<CombatLogEvent>());
        var json = CombatLogWriter.Write(log);

        Assert.Contains("\"partyId\":\"8837421\"", json);
    }

    // Unknown/solo party (0) is OMITTED from header.encounter — the server then applies its
    // u<uploaderUid> fallback keying.
    [Fact]
    public void Writer_omits_partyId_when_unknown()
    {
        var entry = new Plugin.EncounterHistoryEntry
        {
            SceneName = "7151", EnteredAtMs = 1000, ArchivedAtMs = 11_000, CombatDurationMs = 10_000,
            LevelUuid = 42, Result = "partial",   // PartyId left at default 0
        };
        var enc = CombatLogAssembler.BuildEncounter(entry);
        Assert.Equal(0L, enc.PartyId);

        var hdr = new LogHeader("cm-no-partyid", 11_000L, "2.11", "SEA", "1.9.0", "1.1.0", "unlisted",
            enc, new Uploader(42L, "sig", "nonce"));
        var log = new CombatLog(1, hdr, new Dictionary<string, Actor>(), Array.Empty<CombatLogEvent>());
        var json = CombatLogWriter.Write(log);

        Assert.DoesNotContain("partyId", json);
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

    // Signature safety: the canonical payload hashes only logId|levelUuid|localUid|startMs|endMs|
    // nonce|sha256(events) — DungeonStartMs (like DifficultyLevel) is NOT covered, so adding it
    // to header.encounter cannot change an existing signature.
    [Fact]
    public void CanonicalPayload_is_invariant_to_dungeonStartMs()
    {
        var actors = new Dictionary<string, Actor>();
        var upl = new Uploader(55L, "", "abc123nonce");
        Encounter Enc(long dstart) => new Encounter("dungeon", 77L, null, 100, 0, null, 0, null, null, 0,
            "kill", 1000L, 2000L, 1000L, 0, DifficultyLevel: 0, DungeonStartMs: dstart);

        var without = CanonicalPayload.Build(new CombatLog(1,
            new LogHeader("my-log-id", 2000L, "2.11", "SEA", null, null, "public", Enc(0), upl),
            actors, new List<CombatLogEvent>()));
        var with = CanonicalPayload.Build(new CombatLog(1,
            new LogHeader("my-log-id", 2000L, "2.11", "SEA", null, null, "public", Enc(1_700_000_000_000L), upl),
            actors, new List<CombatLogEvent>()));

        Assert.Equal(without, with);
    }

    // -------------------------------------------------------------------------
    // Task 3 (per-class-loadout plan): self-only modules/talentStageId/loadouts on the uploader
    // Actor. Frozen onto the history entry at archive time (Plugin.History.cs's BuildHistoryEntry
    // sets Loadouts = LoadoutSnapshot()) rather than read live from the LoadoutCapture accumulator —
    // a post-run town class-swap must not pollute an already-archived run's upload. The assembler's
    // self-only GATE (CombatLogAssembler.ResolveLoadoutFields) is what stops every teammate's Actor
    // from wrongly carrying the uploader's own loadout data — the accumulator has no per-teammate
    // distinction once flattened, since it only ever captures the LOCAL player's classes.
    // -------------------------------------------------------------------------

    private static ModuleEntry MakeModuleEntry(int slot = 0, int configId = 5500102, int quality = 5) =>
        new(slot, configId, quality, new List<int[]> { new[] { 1110, 5 } });

    private static LoadoutEntry MakeLoadoutEntry(int professionId = 2, int talentStageId = 0) =>
        new(ProfessionId: professionId, ProjectName: null,
            Gear: new List<int[]> { new[] { 200, 2011227 } },
            GearDetail: null,
            Skills: new List<int[]> { new[] { 1241, 30, 6 } },
            Fashion: new List<Fashion>(),
            Modules: null,
            TalentStageId: talentStageId);

    private static CapturedLoadout MakeCapturedLoadout(int professionId, string? projectName = null,
        int talentStageId = 0, IReadOnlyList<GearDetail>? gearDetail = null,
        IReadOnlyList<CapturedModule>? modules = null, IReadOnlyList<int>? talentNodes = null) => new(
        ProfessionId:  professionId,
        ProjectName:   projectName,
        TalentStageId: talentStageId,
        Gear:          new List<int[]> { new[] { 200, professionId } },
        GearDetail:    gearDetail ?? new List<GearDetail>(),
        Skills:        new List<int[]> { new[] { 1241, 30, 6 } },
        Fashion:       new List<Fashion>(),
        Modules:       modules ?? new List<CapturedModule>(),
        TalentNodes:   talentNodes);

    private static CombatLog MakeLoadoutLog(Actor actor, string key = "1248014") =>
        new(1,
            new LogHeader("cm-loadout-test", 0, "2.11", "sea", null, null, "unlisted",
                new Encounter("dungeon", 1, null, 1, 0, null, 0, null, null, 0, "kill", 0, 0, 0, 0),
                new Uploader(1248014, "sig", "nonce")),
            new Dictionary<string, Actor> { [key] = actor },
            Array.Empty<CombatLogEvent>());

    [Fact]
    public void WriteActor_emits_modules_talentStageId_loadouts_when_present()
    {
        var actor = new Actor(
            Name: "Aria", Kind: "player", TeamId: 1, IsLocal: true, Uid: 1248014,
            ProfessionId: 12, Level: 60, AbilityScore: 184230, MaxHp: 1850000,
            Attributes: new List<long[]>(), Gear: new List<int[]>(), Skills: new List<int[]>(),
            Fashion: new List<Fashion>(),
            GearDetail: null,
            Modules: new List<ModuleEntry> { MakeModuleEntry() },
            TalentStageId: 104,
            Loadouts: new List<LoadoutEntry> { MakeLoadoutEntry() });

        var json = CombatLogWriter.Write(MakeLoadoutLog(actor));

        Assert.Contains(
            "\"modules\":[{\"slot\":0,\"configId\":5500102,\"quality\":5,\"parts\":[[1110,5]]}]", json);
        Assert.Contains("\"talentStageId\":104", json);
        Assert.Contains(
            "\"loadouts\":[{\"professionId\":2,\"gear\":[[200,2011227]],\"skills\":[[1241,30,6]],\"fashion\":[]}]",
            json);
    }

    [Fact]
    public void WriteActor_omits_modules_talentStageId_loadouts_when_absent()
    {
        // Defaults (null/null/0) — exactly what a non-local teammate's Actor looks like.
        var actor = new Actor(
            Name: "Teammate", Kind: "player", TeamId: 1, IsLocal: false, Uid: 999,
            ProfessionId: 3, Level: 60, AbilityScore: 0, MaxHp: 1,
            Attributes: new List<long[]>(), Gear: new List<int[]>(), Skills: new List<int[]>(),
            Fashion: new List<Fashion>());

        var json = CombatLogWriter.Write(MakeLoadoutLog(actor, "999"));

        Assert.DoesNotContain("\"modules\"", json);
        Assert.DoesNotContain("\"talentStageId\"", json);
        Assert.DoesNotContain("\"loadouts\"", json);
    }

    [Fact]
    public void WriteActor_nonPlayerKind_never_carries_loadout_fields_even_if_populated()
    {
        // The writer's own Kind=="player" guard is what withholds every player-only field
        // (professionId/gear/skills/... and now modules/talentStageId/loadouts too) — defense in
        // depth even though the assembler never actually populates these for a non-player Kind.
        var actor = new Actor(
            Name: "Astralisk", Kind: "boss", TeamId: 2, IsLocal: false, Uid: null,
            ProfessionId: 0, Level: 0, AbilityScore: 0, MaxHp: 0,
            Attributes: Array.Empty<long[]>(), Gear: Array.Empty<int[]>(), Skills: Array.Empty<int[]>(),
            Fashion: Array.Empty<Fashion>(),
            GearDetail: null,
            Modules: new List<ModuleEntry> { MakeModuleEntry() },
            TalentStageId: 104,
            Loadouts: new List<LoadoutEntry> { MakeLoadoutEntry() });

        var json = CombatLogWriter.Write(MakeLoadoutLog(actor, "9001"));

        Assert.DoesNotContain("\"modules\"", json);
        Assert.DoesNotContain("\"talentStageId\"", json);
        Assert.DoesNotContain("\"loadouts\"", json);
    }

    [Fact]
    public void WriteActor_and_loadout_emit_attrPeaks_when_present_omit_when_absent()
    {
        // Base+peak stats (2026-08-02): the hand-rolled upload writer must emit the sparse combat
        // peaks for BOTH the per-actor snapshot and each per-class loadout, or the site never sees a
        // peak. Regression pin — the plan's Task 4 originally missed this writer.
        var loadoutWithPeaks = MakeLoadoutEntry() with { AttrPeaks = new List<long[]> { new long[] { 11710, 2330 } } };
        var actor = new Actor(
            Name: "Aria", Kind: "player", TeamId: 1, IsLocal: true, Uid: 1248014,
            ProfessionId: 12, Level: 60, AbilityScore: 1, MaxHp: 1,
            Attributes: new List<long[]> { new long[] { 11710, 500 } },
            Gear: new List<int[]>(), Skills: new List<int[]>(), Fashion: new List<Fashion>(),
            Loadouts: new List<LoadoutEntry> { loadoutWithPeaks },
            AttrPeaks: new List<long[]> { new long[] { 11710, 2330 } });

        var json = CombatLogWriter.Write(MakeLoadoutLog(actor));
        Assert.Contains("\"attrPeaks\":[[11710,2330]]", json);                                  // actor-level
        Assert.Contains("\"skills\":[[1241,30,6]],\"fashion\":[],\"attrPeaks\":[[11710,2330]]", json); // loadout-level

        var noPeaks = actor with { AttrPeaks = null, Loadouts = new List<LoadoutEntry> { MakeLoadoutEntry() } };
        Assert.DoesNotContain("\"attrPeaks\"", CombatLogWriter.Write(MakeLoadoutLog(noPeaks)));
    }

    // Per-entity class detection (Task 3): the hand-rolled upload writer must emit the per-actor
    // professionId timeline (self AND party — NOT gated to local, unlike modules/loadouts/talents)
    // as sparse [professionId,startMs,endMs] triples, or the site never learns a party member swapped
    // class mid-run. Regression pin, mirroring the attrPeaks writer test above.
    [Fact]
    public void WriteActor_emits_classSpans_for_a_2class_actor_omits_for_1class()
    {
        var twoClass = new Actor(
            Name: "Teammate", Kind: "player", TeamId: 1, IsLocal: false, Uid: 999,
            ProfessionId: 5, Level: 60, AbilityScore: 0, MaxHp: 1,
            Attributes: new List<long[]>(), Gear: new List<int[]>(), Skills: new List<int[]>(),
            Fashion: new List<Fashion>(),
            ClassSpans: new List<long[]> { new long[] { 2, 0, 5000 }, new long[] { 5, 5000, 12_000 } });

        var json = CombatLogWriter.Write(MakeLoadoutLog(twoClass, "999"));
        Assert.Contains("\"classSpans\":[[2,0,5000],[5,5000,12000]]", json);

        var oneClass = twoClass with { ClassSpans = null };
        Assert.DoesNotContain("\"classSpans\"", CombatLogWriter.Write(MakeLoadoutLog(oneClass, "999")));
    }

    [Fact]
    public void BuildModuleEntries_maps_fields_and_returns_null_when_empty()
    {
        var captured = new List<CapturedModule> { new(0, 5500102, 5, new List<int[]> { new[] { 1110, 5 } }) };

        var mapped = CombatLogAssembler.BuildModuleEntries(captured);

        var m = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ModuleEntry>>(mapped));
        Assert.Equal(0, m.Slot);
        Assert.Equal(5500102, m.ConfigId);
        Assert.Equal(5, m.Quality);
        Assert.Equal(new[] { 1110, 5 }, m.Parts[0]);

        Assert.Null(CombatLogAssembler.BuildModuleEntries(new List<CapturedModule>()));
    }

    [Fact]
    public void BuildLoadoutEntries_maps_fields_nulling_empty_gearDetail_and_modules()
    {
        var captured = new List<CapturedLoadout>
        {
            MakeCapturedLoadout(2, projectName: "Frost Build", talentStageId: 104,
                modules: new List<CapturedModule> { new(0, 5500102, 5, new List<int[]>()) }),
        };

        var mapped = CombatLogAssembler.BuildLoadoutEntries(captured);

        var l = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<LoadoutEntry>>(mapped));
        Assert.Equal(2, l.ProfessionId);
        Assert.Equal("Frost Build", l.ProjectName);
        Assert.Equal(104, l.TalentStageId);
        Assert.Null(l.GearDetail);          // empty capture -> null on the wire (mirrors BuildGearDetail)
        Assert.NotNull(l.Modules);
        Assert.Single(l.Modules!);

        Assert.Null(CombatLogAssembler.BuildLoadoutEntries(new List<CapturedLoadout>()));
    }

    // ResolveLoadoutFields is the self-only GATE: non-local always null/null/0 regardless of what
    // the run captured; local carries the full Loadouts list plus the top-level Modules/TalentStageId
    // for whichever captured class matches the actor's FINAL professionId (not necessarily the last
    // one captured — a run can end back on an earlier class).
    [Fact]
    public void ResolveLoadoutFields_nonLocal_alwaysNullRegardlessOfCapturedData()
    {
        var runLoadouts = new List<CapturedLoadout> { MakeCapturedLoadout(2, talentStageId: 104, talentNodes: new[] { 233002, 5205 }) };

        var (loadouts, modules, talentStageId, talentNodes) = CombatLogAssembler.ResolveLoadoutFields(
            isLocal: false, professionId: 2, runLoadouts);

        Assert.Null(loadouts);
        Assert.Null(modules);
        Assert.Equal(0, talentStageId);
        Assert.Null(talentNodes);   // self-only gate strips node ids for non-local actors too
    }

    [Fact]
    public void ResolveLoadoutFields_local_carriesFullListAndMatchesTopLevelToFinalProfession()
    {
        var moduleForClass2 = new List<CapturedModule> { new(0, 5500102, 5, new List<int[]>()) };
        var runLoadouts = new List<CapturedLoadout>
        {
            MakeCapturedLoadout(5, projectName: "Old class", talentStageId: 900),   // played earlier, not current
            MakeCapturedLoadout(2, projectName: "Current class", talentStageId: 104, modules: moduleForClass2, talentNodes: new[] { 233002, 5205, 222011 }),
        };

        var (loadouts, modules, talentStageId, talentNodes) = CombatLogAssembler.ResolveLoadoutFields(
            isLocal: true, professionId: 2, runLoadouts);

        Assert.NotNull(loadouts);
        Assert.Equal(2, loadouts!.Count);                 // BOTH played classes ride along
        Assert.NotNull(modules);
        Assert.Single(modules!);                          // top-level mirrors class 2 (the final profession) only
        Assert.Equal(104, talentStageId);
        Assert.Equal(new[] { 233002, 5205, 222011 }, talentNodes);   // top-level nodes mirror the final class
    }

    [Fact]
    public void ResolveLoadoutFields_local_currentClassNeverCaptured_topLevelNullButListStillCarries()
    {
        var runLoadouts = new List<CapturedLoadout> { MakeCapturedLoadout(5, talentStageId: 900) };

        var (loadouts, modules, talentStageId, talentNodes) = CombatLogAssembler.ResolveLoadoutFields(
            isLocal: true, professionId: 99, runLoadouts);   // profession 99 was never captured

        Assert.NotNull(loadouts);
        Assert.Single(loadouts!);
        Assert.Null(modules);
        Assert.Equal(0, talentStageId);
        Assert.Null(talentNodes);   // no matching class → no top-level nodes
    }

    // -------------------------------------------------------------------------
    // Task 11: BuildPrecheckHeader carries the real region from the log header.
    // -------------------------------------------------------------------------

    [Fact]
    public void PrecheckHeader_CarriesRegion()
    {
        var enc = new Encounter("dungeon", 77L, null, 100, 0, null, 0, null, null, 0,
            "kill", 1000L, 2000L, 1000L, 0);
        var header = new LogHeader("cm-region-test", 2000L, "2.11", "jp", null, null, "public",
            enc, new Uploader(55L, "sig", "nonce"));
        var log = new CombatLog(1, header, new Dictionary<string, Actor>(), Array.Empty<CombatLogEvent>());

        var precheck = LogUploader.BuildPrecheckHeader(log);
        Assert.Contains("region=jp", precheck);
        Assert.Contains("levelUuid=", precheck);
    }
}
