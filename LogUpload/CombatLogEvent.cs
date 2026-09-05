// VENDORED from services/stellar-logs/dotnet/Stellar.LogFormat/ — DO NOT edit upstream here.
// Namespace adjusted to Stellar.CombatMeter.LogUpload for plugin-local use.

using System.Collections.Generic;

namespace Stellar.CombatMeter.LogUpload;

internal abstract record CombatLogEvent(long Ms);

internal sealed record SkillEvent(long Ms, string Src, int Skill, int Phase) : CombatLogEvent(Ms);

internal sealed record DamageEvent(
    long Ms, string Src, string Tgt, int Skill,
    long Amt, long Act, long Shield,
    bool Crit, bool Lucky, bool Heal, bool Dead,
    int Elem, int Kind, int Source) : CombatLogEvent(Ms);

internal sealed record BuffEvent(
    long Ms, string Tgt, int Uuid, int Base,
    string Kind, int Stacks, int Layer, int DurMs,
    string Src, int SrcKind, int SrcId,
    string? SrcOwner = null) : CombatLogEvent(Ms);   // owner(src) when src is a known player summon (spec § 6.8)

/// <summary>A `sheet` track row (spec § 6.1): the local player's damage-relevant attributes as ABSOLUTE
/// values — <see cref="Attrs"/> holds [attrId, value] pairs. <see cref="Keyframe"/> marks the one row per
/// segment that carries every tracked attr the live sheet has; every other row is a change-only delta.</summary>
internal sealed record SheetEvent(long Ms, bool Keyframe, IReadOnlyList<long[]> Attrs) : CombatLogEvent(Ms);
