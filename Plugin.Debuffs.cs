using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Party-focus per-member debuff strip (owner request 2026-09-24; spec
// docs/superpowers/specs/2026-09-24-party-debuff-icons-design.md). Reads the member's live buffs from the
// combat wire, filters/orders via the pure DebuffStrip builder, resolves icons through IGameAssets, and
// fills the row's 2×2 debuff slots. Display only — touches no capture/archive/upload path.
public sealed partial class Plugin
{
    // User selection loaded from prefs at construction (Plugin.cs); mutated + re-saved by the debuff
    // config panel (Plugin.DebuffConfig.cs). Empty set + ShowAllExceptSelected (its default) shows every
    // named, icon'd debuff out of the box.
    private DebuffSelection _debuffSelection = null!;

    // Every debuff base id observed on a party member's live buffs this session, for the config panel's
    // "seen debuffs" catalog — keyed by BaseId so a debuff appears once regardless of how many members/
    // stacks carried it. Recorded for EVERY debuff on the member, not just the ones the 4-cell strip kept,
    // so the panel can toggle a debuff the strip is currently hiding (overflow or filtered out).
    private readonly Dictionary<int, (string name, bool hasIcon)> _seenDebuffs = new();

    // Hoisted once so ResolveDebuffs (~200 calls/sec at raid-20) doesn't allocate a new delegate from the
    // method group on every call.
    private Func<int, BuffInfo?>? _getBuffFn;

    private void ResolveDebuffs(EntityId id, bool show, ref MeterRowData row)
    {
        row.ShowDebuffs = show;
        if (!show) { row.DebuffOverflow = 0; return; }
        var buffs = _services.CombatLookup.BuffsFor(id);
        _getBuffFn ??= _services.GameData.Combat.GetBuff;
        NoteSeenDebuffs(buffs);
        var r = DebuffStrip.Build(buffs, _getBuffFn, _debuffSelection, _services.CombatSnapshot.ServerNowMs);
        row.Debuff0 = ToSlot(r, 0);
        row.Debuff1 = ToSlot(r, 1);
        row.Debuff2 = ToSlot(r, 2);
        row.Debuff3 = ToSlot(r, 3);
        row.DebuffOverflow = r.Overflow;
    }

    // Upserts every real debuff (IsDebuff:true) on this member into the seen catalog. A plain dictionary
    // write per entry — cheap even at the ~200/sec raid-20 refresh rate the caller runs at.
    private void NoteSeenDebuffs(IReadOnlyList<ActiveBuff> buffs)
    {
        if (buffs == null) return;
        for (int i = 0; i < buffs.Count; i++)
        {
            if (_getBuffFn!(buffs[i].BaseId) is not { IsDebuff: true } bi) continue;
            _seenDebuffs[buffs[i].BaseId] = (bi.Name ?? "", !string.IsNullOrEmpty(bi.IconPath));
        }
    }

    private DebuffSlot ToSlot(in DebuffStripResult r, int i)
    {
        if (i >= r.Count) return DebuffSlot.None;
        var e = r.At(i);
        UvRect uv;
        // Imagine-lockout debuffs (e.g. Time Stasis) show the SOURCE Battle-Imagine card instead of the raw
        // debuff icon — same as the CooldownBar plugin (DebuffAttribution): BuffTable.SkillId -> is that skill
        // a Battle Imagine? If so, load the imagine art. Otherwise the debuff's own icon.
        int skillId = _getBuffFn?.Invoke(e.BaseId)?.SkillId ?? 0;
        object? icon = skillId > 0 && _services.ResonanceData.GetImagineForSkill(skillId) is { } img
            ? _services.GameAssets.LoadImagineIcon(img.SkillId, out uv)              // imagine-lockout -> imagine card
            : _services.GameAssets.LoadBuffIcon(e.BaseId, out uv);                   // normal debuff icon
        return new DebuffSlot(icon, uv, e.Stacks, e.RemainFraction, Present: true);
    }
}
