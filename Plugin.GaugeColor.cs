using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

// Gauge tinting — Appearance → Bars → "Bar colour: Role | Class" (2.10.0, owner 2026-09-09: "combat meter
// should have an option on appearance to be able to toggle to show role gauge color or class gauge color").
// Lives in its own partial so Plugin.cs (already over the 500-LoC guardrail) does not grow.
public sealed partial class Plugin
{
    // Colour of a player's GAUGE (main bar, DPS spine, history chart series, archived history rows, skill
    // breakdown bars): the class crest colour when the active layout's Appearance says Bar colour = Class AND
    // the class resolves, else the role colour — so an unknown class degrades to today's behaviour instead of
    // guessing. Appearance is per layout, so this reads the SAME toggle set the visible rows were built from
    // (ActiveToggles, Plugin.List.cs). The HP spine is NOT a DPS gauge and is unaffected: it stays HpColor().
    //
    // AssembleRow feeds this into MeterRowData.RoleColor (the main bar) and into MeterRowData.HpColor when the
    // spine is in DPS mode — both are DPS gauges, so both follow the setting. RoleColor keeps its framework
    // contract name; only what it carries changed.
    private ColorRgba GaugeColorFor(EntityId id)
        => ActiveToggles().BarColor == BarColorMode.Class && ClassPalette.TryGet(ResolveProfessionId(id), out var c)
            ? c : RoleColorFor(id);
}
