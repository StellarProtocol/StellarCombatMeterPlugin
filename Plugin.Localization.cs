using System;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// i18n P1 live-switch. The overlay redraws every frame and the vast majority of text is drawn through
// `() => _loc.T(...)` Func labels, which re-poll each frame — so a language change is instant for them
// with no work here. This partial handles only the exceptions that bake their text ONCE:
//   • window TITLES (WindowSpec.Title is a plain string set at Register; there is no SetTitle),
//   • entity context-menu items (IEntityContextMenu.Register takes a string label, captured once),
//   • the cached chart series ("Team total" is built only when the chart is dirty).
// Colour-registry labels are deliberately NOT re-registered (that could reset user colour overrides);
// they resolve once at registration in the active language (Plugin.cs RegisterColours).
public sealed partial class Plugin
{
    private void OnLanguageChanged()
    {
        // Re-register every window that carries a localized title (main window's title is the "CombatMeter"
        // brand, and the row-menu title is empty — neither needs it). Rect + visibility are preserved.
        RebuildWindow(ref _historyWindow, RegisterHistoryWindow);
        RebuildWindow(ref _skillBreakdownWindow, RegisterSkillBreakdownWindow);
        RebuildWindow(ref _snapshotWindow, RegisterSnapshotWindow);
        RebuildWindow(ref _settingsWindow, BuildAndRegisterSettings);
        RebuildWindow(ref _accountWindow, BuildAndRegisterAccount);
        RebuildWindow(ref _archiveSettingsWindow, BuildAndRegisterArchiveSettings);

        // Context-menu items baked their labels at Register-time; re-register under the new language.
        ReRegisterTeamContextMenuItems();

        // The chart series caches "Team total" until dirtied; bump the version so it rebuilds localized.
        _chartSourcesVersion++;
    }

    // Framework-sanctioned Remove()+Register() rebuild that preserves the window's rect + visibility
    // (mirrors RebuildHistoryWindow). Used to re-localize baked window titles on a language change.
    private static void RebuildWindow(ref IWindowControl window, Func<IWindowControl> register)
    {
        var rect = window.Rect;
        var wasShown = window.IsShown;
        window.Remove();
        window = register();
        if (rect.Width > 0f) window.SetRect(rect);
        window.SetVisible(wasShown);
    }
}
