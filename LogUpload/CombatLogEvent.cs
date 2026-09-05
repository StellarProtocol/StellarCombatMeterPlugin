// Originally VENDORED from services/stellar-logs/dotnet/Stellar.LogFormat/ (namespace adjusted to
// Stellar.CombatMeter.LogUpload for plugin-local use). This copy has DIVERGED from upstream since the rDPS
// P1/P2 work: BuffEvent gained Src/SrcKind/SrcId/SrcOwner and SheetEvent is new here. Treat this file as the
// plugin's own wire shape, not a mirror — an upstream sync (either direction) is still PENDING, so a change
// made here does NOT reach Stellar.LogFormat and a stale upstream must not be copied back over it.

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
/// segment that carries every tracked attr the live sheet has; every other row is a change-only delta.
/// <para>TIME BASE: within a chunk the keyframe is first by ARRAY order, but <see cref="CombatLogEvent.Ms"/>
/// is NOT guaranteed monotonic across it — a tick-written keyframe is stamped with <c>UtcNow</c> on the update
/// thread while delta rows carry the earlier network-thread receive stamp. The values stay consistent because
/// the attribute sink is written at packet receive and the keyframe reads that live sink, so a consumer that
/// sorts by <c>ms</c> (the worker's <c>sheetSteps</c>) sees the same step function as array order. The chunk
/// ref's window is min/max over the batch accordingly (SpoolTrack.SealOpen).</para></summary>
internal sealed record SheetEvent(long Ms, bool Keyframe, IReadOnlyList<long[]> Attrs) : CombatLogEvent(Ms);
