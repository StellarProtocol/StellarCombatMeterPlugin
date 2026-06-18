using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>
/// Diagnostic-mode logging for <see cref="Plugin"/>. All entry points short-circuit
/// on <see cref="StellarDiagnostics.IsEnabled"/> so production partials can call
/// them unconditionally — keeps the production code clean of inline gates
/// (per coding-standards § Diagnostics; same pattern as
/// <c>FileConfigStore.Diagnostics.cs</c>).
/// </summary>
public sealed partial class Plugin
{
    // Logs (once per id, diagnostics-gated) a damage-attributed id that resolved to neither a real skill, a curated
    // override, nor a buff name — i.e. it renders as a raw "#id". Use the output to add an entry to
    // Plugin.SkillBreakdown's SkillNameOverrides map.
    private readonly HashSet<int> _loggedUnresolvedSkillNames = new();
    private void LogUnresolvedSkillName(int skillId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (!_loggedUnresolvedSkillNames.Add(skillId)) return;
        _services.Log.Info(
            $"[CombatMeter][name] unresolved id={skillId} (no skill, override, or buff name) — add to SkillNameOverrides if needed");
    }
    // TEMP cast-time-redesign capture: wire cd row vs what we render for a SELF imagine, on change + every
    // ~0.5s. Pins the multi-charge recharge model (does `begin` reset per cast? parallel vs sequential?) and
    // shows where our seconds/charges diverge from the game's own [Z]/[X]. Remove before the next commit.
    private int _imgDiagCount;
    private string _imgDiagSig = "";
    private long _imgDiagNow;
    private void LogSelfImagine(int baseSkill, in SkillCooldown cd, in ImagineSlot slot)
    {
        if (!StellarDiagnostics.IsEnabled || _imgDiagCount >= 300) return;
        long now = _services.CombatSnapshot.ServerNowMs;
        var sig = $"{baseSkill}:{cd.SkillId}:{cd.BeginTimeMs}:{cd.DurationMs}:{slot.ChargesAvailable}";
        bool changed = sig != _imgDiagSig;
        bool tick = now - _imgDiagNow >= 500;
        if (!changed && !tick) return;
        _imgDiagSig = sig; _imgDiagNow = now; _imgDiagCount++;
        long wireRem = cd.BeginTimeMs + cd.DurationMs > now ? cd.BeginTimeMs + cd.DurationMs - now : 0;
        _services.Log.Info(
            $"[CombatMeter][img] base={baseSkill} cdId={cd.SkillId} kind={cd.Kind} ch={cd.ChargeCount} " +
            $"begin={cd.BeginTimeMs} dur={cd.DurationMs} wireRem={wireRem} | render charges={slot.ChargesAvailable}/{slot.ChargeCount} " +
            $"secs={slot.RemainingSeconds} frac={slot.CooldownFraction:F2} now={now}");
    }

    // TEMP capture: every observed imagine "cast" (a DamageDealt that classifies as an imagine). Reveals
    // whether self casts are seen at all and how many damage hits one cast produces (multi-hit => the
    // damage stream over-counts casts; the cast-time tracker will need per-cast dedup).
    private int _castLogCount;
    private void LogImagineCast(EntityId src, int dmgSkillId, int baseSkillId)
    {
        if (!StellarDiagnostics.IsEnabled || _castLogCount >= 120) return;
        _castLogCount++;
        bool isSelf = src.Value == _services.CombatSnapshot.LocalEntityId.Value;
        _services.Log.Info($"[CombatMeter][img-cast] src={src.Value} self={isSelf} dmgSkill={dmgSkillId} base={baseSkillId} now={_services.CombatSnapshot.ServerNowMs}");
    }

    // TEMP capture: every SkillUsed event (the per-cast skill-phase signal, not per-hit). Shows whether an
    // imagine cast surfaces here, under which skill id + phase, and how it maps to the equipped imagine —
    // candidate clean cast signal for the cast-time charge tracker (self + others, same as ZDPS's AttrSkillId).
    private int _skillUsedLogCount;
    private void LogSkillUsed(CombatEvent.SkillUsed su)
    {
        if (!StellarDiagnostics.IsEnabled || _skillUsedLogCount >= 200) return;
        bool isSelf = su.CasterId.Value == _services.CombatSnapshot.LocalEntityId.Value;
        var img = _services.ResonanceData.GetImagineForSkill(su.SkillId);
        // Only log self casts + anything that maps to an imagine (keeps the flood down while still catching
        // imagine casts by other players).
        if (!isSelf && img is null) return;
        _skillUsedLogCount++;
        _services.Log.Info($"[CombatMeter][skill-used] caster={su.CasterId.Value} self={isSelf} skill={su.SkillId} phase={su.Phase} -> imagine={(img is { } i ? i.SkillId : 0)} now={su.TimestampMs}");
    }
}
