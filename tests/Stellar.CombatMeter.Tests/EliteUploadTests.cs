using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// ELITE CAPTURE channel (owner ruling 2026-08-13, verbatim): "elites (MonsterType==1) get HP + movement
// + identity capture SAME AS BOSSES, but the auto-archive engine, cuts, verdict, bossId/bosses[],
// killed-boss tracker, and every config toggle stay BOSS-TYPE-ONLY — the elite channel must feed NOTHING
// in AutoArchive/BossStatus/verdict paths." These tests mirror the Task 5/6 boss upload tests in
// LogUploadTests.cs (Writer_emits_bosses_array_when_present, ResolveStageBosses_*, BuildEncounter_*),
// but for the CAPTURE-ONLY elites[] channel — which, unlike bosses[], carries no scalar representative.
public sealed class EliteUploadTests
{
    // -------------------------------------------------------------------------
    // EliteRepresentative.ResolveElites — plain map, no representative/fallback logic.
    // -------------------------------------------------------------------------

    [Fact]
    public void ResolveElites_TwoMembers_BothCarried()
    {
        var members = new[]
        {
            (Id: new EntityId(20), ConfigId: 200100, Killed: true),
            (Id: new EntityId(21), ConfigId: 200101, Killed: false),
        };

        var elites = EliteRepresentative.ResolveElites(members);

        Assert.NotNull(elites);
        Assert.Equal(2, elites!.Count);
        Assert.Contains(elites, e => e.ConfigId == 200100 && e.Killed);
        Assert.Contains(elites, e => e.ConfigId == 200101 && !e.Killed);
    }

    [Fact]
    public void ResolveElites_Empty_ReturnsNull()
    {
        var elites = EliteRepresentative.ResolveElites(
            Array.Empty<(EntityId Id, int ConfigId, bool Killed)>());
        Assert.Null(elites);
    }

    // entry.Elites defaults to an empty list (never null) for an entry built before this field existed —
    // mirrors EncounterHistoryEntry_DefaultStageBosses_IsEmpty_NotNull.
    [Fact]
    public void EncounterHistoryEntry_DefaultElites_IsEmpty_NotNull()
    {
        var entry = new Plugin.EncounterHistoryEntry();
        Assert.Empty(entry.Elites);
    }

    // -------------------------------------------------------------------------
    // BuildEncounter wiring — the resolved elites list reaches the encounter exactly as Assemble
    // composes it (ResolveElites -> BuildEncounter), without needing a full IPluginServices fake.
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildEncounter_CarriesResolvedElites()
    {
        var entry = new Plugin.EncounterHistoryEntry { SceneName = "13021", LevelUuid = 1 };
        var members = new[] { (Id: new EntityId(20), ConfigId: 200100, Killed: true) };
        var elites = EliteRepresentative.ResolveElites(members);

        var enc = CombatLogAssembler.BuildEncounter(entry, elites: elites);

        Assert.NotNull(enc.Elites);
        Assert.Single(enc.Elites!);
        Assert.Equal(200100, enc.Elites![0].ConfigId);
        Assert.True(enc.Elites![0].Killed);
    }

    // Backward compat: omitting `elites` (old call sites, e.g. the replay-doc builder in
    // Plugin.Replay.cs) leaves Encounter.Elites null.
    [Fact]
    public void BuildEncounter_OmittedElites_StaysNull()
    {
        var entry = new Plugin.EncounterHistoryEntry { SceneName = "13021", LevelUuid = 1 };
        var enc = CombatLogAssembler.BuildEncounter(entry);
        Assert.Null(enc.Elites);
    }

    // BuildEncounter must not conflate bosses and elites — a segment can carry both independently.
    [Fact]
    public void BuildEncounter_CarriesBothBossesAndElitesIndependently()
    {
        var entry = new Plugin.EncounterHistoryEntry { SceneName = "13021", LevelUuid = 1 };
        var bosses = new[] { new BossRec(102800, true) };
        var elites = new[] { new EliteRec(200100, false) };

        var enc = CombatLogAssembler.BuildEncounter(entry, bosses: bosses, elites: elites);

        Assert.NotNull(enc.Bosses);
        Assert.NotNull(enc.Elites);
        Assert.Equal(102800, enc.Bosses![0].ConfigId);
        Assert.Equal(200100, enc.Elites![0].ConfigId);
    }

    // -------------------------------------------------------------------------
    // CombatLogWriter — additive elites[] array, byte-identical omission on old-shape uploads.
    // -------------------------------------------------------------------------

    [Fact]
    public void Writer_emits_elites_array_when_present()
    {
        var enc = new Encounter("dungeon", 77L, null, 100, 0, null, 0, null, null, 0,
            "kill", 1000L, 2000L, 1000L, 0,
            Elites: new[] { new EliteRec(200100, true), new EliteRec(200101, false) });
        var hdr = new LogHeader("cm-elites", 2000L, "2.11", "SEA", null, null, "unlisted",
            enc, new Uploader(55L, "sig", "nonce"));
        var log = new CombatLog(1, hdr, new Dictionary<string, Actor>(), Array.Empty<CombatLogEvent>());

        var json = CombatLogWriter.Write(log);

        Assert.Contains("\"elites\"", json);
        Assert.Contains("200100", json);
        Assert.Contains("200101", json);
        Assert.Contains("\"killed\":true", json);
    }

    [Fact]
    public void Writer_omits_elites_when_null_backcompat()
    {
        var enc = new Encounter("dungeon", 77L, null, 100, 0, null, 0, null, null, 0,
            "partial", 1000L, 2000L, 1000L, 0);   // Elites left at default null
        var hdr = new LogHeader("cm-no-elites", 2000L, "2.11", "SEA", null, null, "unlisted",
            enc, new Uploader(55L, "sig", "nonce"));
        var log = new CombatLog(1, hdr, new Dictionary<string, Actor>(), Array.Empty<CombatLogEvent>());

        var json = CombatLogWriter.Write(log);

        Assert.DoesNotContain("\"elites\"", json);
    }

    // A killed=false elite must not emit the "killed" key at all (mirrors the bosses[] convention).
    [Fact]
    public void Writer_omits_killed_key_when_elite_not_killed()
    {
        var enc = new Encounter("dungeon", 77L, null, 100, 0, null, 0, null, null, 0,
            "partial", 1000L, 2000L, 1000L, 0, Elites: new[] { new EliteRec(200100, false) });
        var hdr = new LogHeader("cm-elite-alive", 2000L, "2.11", "SEA", null, null, "unlisted",
            enc, new Uploader(55L, "sig", "nonce"));
        var log = new CombatLog(1, hdr, new Dictionary<string, Actor>(), Array.Empty<CombatLogEvent>());

        var json = CombatLogWriter.Write(log);

        Assert.DoesNotContain("\"killed\"", json);
    }

    // Signature safety: Elites (like Bosses/bossId/partyId) is NOT covered by the canonical payload.
    [Fact]
    public void CanonicalPayload_is_invariant_to_elites()
    {
        var actors = new Dictionary<string, Actor>();
        var upl = new Uploader(55L, "", "abc123nonce");
        Encounter Enc(IReadOnlyList<EliteRec>? elites) => new Encounter("dungeon", 77L, null, 100, 0, null, 0, null, null, 0,
            "kill", 1000L, 2000L, 1000L, 0, Elites: elites);

        var without = CanonicalPayload.Build(new CombatLog(1,
            new LogHeader("my-log-id", 2000L, "2.11", "SEA", null, null, "public", Enc(null), upl),
            actors, new List<CombatLogEvent>()));
        var with = CanonicalPayload.Build(new CombatLog(1,
            new LogHeader("my-log-id", 2000L, "2.11", "SEA", null, null, "public",
                Enc(new[] { new EliteRec(200100, true) }), upl),
            actors, new List<CombatLogEvent>()));

        Assert.Equal(without, with);
    }
}
