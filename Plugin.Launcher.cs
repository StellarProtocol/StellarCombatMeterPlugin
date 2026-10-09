using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Launcher tiles (the Stellar plugins window). CombatMeter was the only tool plugin that registered NO
// launcher entry — its history + settings were reachable only through the meter overlay's header menu, so
// a player who hid the meter (Toir's feature request 2026-09-17) had to unhide it just to open history or
// link an account. Two tiles fix that WITHOUT the meter needing to be visible:
//   • a ⚙ "CombatMeter" tile   → the uploads/auto-archive Settings window (which now also carries
//                                 Account → Link to site, folded in from its old standalone window),
//   • a 🕘 "Combat History" tile → the history window.
// Both reuse the existing Toggle* handlers, so the windows keep their own World-phase ShouldRender gates.
// The tiles themselves are shown only in the World phase, matching the sibling plugins (Wardrobe /
// LoadoutSwitcher).
public sealed partial class Plugin
{
    private IDisposable? _settingsLauncherEntry;
    private IDisposable? _historyLauncherEntry;
    private byte[]? _historyIconPng;

    private void RegisterLauncher()
    {
        _historyIconPng = LoadHistoryIconPng();

        // Settings tile → the uploads/auto-archive Settings window (gear icon reused from the header).
        // Title stays the fixed literal "CombatMeter" — the stable pin-identity key (ILauncher.cs:49-50) —
        // so it needs no TitleProvider to stay a valid pin across a language change. Title also doubles as
        // the stable pinned-state identity. "CombatMeter" is the plugin brand and every catalog keeps it
        // identical (see settings.window.title / settings.appearance.title); launcher.settings.title
        // mirrors that for the DISPLAY text via TitleProvider so the tile re-localizes live like its
        // siblings, even though the value itself never changes per-locale today.
        _settingsLauncherEntry = _services.Launcher.Register(new LauncherEntry(
            "CombatMeter", IconPng: _settingsGearPng, IconKey: null, OnOpen: ToggleArchiveSettings)
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
