// UNVERIFIED — this code has never been executed in-game.
// SP1: Assembles the full CombatLog DTO from the captured encounter state.
// Encounter metadata stubs are clearly marked TODO(SP1) where game API access is needed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Assembles a <see cref="CombatLog"/> from the raw encounter data captured by the plugin.
/// Called once per run-archive; not on the hot path.
/// </summary>
internal sealed class CombatLogAssembler
{
    private readonly IPluginServices _services;

    internal CombatLogAssembler(IPluginServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Builds the complete <see cref="CombatLog"/> ready for signing and upload.
    /// </summary>
    /// <param name="entry">The archived encounter history entry (stats + entity snapshots).</param>
    /// <param name="events">Raw combat events flushed from <see cref="CombatEventBuffer"/>.</param>
    /// <param name="signerKey">
    /// Base64-PKCS#8 private key, or null/empty to produce an empty placeholder signature
    /// (upload will be rejected by the server if <c>UPLOAD_PUBKEY</c> is set).
    /// </param>
    /// <param name="truncatedEvents">
    /// True when the raw dmg/skill forensic ring overflowed during the encounter. Forensic
    /// metadata only — every rendered number comes from the unsigned <c>derived</c> aggregates.
    /// </param>
    internal CombatLog Assemble(
        Plugin.EncounterHistoryEntry entry,
        IReadOnlyList<CombatLogEvent> events,
        string? signerKey,
        bool truncatedEvents)
    {
        var logId    = GenerateLogId();
        var nowMs    = _services.CombatSnapshot.ServerNowMs;

        // Encounter identity comes entirely from the entry (snapshotted at archive),
        // so a deferred manual upload stamps the run's OWN levelUuid/settlement.
        var bossConfigId = ResolveBossConfigId(entry);
        var encounter    = BuildEncounter(entry, bossConfigId);

        // --- Uploader ---
        var localUid = _services.CombatSnapshot.LocalEntityId.Value;
        var nonce    = GenerateNonce();

        // Build a temporary uploader with empty sig, then compute the real sig over the assembled log.
        var uploaderUnsigned = new Uploader(localUid, "", nonce);

        // --- Actors from entity snapshots ---
        var actors = BuildActors(entry, localUid);

        // --- Framework / plugin versions ---
        string? frameworkVer = null;
        string? pluginVer    = null;
        try
        {
            frameworkVer = Stellar.Abstractions.Domain.FrameworkVersion.Value;
            pluginVer    = typeof(Plugin).Assembly.GetName().Version?.ToString(3);
        }
        catch
        {
            // Defensive: version resolution failures must not block the upload.
        }

        var header = new LogHeader(
            LogId:        logId,
            CapturedAtMs: nowMs,
            GameVersion:  "2.11",                 // TODO(SP1): read live game version from IClientState once exposed
            Region:       "SEA",                  // TODO(SP1): read from launcher config once exposed
            FrameworkVer: frameworkVer,
            PluginVer:    pluginVer,
            Privacy:      "unlisted",              // default; TODO(SP1): expose per-user privacy pref in settings
            Encounter:    encounter,
            Uploader:     uploaderUnsigned);

        // Plugin-authoritative aggregates (uncapped) ride alongside the (capped) raw event detail track.
        var derived = DerivedBuilder.Build(entry, truncatedEvents);
        var logUnsigned = new CombatLog(1, header, actors, events, derived);

        // --- Signature ---
        var sig = ComputeSig(logUnsigned, signerKey);
        var uploaderSigned = new Uploader(localUid, sig, nonce);
        var headerSigned   = header with { Uploader = uploaderSigned };
        return logUnsigned with { Header = headerSigned };
    }

    /// <summary>
    /// Builds the <see cref="Encounter"/> header purely from the archived entry — no live
    /// <see cref="IDungeonState"/> reads. This is what makes a deferred (manual) upload correct:
    /// run-identity (<c>LevelUuid</c>/<c>PassTime</c>/<c>MasterModeScore</c>/<c>Result</c>) was
    /// snapshotted onto the entry at archive time, so re-uploading an old run later cannot leak
    /// the currently-live run's identity onto it.
    /// </summary>
    /// <param name="entry">The archived encounter history entry.</param>
    /// <param name="bossConfigId">
    /// Monster-table config id of the identified boss entity, or 0 when no boss was found.
    /// Resolved from <c>IGameDataWorld.GetMonsterByEntity</c> at assemble time via
    /// <see cref="ResolveBossConfigId"/>.
    /// </param>
    internal static Encounter BuildEncounter(Plugin.EncounterHistoryEntry entry, int bossConfigId = 0)
    {
        var sceneName = entry.SceneName ?? "";
        if (!int.TryParse(sceneName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sceneMapId))
            sceneMapId = 0;

        // Encounter kind heuristic from party type.
        var encounterKind = entry.PartyType switch
        {
            PartyType.Raid20 => "raid",
            _                => "dungeon",
        };

        return new Encounter(
            Kind:            encounterKind,
            LevelUuid:       entry.LevelUuid,
            DungeonGuid:     null,                 // TODO(enrich-later): from SceneData via IGameDataWorld.GetScene
            MapId:           sceneMapId,
            LineId:          0,                    // TODO(enrich-later): lineId from server scene info
            Name:            null,                 // TODO(enrich-later): GetScene(sceneMapId)?.Name
            BossId:          bossConfigId,
            BossName:        null,
            Difficulty:      null,
            MasterModeScore: entry.MasterModeScore,
            Result:          entry.Result,
            StartMs:         entry.EnteredAtMs,
            EndMs:           entry.ArchivedAtMs,
            DurationMs:      entry.ArchivedAtMs - entry.EnteredAtMs,
            PassTime:        entry.PassTime);
    }

    /// <summary>
    /// Resolves the boss's monster-table config id from the entry's captured entity set.
    /// Iterates non-player entities, calls <c>GetMonsterByEntity</c> for each, runs
    /// <see cref="BossPicker"/> to choose the highest-MaxHp boss, and returns its config id.
    /// Returns 0 when no boss entity is found or when the monster table is not yet loaded.
    /// </summary>
    private int ResolveBossConfigId(Plugin.EncounterHistoryEntry entry)
    {
        var candidates = new List<(long id, bool isBoss, long maxHp)>(entry.Entities.Count);
        foreach (var (entityId, _) in entry.Entities)
        {
            if (entityId.IsPlayer) continue;
            var info   = _services.GameData.World.GetMonsterByEntity(entityId);
            var isBoss = info.HasValue && info.Value.IsBoss;
            // Use MaxHp as a tie-break only; 0 when vitals unknown is acceptable — we must
            // NOT skip a boss candidate just because its vitals are not yet populated.
            var maxHp  = _services.CombatLookup.GetVitals(entityId).MaxHp;
            candidates.Add((entityId.Value, isBoss, maxHp));
        }
        var bossId = Replay.BossPicker.Pick(candidates);
        if (!bossId.HasValue) return 0;
        var bossEntity = new EntityId(bossId.Value);
        var bossInfo   = _services.GameData.World.GetMonsterByEntity(bossEntity);
        return bossInfo.HasValue ? bossInfo.Value.Id : 0;
    }

    private static string ComputeSig(CombatLog log, string? signerKey)
    {
        if (string.IsNullOrWhiteSpace(signerKey))
            return "";   // placeholder; server will reject if UPLOAD_PUBKEY is configured

        try
        {
            var payload = CanonicalPayload.Build(log);
            using var signer = new LogSigner(signerKey);
            return signer.Sign(payload);
        }
        catch (Exception ex)
        {
            // Key provisioning or crypto failure — return placeholder and let the server reject.
            // The caller logs the error separately.
            return $"sig-error:{ex.Message}";
        }
    }

    private Dictionary<string, Actor> BuildActors(
        Plugin.EncounterHistoryEntry entry,
        long localEntityIdValue)
    {
        var actors = new Dictionary<string, Actor>();
        foreach (var (entityId, snap) in entry.Entities)
        {
            var key = entityId.Value.ToString(CultureInfo.InvariantCulture);
            actors[key] = SnapToActor(entityId, snap, localEntityIdValue);
        }
        return actors;
    }

    private Actor SnapToActor(EntityId entityId, EntitySnapshot snap, long localEntityIdValue)
    {
        var isLocal  = entityId.Value == localEntityIdValue;
        var teamId   = snap.TeamId;
        var name     = snap.Name ?? EntityLabel.Resolve(
            entityId,
            _services.CombatSnapshot.LocalEntityId,
            _services.PlayerState,
            _services.CombatLookup,
            _services.PartyRoster.Members);

        // Build attribute pairs [[attrId, value], ...]
        var attrs = new long[snap.AttrIds.Length][];
        for (var i = 0; i < snap.AttrIds.Length; i++)
            attrs[i] = new long[] { snap.AttrIds[i], snap.AttrValues[i] };

        // Build gear pairs [[slot, itemId], ...]
        var gear = new int[snap.GearSlots.Length][];
        for (var i = 0; i < snap.GearSlots.Length; i++)
            gear[i] = new[] { snap.GearSlots[i], snap.GearItemIds[i] };

        // Build skill triples [[skillId, level, tier], ...]
        var skills = new int[snap.SkillIds.Length][];
        for (var i = 0; i < snap.SkillIds.Length; i++)
            skills[i] = new[] { snap.SkillIds[i], snap.SkillLevels[i], snap.SkillTiers[i] };

        // Build fashion entries [[slot, fashionId, [dyes...]], ...]
        var fashion = new List<Fashion>(snap.FashionIds.Length);
        var dyeOffset = 0;
        for (var i = 0; i < snap.FashionIds.Length; i++)
        {
            var count = i < snap.FashionDyeCounts.Length ? snap.FashionDyeCounts[i] : 0;
            var dyes  = new float[count * 4];
            for (var d = 0; d < count * 4 && dyeOffset + d < snap.FashionDyes.Length; d++)
                dyes[d] = snap.FashionDyes[dyeOffset + d];
            dyeOffset += count * 4;
            fashion.Add(new Fashion(snap.FashionSlots[i], snap.FashionIds[i], dyes));
        }

        // Level from attributes (AttrLevel = 10000, matches Plugin.SessionSnapshot.Build.cs).
        const int AttrLevel = 10000;
        var level = 0;
        for (var i = 0; i < snap.AttrIds.Length; i++)
            if (snap.AttrIds[i] == AttrLevel) { level = (int)snap.AttrValues[i]; break; }

        // ProfessionId from attributes (AttrProfessionId = 220).
        const int AttrProfessionId = 220;
        var professionId = 0;
        for (var i = 0; i < snap.AttrIds.Length; i++)
            if (snap.AttrIds[i] == AttrProfessionId) { professionId = (int)snap.AttrValues[i]; break; }

        // Uid: the high 48 bits of EntityId.Value encode the CharId (per Plugin.cs GetClassLine).
        long? uid = entityId.IsPlayer ? (entityId.Value >> 16) : (long?)null;

        return new Actor(
            Name:         name ?? "Unknown",
            Kind:         "player",
            TeamId:       teamId,
            IsLocal:      isLocal,
            Uid:          uid,
            ProfessionId: professionId,
            Level:        level,
            AbilityScore: snap.FightPoint,
            MaxHp:        snap.MaxHp,
            Attributes:   attrs,
            Gear:         gear,
            Skills:       skills,
            Fashion:      fashion);
    }

    private static string GenerateLogId()
    {
        // logId format: "cm-{yyyyMMddHHmmss}-{8-char random hex}"
        // "cm" prefix identifies CombatMeter as the uploader.
        var now    = DateTime.UtcNow;
        var rand   = new byte[4];
        RandomNumberGenerator.Fill(rand);
        var hex    = BitConverter.ToString(rand).Replace("-", "").ToLowerInvariant();
        return $"cm-{now:yyyyMMddHHmmss}-{hex}";
    }

    private static string GenerateNonce()
    {
        var rand = new byte[12];
        RandomNumberGenerator.Fill(rand);
        return Convert.ToBase64String(rand);
    }
}
