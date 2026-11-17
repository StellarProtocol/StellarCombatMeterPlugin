using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Launcher tile (the Stellar plugins window). CombatMeter was the only tool plugin that registered NO launcher entry — its
// history + settings were reachable only through the meter overlay's header menu, so a player who hid the meter (Toir's
// feature request 2026-09-17) had to unhide it just to open history or link an account. Since 2026-11-17 (approved hub
// redesign) there is ONE tile, "CombatMeter", which opens the CombatMeter hub (Plugin.Hub.cs: Meter show/hide, Combat
// History, Settings) — the separate "CombatMeter History" tile is gone (a pinned History tile disappears with it).
// The tile is shown only in the World phase, matching the sibling plugins (Wardrobe / LoadoutSwitcher).
public sealed partial class Plugin
{
    private IDisposable? _settingsLauncherEntry;

    // The launcher's tile glyph slot in canvas units (framework LauncherView.Tile.cs TileGlyph).
    private const float LauncherTileGlyphUnits = 32f;

    private void RegisterLauncher()
    {
        _hubWindow = RegisterHubWindow();   // Plugin.Hub.cs — after History / Settings, which its cards open

        // The tile → the CombatMeter hub, under the CombatMeter mark (the three-bar meter glyph, Plugin.MeterIcon.cs).
        // LauncherEntry.IconPng is ONE fixed array that the launcher draws mipmapped in its 32-unit tile glyph slot, so pick
        // the pre-drawn size for that slot at the UI scale of registration time (exact at the common scales; a later scale
        // change only costs mip-scaling, as any plugin icon does).
        // Title stays the fixed literal "CombatMeter" — the stable pin-identity key (ILauncher.cs:49-50) — so a pinned
        // tile survives a language change (and the merge into the hub). "CombatMeter" is the plugin brand and every
        // catalog keeps it identical (launcher.settings.title, TitleProvider's live-localized DISPLAY text).
        _settingsLauncherEntry = _services.Launcher.Register(new LauncherEntry(
            "CombatMeter", IconPng: MeterIconPng(LauncherTileGlyphUnits), IconKey: null,
            OnOpen: ToggleHub)   // centred on the screen — the launcher is not a CombatMeter window to centre on
        {
            ShouldShow = () => _services.ClientState.Phase == GamePhase.World,
            TitleProvider = () => _loc.T("launcher.settings.title"),
        });
    }

    private void DisposeLauncher()
    {
        try { _settingsLauncherEntry?.Dispose(); } catch { /* disposal must not throw */ }
        DisposeHub();
    }
}
