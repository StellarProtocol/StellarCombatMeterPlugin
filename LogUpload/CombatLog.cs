// VENDORED from services/stellar-logs/dotnet/Stellar.LogFormat/ — DO NOT edit upstream here.
// Namespace adjusted to Stellar.CombatMeter.LogUpload for plugin-local use.
// UNVERIFIED — this code has never been executed in-game.

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
    SeriesBlock Series);

internal sealed record ActorAgg(
    long Damage, long Healing, long DamageTaken,
    int Hits, int Crits, int Luckys, int Deaths,
    long TopHit, long FirstHitMs, long LastHitMs);

internal sealed record SkillAgg(int SkillId, long Total, int Hits, int Crits);
internal sealed record TakenAgg(int SkillId, long Total, int Hits);
internal sealed record DeathRec(long Ms, string Victim, int Skill);
internal sealed record SeriesBlock(int BucketMs, IReadOnlyDictionary<string, ActorSeries> PerActor);
internal sealed record ActorSeries(IReadOnlyList<long> Dealt, IReadOnlyList<long> Healing, IReadOnlyList<long> Taken);

internal sealed record LogHeader(
    string LogId, long CapturedAtMs, string GameVersion, string Region,
    string? FrameworkVer, string? PluginVer, string Privacy,
    Encounter Encounter, Uploader Uploader);

internal sealed record Encounter(
    string Kind, long LevelUuid, string? DungeonGuid, int MapId, int LineId,
    string? Name, int BossId, string? BossName, string? Difficulty, int MasterModeScore,
    string Result, long StartMs, long EndMs, long DurationMs, int PassTime);

internal sealed record Uploader(long LocalUid, string Sig, string Nonce);

internal sealed record Actor(
    string Name, string Kind, long TeamId, bool IsLocal, long? Uid,
    int ProfessionId, int Level, long AbilityScore, long MaxHp,
    IReadOnlyList<long[]> Attributes,    // [attrId, value]
    IReadOnlyList<int[]> Gear,           // [slot, itemId]
    IReadOnlyList<int[]> Skills,         // [skillId, level, tier]
    IReadOnlyList<Fashion> Fashion);

internal sealed record Fashion(int Slot, int FashionId, IReadOnlyList<float> Dyes); // RGBA flattened
