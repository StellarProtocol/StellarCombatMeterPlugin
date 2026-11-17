using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Launcher tiles (the Stellar plugins window). CombatMeter was the only tool plugin that registered NO
// launcher entry — its history + settings were reachable only through the meter overlay's header menu, so
// a player who hid the meter (Toir's feature request 2026-09-17) had to unhide it just to open history or
// link an account. Two tiles fix that WITHOUT the meter needing to be visible:
//   • a ⚙ "CombatMeter" tile   → the merged CombatMeter settings window (Plugin.SettingsWindow.cs), Archive tab,
//   • a 🕘 "Combat History" tile → the history window.
// Both reuse the existing Toggle* handlers, so the windows keep their own World-phase ShouldRender gates.
// The tiles themselves are shown only in the World phase, matching the sibling plugins (Wardrobe /
// LoadoutSwitcher).
public sealed partial class Plugin
{
    private IDisposable? _settingsLauncherEntry;
    private IDisposable? _historyLauncherEntry;
    private byte[]? _historyIconPng;

    // The launcher's tile glyph slot in canvas units (framework LauncherView.Tile.cs TileGlyph).
    private const float LauncherTileGlyphUnits = 32f;

    private void RegisterLauncher()
    {
        _historyIconPng = LoadHistoryIconPng();

        // Settings tile → the CombatMeter Settings window, under the CombatMeter mark (the three-bar meter glyph,
        // Plugin.MeterIcon.cs). LauncherEntry.IconPng is ONE fixed array that the launcher draws mipmapped in its 32-unit
        // tile glyph slot, so pick the pre-drawn size for that slot at the UI scale of registration time (exact at the
        // common scales; a later scale change only costs mip-scaling, as any plugin icon does).
        // Title stays the fixed literal "CombatMeter" — the stable pin-identity key (ILauncher.cs:49-50) —
        // so a pinned tile survives a language change. "CombatMeter" is the plugin brand and every catalog
        // keeps it identical (launcher.settings.title, TitleProvider's live-localized DISPLAY text, never changes
        // per-locale today); the window it opens is titled "CombatMeter Settings" (settings.window.title).
        _settingsLauncherEntry = _services.Launcher.Register(new LauncherEntry(
            "CombatMeter", IconPng: MeterIconPng(LauncherTileGlyphUnits), IconKey: null,
            // Opens the merged settings window on the Archive tab (what this tile opened before the merge), centred on
            // the screen — the launcher is not a CombatMeter window to centre on.
            OnOpen: () => ToggleSettings(SettingsTab.Archive))
        {
            ShouldShow = () => _services.ClientState.Phase == GamePhase.World,
            TitleProvider = () => _loc.T("launcher.settings.title"),
        });

        // History tile → the history window. Display title is the localized "Combat History"
        // (history.window.title); the stable identity stays language-independent for pin persistence.
        _historyLauncherEntry = _services.Launcher.Register(new LauncherEntry(
            "CombatMeter History", IconPng: _historyIconPng, IconKey: null, OnOpen: ToggleHistory)
        {
            ShouldShow = () => _services.ClientState.Phase == GamePhase.World,
            TitleProvider = () => _loc.T("history.window.title"),
        });
    }

    private void DisposeLauncher()
    {
        try { _settingsLauncherEntry?.Dispose(); } catch { /* disposal must not throw */ }
        try { _historyLauncherEntry?.Dispose(); } catch { /* disposal must not throw */ }
    }

    // Loads Resources/history.png (packed via the csproj EmbeddedResource entry) for the history launcher
    // tile. Mirrors LoadSettingsGearPng — never throws; a missing/corrupt resource degrades to null and
    // the launcher falls back to its generic plugins glyph.
    private static byte[]? LoadHistoryIconPng()
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.CombatMeter.history.png");
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
