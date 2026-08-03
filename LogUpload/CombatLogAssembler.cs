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
    /// <param name="events">
    /// Raw combat events flushed from <see cref="CombatEventBuffer"/>. Task 8: no longer embedded
    /// in the summary blob (which always ships <c>events: []</c>) — retained here only so callers
    /// keep a single call site; the caller uploads the same list separately via
    /// <see cref="EventChunker"/> + chunk uploads once this summary has landed.
    /// </param>
    /// <param name="signerKey">
    /// Base64-PKCS#8 private key, or null/empty to produce an empty placeholder signature
    /// (upload will be rejected by the server if <c>UPLOAD_PUBKEY</c> is set).
    /// </param>
    /// <param name="truncatedEvents">
    /// True when the raw dmg/skill forensic ring overflowed during the encounter. Forensic
    /// metadata only — every rendered number comes from the unsigned <c>derived</c> aggregates.
    /// </param>
    /// <param name="snapshotBossConfigId">
    /// Monster-table config id of the boss, captured at fight time before entity caches were
    /// wiped (provided by Plugin.Replay via <c>_bossMonsterInfo?.Id</c>).
    /// When non-zero this value is used directly as <c>encounter.bossId</c>, bypassing the
    /// dead-cache <c>ResolveBossConfigId</c> fallback. Pass 0 when no snapshot is available
    /// (e.g. bossless runs or manual deferred upload of pre-fix entries).
    /// </param>
    /// <param name="eventChunks">
    /// Number of chunks <see cref="EventChunker"/> planned for the raw event stream (Task 8).
    /// Written to <c>header.eventChunks</c>; the summary blob itself always ships
    /// <c>events: []</c> — chunk uploads carry the raw stream separately, sequentially, after
    /// this summary has landed. 0 on the manual path (no chunking).
    /// </param>
    internal CombatLog Assemble(
        Plugin.EncounterHistoryEntry entry,
        IReadOnlyList<CombatLogEvent> events,
        string? signerKey,
        bool truncatedEvents,
        int snapshotBossConfigId = 0,
        int eventChunks = 0)
    {
        var logId    = GenerateLogId();
        var nowMs    = _services.CombatSnapshot.ServerNowMs;

        // Use the capture-time snapshot when available; fall back to live resolution for
        // deferred manual uploads of old entries (caches may still be warm if still in-map).
        var bossConfigId = snapshotBossConfigId != 0 ? snapshotBossConfigId : ResolveBossConfigId(entry);
        var encounter    = BuildEncounter(entry, bossConfigId, ResolveSceneDisplayName(entry.SceneName));

        // --- Uploader ---
        var localEntityId = _services.CombatSnapshot.LocalEntityId;
        var localUid = localEntityId.Value;
        var nonce    = GenerateNonce();
        // Attach the uploader's CURRENT account master score (from the social snapshot the ID card /
        // portraits feed populates for self) so the char page updates promptly after a dungeon clear.
        // 0 when no snapshot yet — the server's >0 guard then leaves the last-known value untouched.
        var masterScore = _services.EntityDetail.GetSocialSnapshot(localEntityId)?.Identity.MasterScore ?? 0;

        // Build a temporary uploader with empty sig, then compute the real sig over the assembled log.
        var uploaderUnsigned = new Uploader(localUid, "", nonce, masterScore);

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
            GameVersion:  _services.GameEnvironment.GameVersion,
            Region:       _services.GameEnvironment.RegionCode,
            FrameworkVer: frameworkVer,
            PluginVer:    pluginVer,
            Privacy:      "unlisted",              // default; TODO(SP1): expose per-user privacy pref in settings
            Encounter:    encounter,
            Uploader:     uploaderUnsigned,
            EventChunks:  eventChunks);

        // Plugin-authoritative aggregates (uncapped) ride alongside the (capped) raw event detail track.
        var derived = DerivedBuilder.Build(entry, truncatedEvents);
        // Task 8: the summary blob always ships events: [] — the raw stream (if any) uploads
        // separately via sequential chunk POSTs once this summary lands (see ChunkUploader).
        var logUnsigned = new CombatLog(1, header, actors, Array.Empty<CombatLogEvent>(), derived);

        // --- Signature ---
        var sig = ComputeSig(logUnsigned, signerKey);
        var uploaderSigned = new Uploader(localUid, sig, nonce, masterScore);
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
    /// <summary>The game's own display name for an archived run's scene, or null when the scene table has
    /// no row (the server and site then fall back to their own mapId lookup). Mirrors the resolution the
    /// History window uses; this is the call the assembler's long-standing TODO asked for.</summary>
    private string? ResolveSceneDisplayName(string? sceneToken)
    {
        if (!int.TryParse(sceneToken ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return null;
        var name = _services.GameData.World.GetScene(id)?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <param name="bossConfigId">
    /// Monster-table config id of the identified boss entity, or 0 when no boss was found.
    /// Resolved from <c>IGameDataWorld.GetMonsterByEntity</c> at assemble time via
    /// <see cref="ResolveBossConfigId"/>.
    /// </param>
    /// <param name="sceneDisplayName">
    /// The game's own scene name for this run, resolved by the caller from
    /// <c>IGameDataWorld.GetScene(mapId)?.Name</c> — the same lookup the History window uses
    /// (<c>Plugin.HistoryWindow.cs</c>'s <c>ResolveSceneName</c>). Optional so the replay-doc call site,
    /// which has no use for it, is unchanged. Blank is normalised to null so the server and site fall
    /// back to their own mapId lookup rather than storing an empty string.
    /// </param>
    internal static Encounter BuildEncounter(Plugin.EncounterHistoryEntry entry, int bossConfigId = 0,
                                            string? sceneDisplayName = null)
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
            // Resolved by the caller from the game's own scene table (TODO closed 2026-07-29 — the
            // History window had been doing this lookup all along while uploads sent null).
            Name:            string.IsNullOrWhiteSpace(sceneDisplayName) ? null : sceneDisplayName,
            BossId:          bossConfigId,
            BossName:        null,
            Difficulty:      null,
            MasterModeScore: entry.MasterModeScore,
            TotalScore:      entry.TotalScore,
            Result:          entry.Result,
            StartMs:         entry.EnteredAtMs,
            EndMs:           entry.ArchivedAtMs,
            DurationMs:      entry.ArchivedAtMs - entry.EnteredAtMs,
            PassTime:        entry.PassTime,
            DifficultyLevel: entry.DifficultyLevel,
            DungeonStartMs:  entry.DungeonStartMs,
            DefeatedCount:   entry.Defeated);
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
            actors[key] = SnapToActor(entityId, snap, localEntityIdValue, entry.Loadouts);
        }
        return actors;
    }

    /// <summary>Snapshot sparse peak arrays → [attrId, peakValue][] for upload; null when empty.</summary>
    internal static IReadOnlyList<long[]>? BuildActorAttrPeaks(EntitySnapshot snap)
    {
        if (snap.AttrPeakIds.Length == 0) return null;
        var peaks = new long[snap.AttrPeakIds.Length][];
        for (var i = 0; i < snap.AttrPeakIds.Length; i++)
            peaks[i] = new long[] { snap.AttrPeakIds[i], snap.AttrPeakValues[i] };
        return peaks;
    }

    /// <summary>Snapshot's parallel ClassSpan* arrays (baked in by <c>Plugin.ApplyClassSpans</c> at
    /// archive) → [professionId,startMs,endMs][] for upload; null when empty (single-class actor — no
    /// timeline needed). Populated for EVERY player actor, self AND party alike.</summary>
    internal static IReadOnlyList<long[]>? BuildActorClassSpans(EntitySnapshot snap)
    {
        if (snap.ClassSpanProf.Length == 0) return null;
        var spans = new long[snap.ClassSpanProf.Length][];
        for (var i = 0; i < snap.ClassSpanProf.Length; i++)
            spans[i] = new long[] { snap.ClassSpanProf[i], snap.ClassSpanStart[i], snap.ClassSpanEnd[i] };
        return spans;
    }

    private Actor SnapToActor(EntityId entityId, EntitySnapshot snap, long localEntityIdValue,
        IReadOnlyList<CapturedLoadout> runLoadouts)
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

        var (loadouts, modules, talentStageId, talentNodes) = ResolveLoadoutFields(isLocal, professionId, runLoadouts);

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
            Fashion:      fashion,
            GearDetail:   BuildGearDetail(snap),
            Modules:      modules,
            TalentStageId: talentStageId,
            Loadouts:     loadouts,
            TalentNodes:  talentNodes,
            AttrPeaks:    BuildActorAttrPeaks(snap),
            ClassSpans:   BuildActorClassSpans(snap));
    }

    /// <summary>
    /// Self-only GATE for the per-class-loadout fields (Task 3): a non-local actor always gets
    /// null/null/0 regardless of <paramref name="runLoadouts"/> content — the accumulator
    /// (<c>LoadoutCapture</c>, Plugin.LoadoutCapture.cs) only ever captures the LOCAL player's own
    /// classes, so without this gate every teammate's Actor would wrongly carry the uploader's own
    /// loadout data (the flattened list has no per-teammate distinction). When local,
    /// <c>Loadouts</c> carries every class played so far this run; the top-level
    /// <c>Modules</c>/<c>TalentStageId</c> mirror whichever captured class matches the actor's
    /// (final) <paramref name="professionId"/> — null/0 if that class was never captured (e.g. the
    /// player never triggered a profession-change poll this run, or captured before Task 2 shipped).
    /// </summary>
    internal static (IReadOnlyList<LoadoutEntry>? Loadouts, IReadOnlyList<ModuleEntry>? Modules, int TalentStageId, IReadOnlyList<int>? TalentNodes)
        ResolveLoadoutFields(bool isLocal, int professionId, IReadOnlyList<CapturedLoadout> runLoadouts)
    {
        if (!isLocal || runLoadouts.Count == 0) return (null, null, 0, null);

        var loadouts = BuildLoadoutEntries(runLoadouts);
        foreach (var l in runLoadouts)
            if (l.ProfessionId == professionId) return (loadouts, BuildModuleEntries(l.Modules), l.TalentStageId, l.TalentNodes);
        return (loadouts, null, 0, null);
    }

    // Count == 0 ? null helper — mirrors BuildGearDetail's own null-when-empty convention.
    internal static IReadOnlyList<LoadoutEntry>? BuildLoadoutEntries(IReadOnlyList<CapturedLoadout> loadouts)
    {
        if (loadouts.Count == 0) return null;
        var list = new List<LoadoutEntry>(loadouts.Count);
        foreach (var l in loadouts)
            list.Add(new LoadoutEntry(
                ProfessionId:  l.ProfessionId,
                ProjectName:   l.ProjectName,
                Gear:          l.Gear,
                GearDetail:    l.GearDetail.Count == 0 ? null : l.GearDetail,
                Skills:        l.Skills,
                Fashion:       l.Fashion,
                Modules:       BuildModuleEntries(l.Modules),
                TalentStageId: l.TalentStageId,
                TalentNodes:   l.TalentNodes,
                Attributes:    l.Attributes,
                AttrPeaks:     l.AttrPeaks));
        return list;
    }

    internal static IReadOnlyList<ModuleEntry>? BuildModuleEntries(IReadOnlyList<CapturedModule> modules)
    {
        if (modules.Count == 0) return null;
        var list = new List<ModuleEntry>(modules.Count);
        foreach (var m in modules) list.Add(new ModuleEntry(m.Slot, m.ConfigId, m.Quality, m.Parts));
        return list;
    }

    // Self-only per-piece instance detail (captured into the snapshot from IInventory.GetSelfGear;
    // arrays are empty for everyone else). Null when absent so the writer omits the key entirely.
    private static IReadOnlyList<GearDetail>? BuildGearDetail(EntitySnapshot snap)
    {
        var n = snap.GdSlots.Length;
        if (n == 0) return null;
        var list = new List<GearDetail>(n);
        var off = 0;
        for (var i = 0; i < n; i++)
        {
            var count = i < snap.GdRollCounts.Length ? snap.GdRollCounts[i] : 0;
            var rolls = new int[count][];
            for (var r = 0; r < count && off + 3 < snap.GdRolls.Length; r++, off += 4)
                rolls[r] = new[] { snap.GdRolls[off], snap.GdRolls[off + 1], snap.GdRolls[off + 2], snap.GdRolls[off + 3] };
            list.Add(new GearDetail(
                snap.GdSlots[i], snap.GdQuality[i], snap.GdRefine[i],
                snap.GdPerfVal[i], snap.GdPerfMax[i],
                snap.GdEnchantId[i], snap.GdEnchantLv[i], rolls,
                i < snap.GdItemLv.Length ? snap.GdItemLv[i] : 0,
                i < snap.GdBt.Length ? snap.GdBt[i] : 0));
        }
        return list;
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
