// VENDORED from services/stellar-logs/dotnet/Stellar.LogFormat/ — DO NOT edit upstream here.
// Namespace adjusted to Stellar.CombatMeter.LogUpload for plugin-local use.

using System.Collections.Generic;

namespace Stellar.CombatMeter.LogUpload;

internal sealed record CombatLog(
    int V,
    LogHeader Header,
    IReadOnlyDictionary<string, Actor> Actors,
    IReadOnlyList<CombatLogEvent> Events,
    Derived? Derived = null);

// Plugin-authoritative aggregates (uncapped totals/skills/series/deaths). Unsigned; optional.
internal sealed record Derived(
    long CombatDurationMs, bool TruncatedEvents,
    IReadOnlyDictionary<string, ActorAgg> PerActor,
    IReadOnlyDictionary<string, IReadOnlyList<SkillAgg>> PerActorSkills,
    IReadOnlyDictionary<string, IReadOnlyList<SkillAgg>> PerActorHealSkills,
    IReadOnlyDictionary<string, IReadOnlyList<TakenAgg>> PerActorTakenSkills,
    IReadOnlyList<DeathRec> Deaths,
    SeriesBlock Series,
    // Imagine casts with TRUE timestamps (all players) — the raw event ring truncates on
    // long fights, so the web timeline builds its bubbles from this instead. Optional.
    IReadOnlyList<ImagineCastRec>? ImagineCasts = null,
    // PER-TARGET-BUCKET statistics (Spec B, docs/superpowers/specs/2026-08-14-per-boss-statistics-design.md
    // §4.2). Outer key = uid string (same keys as PerActor above); inner key = the boss/elite monster
    // config id as a string, or the literal "other" for damage not attributed to a tracked target.
    // ALL SIX ARE NULL when the plugin captured no buckets (old plugin / bossless segment) — absent,
    // never empty, so a stored derived block from before Spec B reads byte-identically (§7.5). Boss and
    // elite live in SEPARATE maps and are never merged (owner ruling 2026-08-13: elites never reach boss
    // surfaces). Σ buckets per (uid, channel) == the PerActor total above (§7.1, pinned by
    // DerivedBucketsTests). Taken is totals-only by design (§3.2); healing is never bucketed (§2).
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, BucketDealt>>? PerActorBossDealt = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, BucketTaken>>? PerActorBossTaken = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, BucketSeries>>? PerActorBossSeries = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, BucketDealt>>? PerActorEliteDealt = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, BucketTaken>>? PerActorEliteTaken = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, BucketSeries>>? PerActorEliteSeries = null);

/// <summary>Damage one player dealt to one target bucket: total + the per-skill breakdown in the
/// whole-fight <see cref="SkillAgg"/> shape (luckys/critLuckys/top/min are 0 — the bucket store keeps
/// only total/hits/crits, spec §3.2).</summary>
internal sealed record BucketDealt(long Total, IReadOnlyList<SkillAgg> Skills);

/// <summary>Damage one player took FROM one target bucket. Totals-only by design (spec §3.2 — the
/// whole-fight taken-skill drill stays whole-fight).</summary>
internal sealed record BucketTaken(long Total);

/// <summary>One bucket's dealt/taken series, normalized to the derived block's single
/// <see cref="SeriesBlock.BucketMs"/> and anchored at combat start — like-for-like with the
/// whole-fight <see cref="ActorSeries"/> so the site can swap chart sources.</summary>
internal sealed record BucketSeries(IReadOnlyList<long> Dealt, IReadOnlyList<long> Taken);

/// <summary>One battle-imagine cast: epoch ms, caster entity-id string, base imagine skill id.</summary>
internal sealed record ImagineCastRec(long Ms, string Src, int Skill);

internal sealed record ActorAgg(
    long Damage, long Healing, long DamageTaken,
    int Hits, int Crits, int Luckys, int Deaths,
    long TopHit, long FirstHitMs, long LastHitMs,
    // ZDPS-parity splits (additive; absent on old uploads).
    int CritLuckys = 0,
    long CritDamage = 0, long LuckyDamage = 0, long CritLuckyDamage = 0, long ShieldBreak = 0,
    int HealHits = 0, int HealCrits = 0, int HealLuckys = 0, int HealCritLuckys = 0,
    long CritHealing = 0, long LuckyHealing = 0, long CritLuckyHealing = 0,
    long TopHeal = 0, long EffectiveHealing = 0);

// Luckys/CritLuckys/Top/Min are additive v1 extensions — absent on old uploads, so consumers
// treat them as optional. Heal rows reuse the shape (Total = healing; CritLuckys/Min unused = 0).
internal sealed record SkillAgg(int SkillId, long Total, int Hits, int Crits,
    int Luckys = 0, int CritLuckys = 0, long Top = 0, long Min = 0);
internal sealed record TakenAgg(int SkillId, long Total, int Hits, long Top = 0);
internal sealed record DeathRec(long Ms, string Victim, int Skill);
internal sealed record SeriesBlock(int BucketMs, IReadOnlyDictionary<string, ActorSeries> PerActor);
internal sealed record ActorSeries(IReadOnlyList<long> Dealt, IReadOnlyList<long> Healing, IReadOnlyList<long> Taken);

internal sealed record LogHeader(
    string LogId, long CapturedAtMs, string GameVersion, string Region,
    string? FrameworkVer, string? PluginVer, string Privacy,
    Encounter Encounter, Uploader Uploader,
    // Task 8: planned chunk count for the auto path's chunked-upload follow-up
    // (POST .../run/{region}/{levelUuid}/events per chunk). 0 on the manual path (no chunks; events: []
    // is the whole story). Always emitted (even 0) so the server knows whether to await chunks.
    int EventChunks = 0);

internal sealed record Encounter(
    string Kind, long LevelUuid, string? DungeonGuid, int MapId, int LineId,
    string? Name, int BossId, string? BossName, string? Difficulty, int MasterModeScore,
    string Result, long StartMs, long EndMs, long DurationMs, int PassTime,
    // Raw DungeonSceneInfo.difficulty (dungeon challenge level, e.g. "Master 6"'s 6).
    // Semantic UNCONFIRMED (1-20 level vs. tier enum) — additive, 0/omitted when unknown.
    int DifficultyLevel = 0,
    // Achieved "Total Score" (DungeonScore.total_score) — the numerator in the "686/700"
    // pairing with MasterModeScore (the max/par). Additive, 0/omitted when not a scored run.
    int TotalScore = 0,
    // Server epoch ms when the in-game dungeon run-timer started (IDungeonState.RunTimerStartMs).
    // Additive — 0/omitted when unknown. NOT covered by the upload signature (CanonicalPayload).
    long DungeonStartMs = 0,
    // IDungeonState.LastDefeatedCount snapshotted at archive. Additive — 0/omitted when unknown
    // (also 0 until the attr feeding it is wired on the framework side).
    int DefeatedCount = 0,
    // Party id (GrpcTeam team_id) frozen at run-start (B1). Emitted as a STRING; additive —
    // 0/omitted when solo/unknown.
    long PartyId = 0,
    // Per-segment boss-kill flag (raid per-stage clear derivation). Additive — false/omitted when the
    // segment's tracked boss was not observed killed. NOT part of CanonicalPayload (signature-neutral,
    // like bossId/partyId). The worker aggregates the killed set across a run's segments vs. the raid
    // roster to derive the CLEAR verdict server-side.
    bool BossKilled = false,
    // Every boss the plugin SAW this segment (multi-boss per battle). Additive/null on old uploads.
    // The scalar BossId/BossKilled above stay as the FIRST-ADMITTED-member representative for old
    // readers (Task 6; amendment 4, 2026-08-12 review — no plugin-side raid-roster preference).
    IReadOnlyList<BossRec>? Bosses = null,
    // ELITE CAPTURE channel (owner ruling 2026-08-13): every MonsterType==1 entity the plugin SAW this
    // segment. Additive/null on old uploads or a segment with no elite captured. CAPTURE ONLY — unlike
    // Bosses there is NO scalar EliteId/EliteKilled representative (nothing consumes one); NOT part of
    // CanonicalPayload (signature-neutral, like Bosses/BossId/PartyId).
    IReadOnlyList<EliteRec>? Elites = null);

/// <summary>One boss engaged in a segment (multi-boss per battle, Spec A). configId = monster-table
/// config id (e.g. 102800 Sunfire, 102801 Moonstrike); killed = observed dead / scripted-killed.
/// Additive — omitted on old uploads; NOT part of CanonicalPayload (signature-neutral).</summary>
internal sealed record BossRec(int ConfigId, bool Killed);

/// <summary>One elite (MonsterType==1) engaged in a segment (ELITE CAPTURE channel, owner ruling
/// 2026-08-13). Same shape as <see cref="BossRec"/> — configId = monster-table config id; killed =
/// observed dead (plain Hp&lt;=0; no raid scripted-kill inference for elites). Additive — omitted on old
/// uploads; NOT part of CanonicalPayload (signature-neutral).</summary>
internal sealed record EliteRec(int ConfigId, bool Killed);

// MasterScore: the uploader's CURRENT account master-mode score (SocialIdentity.MasterScore),
// attached on every upload so the StellarLogs char page reflects a fresh dungeon clear promptly
// (the server folds it into the char identity, decoupled from the throttled portraits feed).
// Additive — 0/omitted when unknown. NOT covered by the upload signature (CanonicalPayload uses
// only LocalUid + Nonce), so adding it never affects sig verification.
// PubKey/InstallSig: the per-install identity (SPKI base64) + a SECOND signature over the SAME
// canonical payload as Sig, so the server can learn which install genuinely plays each uid and
// harden character claims (docs/superpowers/specs/2026-08-07-claim-key-hardening-design.md).
// Additive/defaulted — omitted when no install key; NOT part of the canonical, so it never affects
// the existing Sig verification (same safety as MasterScore above).
internal sealed record Uploader(long LocalUid, string Sig, string Nonce, int MasterScore = 0, string PubKey = "", string InstallSig = "");

internal sealed record Actor(
    string Name, string Kind, long TeamId, bool IsLocal, long? Uid,
    int ProfessionId, int Level, long AbilityScore, long MaxHp,
    IReadOnlyList<long[]> Attributes,    // [attrId, value]
    IReadOnlyList<int[]> Gear,           // [slot, itemId]
    IReadOnlyList<int[]> Skills,         // [skillId, level, tier]
    IReadOnlyList<Fashion> Fashion,
    // Per-piece ACTUAL rolls — SELF ONLY (other players broadcast slot+itemId; their rolls
    // are per-instance and never on the wire). Null/empty for everyone but the uploader.
    IReadOnlyList<GearDetail>? GearDetail = null,
    // Per-class-loadout plan (Task 3), all self-only — the accumulator only ever captures the
    // LOCAL player's own classes, so every non-local actor leaves these at their defaults.
    // Modules/TalentStageId/TalentNodes mirror whichever captured class matches THIS actor's
    // (final) ProfessionId above; Loadouts carries every class played so far this run.
    IReadOnlyList<ModuleEntry>? Modules = null,
    int TalentStageId = 0,
    IReadOnlyList<LoadoutEntry>? Loadouts = null,
    IReadOnlyList<int>? TalentNodes = null,   // actual allocated talent-tree node ids (self-only)
    IReadOnlyList<long[]>? AttrPeaks = null,  // [attrId, peakValue] sparse self combat peaks (base+peak stats 2026-08-02)
    // Per-entity class detection (2026-08-03): this actor's professionId timeline this run, as
    // [professionId, startMs, endMs] triples — populated for EVERY player actor (self AND party,
    // NOT self-only like Modules/Loadouts/TalentNodes above), because the tracker samples from the
    // broadcast attr-220 stream which is available for any AOI entity. Null/omitted when the actor
    // played a single class all run (no timeline needed).
    IReadOnlyList<long[]>? ClassSpans = null,
    // Deep-Slumber Psychoscope snapshot at archive (Phase 3, 2026-08-19) — SELF ONLY, read live at
    // archive time. Null for every non-local actor and when the container had not resolved.
    DeepSlumberEntry? DeepSlumber = null);

/// <summary>Self-only per-item instance detail, mirroring the game's Item Detail popup.
/// Rolls are RESOLVED at capture (attr id + display value + 0-100 percentile) so consumers
/// never need the equip attr-lib tables. Kind: 0 basic, 1 advanced, 2 recast, 3 rare, 4 gem effect.
/// EnchantId is the RESOLVED gem ITEM id (name carries the display level); EnchantLevel is the raw
/// wire index kept only as a fallback.</summary>
internal sealed record GearDetail(
    int Slot, int Quality, int RefineLevel,
    int PerfectionValue, int PerfectionMax,
    int EnchantId, int EnchantLevel,
    IReadOnlyList<int[]> Rolls,          // [kind, attrId, value, percentile]; kind 4 = gem effect
    int ItemLevel = 0,                   // wire perfection_level (semantics uncertain; kept raw)
    int BreakThrough = 0);               // breakthrough stage — display Lv = EquipBreakThroughTable stage EquipGs

internal sealed record Fashion(int Slot, int FashionId, IReadOnlyList<float> Dyes); // RGBA flattened

/// <summary>One equipped module on the wire: slot, the module's config id + quality, and its
/// rolled parts ([attrId, value]). Self-only (see <see cref="Actor.Modules"/>/<see cref="LoadoutEntry.Modules"/>).
/// Mirrors the plugin-internal capture shape <c>CapturedModule</c> (<c>Plugin.LoadoutCapture.cs</c>) —
/// the assembler maps one onto the other 1:1.</summary>
internal sealed record ModuleEntry(int Slot, int ConfigId, int Quality, IReadOnlyList<int[]> Parts);

/// <summary>One played class's full loadout on the wire — gear (ids + self-only rolled detail),
/// modules, skills, fashion, its saved-loadout project name, and active talent stage. Self-only;
/// one entry per distinct class the uploader played THIS run (latest-wins per class while playing
/// it — see <c>LoadoutCapture</c>). Mirrors the plugin-internal capture shape <c>CapturedLoadout</c>.</summary>
internal sealed record LoadoutEntry(
    int ProfessionId,
    string? ProjectName,
    IReadOnlyList<int[]> Gear,               // [slot, itemId]
    IReadOnlyList<GearDetail>? GearDetail,    // null when the capture had none (mirrors Actor.GearDetail)
    IReadOnlyList<int[]> Skills,              // [skillId, level, tier]
    IReadOnlyList<Fashion> Fashion,
    IReadOnlyList<ModuleEntry>? Modules,
    int TalentStageId,
    IReadOnlyList<int>? TalentNodes = null,   // actual allocated talent-tree node ids (self-only)
    IReadOnlyList<long[]>? Attributes = null, // [attrId, value] attribute sheet for THIS class (self-only)
    IReadOnlyList<long[]>? AttrPeaks = null,  // [attrId, peakValue] sparse per-class combat peaks (self-only)
    long AbilityScore = 0,                     // this class's combat power (FightPoint), read while active; 0 when unread
    // Equipped Battle Imagine ids, slot-ordered [X, Z] (self-only). Additive/null on old uploads or
    // when unsynced. Owner gap, run B47O8jx6wp retest (2026-08-22): a swap alone mints a new
    // fought-with setup — see the plugin-internal CapturedLoadout.Imagines / LoadoutCapture.SameSetup.
    IReadOnlyList<int>? Imagines = null,
    // Per-setup ACTIVATION TIMELINE (owner-approved feature, 2026-08-23): ServerNowMs stamps for
    // each moment this setup BECAME the equipped identity (the SWAP moment, never first-fought) —
    // SAME timebase as the top-level classSpans, which the site intersects these against. Additive:
    // absent on old plugins = no-timeline. See CapturedLoadout.Activations / LoadoutCapture.Capture.
    IReadOnlyList<long>? Activations = null);

/// <summary>One Deep-Slumber area on the wire — activation, score, and node allocations as
/// [nodeId, value] pairs (big = socketed fantasy card id, mid = socketed item id, nodes = level).
/// Mirrors <c>DeepSlumberArea</c> (Abstractions) 1:1.</summary>
internal sealed record DeepSlumberAreaEntry(
    int AreaId, bool Active, long Score,
    IReadOnlyList<int[]> Big, IReadOnlyList<int[]> Mid, IReadOnlyList<int[]> Nodes);

/// <summary>One Deep-Slumber cultivate line on the wire. Mirrors <c>DeepSlumberLine</c>.</summary>
internal sealed record DeepSlumberLineEntry(int LineId, int SubType, IReadOnlyList<DeepSlumberAreaEntry> Areas);

/// <summary>The uploader's live Deep-Slumber Psychoscope state — self-only (see
/// <see cref="Actor.DeepSlumber"/>). SeasonLevels = [seasonId, level] pairs.</summary>
internal sealed record DeepSlumberEntry(
    IReadOnlyList<int[]> SeasonLevels, IReadOnlyList<DeepSlumberLineEntry> Lines);
