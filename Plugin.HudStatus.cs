using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

// The meter header's live muted status line (approved redesign §1):
//   in combat   → "● 1:42 in combat · 4 players"   (green dot; "● 1:42 in combat" when only you are listed)
//   paused      → "● Paused · 1:42"                (amber dot + amber text)
//   20-raid     → "20 players"                     (Party-focus on a raid; the occupied slot count)
//   idle        → nothing
// Polled every refresh (~10 Hz) but RE-FORMATTED only when what it shows changes — a packed int key of (kind, shown
// second, player count) plus the language — so it allocates once a second at most (Plugin-Performance-Rules §2).
public sealed partial class Plugin
{
    private enum HudStatusKind { None = 0, Combat = 1, Paused = 2, Raid = 3 }

    private int _hudStatusKey = int.MinValue;
    private string? _hudStatusLang;
    private string _hudStatusText = "";
    private HudStatusKind _hudStatusKind;

    private HudStatusKind CurrentHudStatusKind()
        => _paused ? HudStatusKind.Paused
         : _viewMode == ViewMode.PartyFocus && IsRaid20View ? HudStatusKind.Raid
         : _combatActive ? HudStatusKind.Combat
         : HudStatusKind.None;

    // Players the active view lists: the List rows (scope applied) or the occupied Party-focus slots.
    private int HudPlayerCount() => _viewMode == ViewMode.List ? _listRows.Count : _gridOccupied;

    private string HudStatusText()
    {
        var kind = CurrentHudStatusKind();
        int secs = kind is HudStatusKind.Combat or HudStatusKind.Paused ? (int)EncounterElapsedSeconds() : 0;
        int players = kind is HudStatusKind.Combat or HudStatusKind.Raid ? HudPlayerCount() : 0;
        int key = (int)kind | (players & 0xff) << 2 | secs << 10;
        string lang = _loc.Language;
        if (key == _hudStatusKey && ReferenceEquals(lang, _hudStatusLang)) return _hudStatusText;
        _hudStatusKey = key; _hudStatusLang = lang; _hudStatusKind = kind;
        _hudStatusText = kind switch
        {
            HudStatusKind.Paused => _loc.TFormat("hud.status.paused", Clock(secs)),
            HudStatusKind.Raid   => _loc.TFormat("hud.status.players", players),
            HudStatusKind.Combat => players > 1 ? _loc.TFormat("hud.status.combat", Clock(secs), players)
                                                : _loc.TFormat("hud.status.combatSolo", Clock(secs)),
            _                    => "",
        };
        return _hudStatusText;
    }

    // m:ss (minutes keep counting past an hour: "61:05"). Only called when the shown second changes.
    private static string Clock(int secs)
        => SmallNumber(secs / 60) + ":" + (secs % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    // The dot shows for the two "live" states and takes their colour; Paused also tints the text amber.
    private bool HudStatusHasDot() => _hudStatusKind is HudStatusKind.Combat or HudStatusKind.Paused;
    private ColorRgba HudStatusDotColor() => _hudStatusKind == HudStatusKind.Paused ? CautionColor() : SuccessColor();
    private ColorRgba? HudStatusColor() => _hudStatusKind == HudStatusKind.Paused ? CautionColor() : MutedColor();
}
