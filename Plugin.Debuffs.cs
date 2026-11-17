using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Party-focus per-member status-effect strip (owner request 2026-09-24). Reads the member's live buffs from the
// combat wire, filters/orders via the pure DebuffStrip builder, resolves icons through IGameAssets, and fills
// the row's 2×2 block — BOTH buffs (green tile) and debuffs (red tile), color-coded like the CooldownBar plugin.
// Icons come from the shared effect icon chain (Plugin.EffectIcons.cs).
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
        var r = DebuffStrip.Build(buffs, _getBuffFn, _debuffSelection, _buffSelection, _services.CombatSnapshot.ServerNowMs, maxCells,
                                  IsUnnamedFn);   // "hidden" = game name empty/placeholder, as Settings › Effects decides it
        float cell = row.DebuffCellSize > 0f ? row.DebuffCellSize : 20f;   // set by the caller first (0 = the 20 px default)
        row.Debuff0 = ToSlot(r, 0, cell);
        row.Debuff1 = ToSlot(r, 1, cell);
        row.Debuff2 = ToSlot(r, 2, cell);
        row.Debuff3 = ToSlot(r, 3, cell);
        row.Debuff4 = ToSlot(r, 4, cell);
        row.Debuff5 = ToSlot(r, 5, cell);
        row.Debuff6 = ToSlot(r, 6, cell);
        row.Debuff7 = ToSlot(r, 7, cell);
        row.Debuff8 = ToSlot(r, 8, cell);
        row.Debuff9 = ToSlot(r, 9, cell);
        row.Debuff10 = ToSlot(r, 10, cell);
        row.Debuff11 = ToSlot(r, 11, cell);
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

    private DebuffSlot ToSlot(in DebuffStripResult r, int i, float cellUnits)
    {
        if (i >= r.Count) return DebuffSlot.None;
        var e = r.At(i);
        // The shared effect icon chain (Plugin.EffectIcons.cs): imagine card → source skill icon → own icon → category
        // placeholder, sized for this row's cell body (cell − the 1 px frame each side). IsBuff tints the tile green / red.
        object? icon = ResolveEffectIcon(e.BaseId, e.SourceSkillId, e.IsBuff, cellUnits - 2f, out var uv);
        // Time-left snapped to 1 px of foot bar (DisplayQuantize.Remain): these fills were the #1 changed graphic per
        // frame in the 2026-11-10 raid log — a continuously shrinking fraction re-meshed every tile on every refresh.
        return new DebuffSlot(icon, uv, e.Stacks, DisplayQuantize.Remain(e.RemainFraction), Present: true, IsBuff: e.IsBuff);
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
