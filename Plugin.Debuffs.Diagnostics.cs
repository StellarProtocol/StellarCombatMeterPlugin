using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.CombatMeter;

/// <summary>
/// Diagnostic-mode logging for the party-focus debuff strip (<see cref="Plugin"/> partial in
/// Plugin.Debuffs.cs). Gated on <see cref="StellarDiagnostics.IsEnabled"/> so the production path calls it
/// unconditionally (coding-standards § Diagnostics). Its job is to make imagine-lockout icon resolution
/// debuggable in the field: it prints, once per debuff base id, the LIVE wire source the debuff carries and
/// whether each resolution tier maps it to a Battle-Imagine card — the ground truth needed to extend
/// <c>LockoutArcaneSkill</c> (and inform a shared framework resolver fix) without another blind test cycle.
/// </summary>
public sealed partial class Plugin
{
    private readonly HashSet<int> _debuffSrcLogged = new();

    // One line per distinct debuff base id: the wire FightSourceInfo (SourceKind/SourceId), the skill id our
    // resolver derives from it, whether GetImagineForSkill maps that to an imagine, the static BuffTable.SkillId,
    // and the curated lockout-map target (if any). Grep "[debuff-src]" after a run with the lockout debuffs to
    // see why an icon did/didn't resolve and which skill id the game actually attributes the debuff to.
    private void DiagDebuffSource(in ActiveBuff b, BuffInfo? bi)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (!_debuffSrcLogged.Add(b.BaseId)) return;
        int wireSkill = b.SourceKind == 0 ? b.SourceId : 0;
        var imgWire = wireSkill > 0 ? _services.ResonanceData.GetImagineForSkill(wireSkill) : null;
        int lockout = LockoutArcaneSkill(b.BaseId) ?? 0;
        var imgLockout = lockout > 0 ? _services.ResonanceData.GetImagineForSkill(lockout) : null;
        _services.Log.Info(
            $"[CombatMeter][debuff-src] base={b.BaseId} '{bi?.Name}' srcKind={b.SourceKind} srcId={b.SourceId} " +
            $"wireSkill={wireSkill} tableSkill={bi?.SkillId ?? 0} imgWire={(imgWire is { } iw ? iw.SkillId : 0)} " +
            $"lockoutMap={lockout} imgLockout={(imgLockout is { } il ? il.SkillId : 0)} icon='{bi?.IconPath}'");
    }
}
