using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Party-focus per-member debuff strip (owner request 2026-09-24; spec
// docs/superpowers/specs/2026-09-24-party-debuff-icons-design.md). Reads the member's live buffs from the
// combat wire, filters/orders via the pure DebuffStrip builder, resolves icons through IGameAssets, and
// fills the row's 2×2 debuff slots. Display only — touches no capture/archive/upload path.
public sealed partial class Plugin
{
    // Known non-display debuff base-ids to hide even though they are BuffType 0 with an icon. Empty until
    // measured in-game (the "tune it in-game" knob from the spec).
    private static readonly HashSet<int> DebuffDenylist = new();

    private void ResolveDebuffs(EntityId id, bool show, ref MeterRowData row)
    {
        row.ShowDebuffs = show;
        if (!show) { row.DebuffOverflow = 0; return; }
        var buffs = _services.CombatLookup.BuffsFor(id);
        var r = DebuffStrip.Build(buffs, _services.GameData.Combat.GetBuff, DebuffDenylist, _services.CombatSnapshot.ServerNowMs);
        row.Debuff0 = ToSlot(r, 0);
        row.Debuff1 = ToSlot(r, 1);
        row.Debuff2 = ToSlot(r, 2);
        row.Debuff3 = ToSlot(r, 3);
        row.DebuffOverflow = r.Overflow;
    }

    private DebuffSlot ToSlot(in DebuffStripResult r, int i)
    {
        if (i >= r.Count) return DebuffSlot.None;
        var e = r.At(i);
        object? icon = _services.GameAssets.LoadBuffIcon(e.BaseId, out var uv);   // async; null until loaded (framework caches the handle)
        return new DebuffSlot(icon, uv, e.Stacks, e.RemainFraction, Present: true);
    }
}
