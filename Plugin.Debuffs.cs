using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Party-focus per-member status-effect strip (owner request 2026-09-24). Reads the member's live buffs from the
// combat wire, filters/orders via the pure DebuffStrip builder, resolves icons through IGameAssets, and fills
// the row's 2×2 block — BOTH buffs (green tile) and debuffs (red tile), color-coded like the CooldownBar plugin.
// Display only — touches no capture/archive/upload path.
public sealed partial class Plugin
{
    // Per-tab user selections, loaded from prefs at construction (Plugin.cs); mutated + re-saved by the config
    // panel (Plugin.DebuffConfig.cs). The Debuffs tab and Buffs tab each own an independent selection — its own
    // mode + checked set + show-hidden (owner 2026-09-24). Fresh install: debuffs ShowOnlySelected + the 3
    // lockouts; buffs ShowOnlySelected + empty (the user opts buffs in).
    private DebuffSelection _debuffSelection = null!;
    private DebuffSelection _buffSelection = null!;

    // Hoisted once so ResolveDebuffs (~200 calls/sec at raid-20) doesn't allocate a new delegate from the
    // method group on every call.
    private Func<int, BuffInfo?>? _getBuffFn;

    private void ResolveDebuffs(EntityId id, bool show, int maxCells, ref MeterRowData row)
    {
        row.ShowDebuffs = show;
        row.DebuffColumns = maxCells / 2;   // 4->2, 6->3, 8->4 columns (2 rows)
        if (!show) { row.DebuffOverflow = 0; return; }
        var buffs = _services.CombatLookup.BuffsFor(id);
        _getBuffFn ??= _services.GameData.Combat.GetBuff;
        DiagEffects(buffs);
        var r = DebuffStrip.Build(buffs, _getBuffFn, _debuffSelection, _buffSelection, _services.CombatSnapshot.ServerNowMs, maxCells);
        row.Debuff0 = ToSlot(r, 0);
        row.Debuff1 = ToSlot(r, 1);
        row.Debuff2 = ToSlot(r, 2);
        row.Debuff3 = ToSlot(r, 3);
        row.Debuff4 = ToSlot(r, 4);
        row.Debuff5 = ToSlot(r, 5);
        row.Debuff6 = ToSlot(r, 6);
        row.Debuff7 = ToSlot(r, 7);
        row.Debuff8 = ToSlot(r, 8);
        row.Debuff9 = ToSlot(r, 9);
        row.Debuff10 = ToSlot(r, 10);
        row.Debuff11 = ToSlot(r, 11);
        row.DebuffOverflow = r.Overflow;
    }

    // Diagnostics-only pass over the member's live effects (the config panel's pick-list is sourced from the full
    // BuffTable via IGameDataCombat.AllBuffs(), so no live "seen" catalog is needed). DiagDebuffSource self-gates
    // on StellarDiagnostics and dedupes per base id, so this is a no-op in production.
    private void DiagEffects(IReadOnlyList<ActiveBuff> buffs)
    {
        if (!Stellar.Abstractions.Diagnostics.StellarDiagnostics.IsEnabled || buffs == null) return;
        for (int i = 0; i < buffs.Count; i++)
            if (_getBuffFn!(buffs[i].BaseId) is { } bi) DiagDebuffSource(buffs[i], bi);
    }

    private DebuffSlot ToSlot(in DebuffStripResult r, int i)
    {
        if (i >= r.Count) return DebuffSlot.None;
        var e = r.At(i);
        UvRect uv;
        // Imagine-sourced effects (e.g. Time Stasis, Tina's Blessing) show the SOURCE Battle-Imagine card instead
        // of the raw icon — same intent as CooldownBar. IsBuff tints the tile green (buff) vs red (debuff).
        object? icon = ResolveDebuffIcon(e.BaseId, e.SourceSkillId, out uv);
        return new DebuffSlot(icon, uv, e.Stacks, e.RemainFraction, Present: true, IsBuff: e.IsBuff);
    }

    // Icon for an effect: the source Battle-Imagine card for an imagine-sourced one (via the live source skill, then
    // the curated LockoutArcaneSkill map), else the debuff's own icon. Shared by the 2×2 cells (ToSlot) and the
    // click tooltip (Plugin.DebuffTooltip) so both render the same art.
    internal object? ResolveDebuffIcon(int baseId, int sourceSkillId, out UvRect uv)
    {
        int skillId = sourceSkillId != 0 ? sourceSkillId : (_getBuffFn?.Invoke(baseId)?.SkillId ?? 0);
        var img = skillId > 0 ? _services.ResonanceData.GetImagineForSkill(skillId) : null;
        // Curated fallback for known player-arcane lockouts (Mechanical Failure / Element Stasis): their
        // BuffTable carries SkillId 0 and the generic abnormal icon, naming the arcane only in LOCALIZED Desc
        // text, and the wire source resolves to a slot-[0] summon variant GetImagineForSkill can't map — so
        // point at the base [7,8] arcane the debuff locks out (owner 2026-09-24; extend from [debuff-src] diag).
        if (img is null && LockoutArcaneSkill(baseId) is { } lk)
            img = _services.ResonanceData.GetImagineForSkill(lk);
        return img is { } m
            ? _services.GameAssets.LoadImagineIcon(m.SkillId, out uv)              // imagine-lockout -> imagine card
            : _services.GameAssets.LoadBuffIcon(baseId, out uv);                   // normal debuff icon
    }

    // Curated imagine-lockout map: debuff base id -> the base Battle-Imagine skill it locks out. These debuffs
    // have BuffTable.SkillId 0 + the generic buff_abnormal icon and only name the arcane in localized Desc text,
    // so there is NO locale-independent static link — this is the sanctioned curated bridge (mirrors the
    // framework's ImagineAoyiRule.MapCompanionArcane). Mapped ids are slot-[7,8] arcanes that GetImagineForSkill
    // resolves to a card. Add new season arcanes here as the [debuff-src] diagnostic surfaces them.
    private static int? LockoutArcaneSkill(int buffBaseId) => buffBaseId switch
    {
        2110049 => 3971,   // Mechanical Failure -> "Arcane! Superconductor Surge"
        2110050 => 3957,   // Element Stasis     -> "Arcane! Fatal Spiral"
        _ => null,
    };
}
