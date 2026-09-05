// SP1: Assembles the full CombatLog DTO from the captured encounter state.
// Encounter metadata stubs are clearly marked TODO(SP1) where game API access is needed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.DeepSlumber;
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
    /// Raw combat events. Task 8: no longer embedded in the summary blob (which always ships
    /// <c>events: []</c>) — retained here only so callers keep a single call site. Since the rDPS
    /// spool (2026-09-05) every caller passes an EMPTY list: the stream is already chunked into the
    /// segment's <see cref="EventSpool"/> blobs and uploaded from there once this summary has landed.
    /// </param>
    /// <param name="signerKey">
    /// Base64-PKCS#8 private key, or null/empty to produce an empty placeholder signature
    /// (upload will be rejected by the server if <c>UPLOAD_PUBKEY</c> is set).
    /// </param>
    /// <param name="truncatedEvents">
    /// True when the raw dmg/skill forensic ring overflowed during the encounter. Forensic
    /// metadata only — every rendered number comes from the unsigned <c>derived</c> aggregates.
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
        int eventChunks = 0,
        InstallKey? installKey = null,
        bool truncatedBuffEvents = false,
        IReadOnlyList<BuffEffectAgg>? buffEffects = null)
    {
        var logId    = GenerateLogId();
        var nowMs    = _services.CombatSnapshot.ServerNowMs;

        // Multi-boss per battle (Task 6): entry.StageBosses is the ARCHIVE-TIME snapshot
        // (StageBossSet.MembersSnapshot(), taken in Plugin.History.cs BuildHistoryEntry) — read it, and
        // ONLY it, for this segment's boss set. NEVER the live _stageBosses: a manual re-upload of an
        // old entry (UploadHistoryEntry -> AssembleAndUpload) can run long after the set has drained or
        // moved on to a different stage/run, so a live read would silently mislabel the wrong fight's
        // bosses onto this one. Empty (bossless segment, or boss-phase detection off for this content)
        // falls back to entry.FallbackBossConfigId — the archive-time snapshot of the standalone boss-HP
        // heuristic (Plugin.Replay.cs's _bossMonsterInfo, which runs regardless of BossEnabled) —
        // restoring invariant 5 ("Boss phase = OFF -> bossId still recorded") after 957c12f dropped the
        // equivalent live _bossMonsterInfo?.Id ?? 0 argument without a replacement (fix, 2026-08-13). If
        // even that heuristic never resolved a boss, falls through to the pre-existing
        // (players-only-entry, effectively dead) cache resolver, exactly as the pre-multi-boss
        // per-segment scalar did.
        var (stageBossId, stageBossKilled, bosses) = BossRepresentative.ResolveStageBosses(
            entry.StageBosses, entry.FallbackBossConfigId);
        // ELITE CAPTURE channel (owner ruling 2026-08-13): plain map, entry-snapshot at archive time —
        // NEVER a live read (see EliteRepresentative's own doc).
        var elites = EliteRepresentative.ResolveElites(entry.Elites);
        var bossConfigId = stageBossId != 0 ? stageBossId : ResolveBossConfigId(entry);
        var encounter    = BuildEncounter(entry, bossConfigId, ResolveSceneDisplayName(entry.SceneName), stageBossKilled, bosses, elites);

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
        var derived = DerivedBuilder.Build(entry, truncatedEvents, truncatedBuffEvents, buffEffects);
        // Task 8: the summary blob always ships events: [] — the raw stream (if any) uploads
        // separately via sequential chunk POSTs once this summary lands (see ChunkUploader).
        var logUnsigned = new CombatLog(1, header, actors, Array.Empty<CombatLogEvent>(), derived);

        // --- Signature (dual-sign: shared key + per-install key over the SAME canonical) ---
        var sig = ComputeSig(logUnsigned, signerKey);
        var (pubKey, installSig) = ComputeInstallSig(logUnsigned, installKey);
        var uploaderSigned = new Uploader(localUid, sig, nonce, masterScore, pubKey, installSig);
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
    /// <param name="bosses">
    /// Every boss the plugin SAW this segment (multi-boss per battle, Task 6) — null for the replay-doc
    /// call site (which has no use for it) and for a bossless segment. Additive/null on old uploads.
    /// </param>
    /// <param name="elites">
    /// ELITE CAPTURE channel (owner ruling 2026-08-13): every MonsterType==1 entity the plugin SAW this
    /// segment — null for the replay-doc call site and for an eliteless segment. Additive/null on old
    /// uploads. CAPTURE ONLY — no scalar representative (unlike bossConfigId/bossKilled above).
    /// </param>
    internal static Encounter BuildEncounter(Plugin.EncounterHistoryEntry entry, int bossConfigId = 0,
                                            string? sceneDisplayName = null, bool bossKilled = false,
                                            IReadOnlyList<BossRec>? bosses = null,
                                            IReadOnlyList<EliteRec>? elites = null)
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
            DefeatedCount:   entry.Defeated,
            PartyId:         entry.PartyId,
            BossKilled:      bossKilled,
            Bosses:          bosses,
            Elites:          elites);
    }

    /// <summary>
    /// Resolves the boss's monster-table config id from the entry's captured entity set.
    /// Iterates non-player entities, calls <c>GetMonsterByEntity</c> for each, runs
    /// <see cref="BossPicker"/> to choose the highest-MaxHp boss, and returns its config id.
    /// Returns 0 when no boss entity is found or when the monster table is not yet loaded.
    /// NOTE: <c>entry.Entities</c> is a PLAYERS-ONLY snapshot (<c>EntitySnapshot.SnapshotEntities</c>),
    /// so in practice this always returns 0 today — kept as the last-resort fallback for parity with
    /// the pre-multi-boss behavior rather than removed as unrelated dead-code cleanup.
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

    /// <summary>Second signature over the SAME canonical payload as <see cref="ComputeSig"/>, using
    /// the per-install key. Returns (pubkey SPKI base64, install sig base64); ("","") when no install
    /// key. Kept separate from ComputeSig so the existing shared-key path is byte-for-byte unchanged.</summary>
    private static (string PubKey, string InstallSig) ComputeInstallSig(CombatLog log, InstallKey? installKey)
    {
        if (installKey == null) return ("", "");
        try
        {
            var payload = CanonicalPayload.Build(log);
            return (installKey.PubKeySpkiBase64, installKey.SignInstall(payload));
        }
        catch
        {
            // Never let per-install signing break an upload — the shared-key sig still stands.
            return ("", "");
        }
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
            actors[key] = SnapToActor(entityId, snap, localEntityIdValue, entry.Loadouts, entry.DeepSlumber);
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
        IReadOnlyList<CapturedLoadout> runLoadouts, DeepSlumberState? deepSlumber)
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
        var slumber = BuildDeepSlumber(isLocal, deepSlumber);
        // One coherent top-level source on class-switch segments (owner staging run sea/ZdTH3UwZQ6)
        // — see ResolveSelfEquipment.
        var equip = ResolveSelfEquipment(isLocal, professionId, runLoadouts,
            (gear, BuildGearDetail(snap), skills, snap.FightPoint));

        return new Actor(
            Name:         name ?? "Unknown",
            Kind:         "player",
            TeamId:       teamId,
            IsLocal:      isLocal,
            Uid:          uid,
            ProfessionId: professionId,
            Level:        level,
            AbilityScore: equip.AbilityScore,
            MaxHp:        snap.MaxHp,
            Attributes:   attrs,
            Gear:         equip.Gear,
            Skills:       equip.Skills,
            Fashion:      fashion,
            GearDetail:   equip.GearDetail,
            Modules:      modules,
            TalentStageId: talentStageId,
            Loadouts:     loadouts,
            TalentNodes:  talentNodes,
            AttrPeaks:    BuildActorAttrPeaks(snap),
            ClassSpans:   BuildActorClassSpans(snap),
            DeepSlumber:  slumber);
    }

    /// <summary>
    /// Self-only GATE for the per-class-loadout fields (Task 3): a non-local actor always gets
    /// null/null/0 regardless of <paramref name="runLoadouts"/> content — the accumulator
    /// (<c>LoadoutCapture</c>, Plugin.LoadoutCapture.cs) only ever captures the LOCAL player's own
    /// classes, so without this gate every teammate's Actor would wrongly carry the uploader's own
    /// loadout data (the flattened list has no per-teammate distinction). When local,
    /// <c>Loadouts</c> carries every class played so far this run; the top-level
    /// <c>Modules</c>/<c>TalentStageId</c> mirror the LATEST entry matching the actor's (final)
    /// <paramref name="professionId"/> — null/0 if that class was never captured (e.g. the player
    /// never triggered a profession-change poll this run, or captured before Task 2 shipped).
    ///
    /// "Latest" matters since the fought-with-setup fix (owner run B47O8jx6wp, Plugin.LoadoutCapture.cs
    /// LoadoutCapture.Capture): one professionId can now carry MULTIPLE entries in capture order (a
    /// fought-with setup preserved, then a later, different one), and the top-level mirror must
    /// describe the setup currently equipped — the LAST entry — never whichever one happened to be
    /// captured first.
    /// </summary>
    internal static (IReadOnlyList<LoadoutEntry>? Loadouts, IReadOnlyList<ModuleEntry>? Modules, int TalentStageId, IReadOnlyList<int>? TalentNodes)
        ResolveLoadoutFields(bool isLocal, int professionId, IReadOnlyList<CapturedLoadout> runLoadouts)
    {
        if (!isLocal || runLoadouts.Count == 0) return (null, null, 0, null);

        var loadouts = BuildLoadoutEntries(runLoadouts);
        for (var i = runLoadouts.Count - 1; i >= 0; i--)
        {
            var l = runLoadouts[i];
            if (l.ProfessionId == professionId) return (loadouts, BuildModuleEntries(l.Modules), l.TalentStageId, l.TalentNodes);
        }
        return (loadouts, null, 0, null);
    }

    /// <summary>
    /// Self-only equipment mirror for the TOP-LEVEL actor row (owner staging run
    /// <c>sea/ZdTH3UwZQ6</c> — the chimera setup): on a class-switch segment the sticky
    /// EntitySnapshot's gear/skills/abilityScore are frozen at SEGMENT START (the OLD class,
    /// Plugin.EntitySnapshotSticky.cs) while <c>professionId</c> parses from the archive-time
    /// attribute replacement (the NEW class, Plugin.AttrRange.cs) and Modules/Talents mirror the
    /// NEW class's latest captured entry — the worker then synthesizes a setup candidate from that
    /// MIXED row (mergeActors.ts) and a phantom "frost gear + tank talents" chip appears. When the
    /// final <paramref name="professionId"/> has a captured entry, mirror
    /// Gear/GearDetail/Skills/AbilityScore from that SAME latest entry the Modules/Talents mirror
    /// uses — ONE coherent source — so the synthesized candidate sameVariant-dedupes into the real
    /// setup. Non-local actors and a final class that was never captured pass the snapshot's own
    /// values through unchanged. GearDetail maps empty→null to keep the wire's null-when-empty
    /// convention (<see cref="BuildGearDetail"/>).
    /// </summary>
    internal static (IReadOnlyList<int[]> Gear, IReadOnlyList<GearDetail>? GearDetail, IReadOnlyList<int[]> Skills, long AbilityScore)
        ResolveSelfEquipment(
            bool isLocal, int professionId, IReadOnlyList<CapturedLoadout> runLoadouts,
            (IReadOnlyList<int[]> Gear, IReadOnlyList<GearDetail>? GearDetail, IReadOnlyList<int[]> Skills, long AbilityScore) fromSnapshot)
    {
        if (!isLocal) return fromSnapshot;
        for (var i = runLoadouts.Count - 1; i >= 0; i--)
        {
            var l = runLoadouts[i];
            if (l.ProfessionId != professionId) continue;
            return (l.Gear, l.GearDetail is { Count: > 0 } ? l.GearDetail : null, l.Skills, l.AbilityScore);
        }
        return fromSnapshot;
    }

    /// <summary>Self-only gate + 1:1 map of the archive-time Deep-Slumber snapshot onto the wire
    /// shape. Non-local actors always get null (the snapshot is the UPLOADER's own state); a null
    /// snapshot (container unresolved at archive) is omitted rather than sent empty.</summary>
    internal static DeepSlumberEntry? BuildDeepSlumber(bool isLocal, DeepSlumberState? state)
    {
        if (!isLocal || state is null) return null;
        var lines = new List<DeepSlumberLineEntry>(state.Lines.Count);
        foreach (var l in state.Lines)
        {
            var areas = new List<DeepSlumberAreaEntry>(l.Areas.Count);
            foreach (var a in l.Areas)
                areas.Add(new DeepSlumberAreaEntry(a.AreaId, a.IsActive, a.Score, a.BigNodes, a.MiddleNodes, a.NormalNodes));
            lines.Add(new DeepSlumberLineEntry(l.LineId, l.SubType, areas));
        }
        return new DeepSlumberEntry(state.SeasonLevels, lines);
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
                AttrPeaks:     l.AttrPeaks,
                AbilityScore:  l.AbilityScore,
                Imagines:      l.Imagines,
                Activations:   l.Activations,
                // Per-setup psychoscope (owner ruling, run sea/dXkw1PSyOG). The whole loadouts array is
                // already self-only gated by ResolveLoadoutFields, so isLocal is true by construction
                // here; reusing the SAME mapper as the actor-level block keeps the two shapes identical.
                DeepSlumber:   BuildDeepSlumber(isLocal: true, l.DeepSlumber)));
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
