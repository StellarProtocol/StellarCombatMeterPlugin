// VENDORED from services/stellar-logs/dotnet/Stellar.LogFormat/ — DO NOT edit upstream here.
// Namespace adjusted to Stellar.CombatMeter.LogUpload for plugin-local use.

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
    string Kind, int Stacks, int Layer, int DurMs) : CombatLogEvent(Ms);
