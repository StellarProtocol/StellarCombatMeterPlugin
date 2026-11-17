using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

// The meter header's live muted status line (approved redesign §1):
//   in combat   → "● 1:42 in combat · 4 players"   (green dot; "● 1:42 in combat" when only you are listed)
//   archived    → "✓ Archived (102 s) · History 38" (green, ~3 s after a manual archive; wins over the rest)
//   paused      → "● Paused · 1:42"                (amber dot + amber text)
//   20-raid     → "20 players"                     (Party-focus on a raid; the occupied slot count)
//   idle        → nothing
// Polled every refresh (~10 Hz) but RE-FORMATTED only when what it shows changes — a packed int key of (kind, shown
// second, player count) plus the language — so it allocates once a second at most (Plugin-Performance-Rules §2).
public sealed partial class Plugin
{
    // Archived is NOT in the packed key: it shows the per-archive text formatted once in Plugin.HudEncounter.cs.
    private enum HudStatusKind { None = 0, Combat = 1, Paused = 2, Raid = 3, Archived = 4 }

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
        if (ArchivedFeedbackShown)
        {
            // Invalidate the key so the live status re-formats the moment the feedback ends.
            _hudStatusKind = HudStatusKind.Archived; _hudStatusKey = int.MinValue;
            return _archivedFbText;
        }
        var kind = CurrentHudStatusKind();
        int secs = kind is HudStatusKind.Combat or HudStatusKind.Paused ? HeaderElapsedSeconds() : 0;
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

    // The header's "m:ss in combat" is a WALL clock for the open encounter: it keeps ticking between hits
    // (user: "why does it freeze from the last hit?"). It deliberately does NOT reuse
    // EncounterElapsedSeconds() (last hit − start): that one stays the DPS-row divisor and the archive /
    // upload durations are separate again (ComputeDurationMs) — only this status line changes.
    // While paused it holds the value seen when the pause began. TogglePause lives in Plugin.Header.cs, so
    // the hold is latched lazily on the first paused poll (≤100 ms late at the ~10 Hz refresh) rather than
    // hooked there. On resume it jumps to the live value — the encounter (and the DPS window) kept running.
    private int _hudPausedSecs = -1;   // -1 = not latched

    private int HeaderElapsedSeconds()
    {
        // Drop the hold on resume AND when the encounter closes (archive/reset while paused), so the next
        // encounter never inherits a stale frozen value.
        if (!_paused || !_combatActive) _hudPausedSecs = -1;
        else if (_hudPausedSecs >= 0) return _hudPausedSecs;
        // ServerNowMs: the SAME clock domain the encounter stamps are compared against elsewhere
        // (TickAutoArchiveTriggers: now − _lastDamageMs; the manual-start path stamps _combatStartMs from it).
        long ms = HeaderElapsedMs(_combatActive, _combatStartMs, _lastDamageMs, _services.CombatSnapshot.ServerNowMs);
        int secs = (int)(ms / 1000);
        if (_paused && _combatActive) _hudPausedSecs = secs;
        return secs;
    }

    /// <summary>Pure: the header timer in ms = now − start while the encounter is open, never below the
    /// last-hit span (so it can't run backwards vs. the old value if ServerNowMs lags an event timestamp,
    /// or reads 0 before the server clock syncs). 0 when no encounter is open.</summary>
    internal static long HeaderElapsedMs(bool combatActive, long startMs, long lastDamageMs, long nowMs)
    {
        if (!combatActive || startMs == 0) return 0;
        long end = lastDamageMs > startMs ? lastDamageMs : startMs;
        if (nowMs > end) end = nowMs;
        return end - startMs;
    }

    // m:ss (minutes keep counting past an hour: "61:05"). Only called when the shown second changes.
    private static string Clock(int secs)
        => SmallNumber(secs / 60) + ":" + (secs % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    // The dot shows for the two "live" states and takes their colour; Paused also tints the text amber.
    private bool HudStatusHasDot() => _hudStatusKind is HudStatusKind.Combat or HudStatusKind.Paused;
    private ColorRgba HudStatusDotColor() => _hudStatusKind == HudStatusKind.Paused ? CautionColor() : SuccessColor();
    private ColorRgba? HudStatusColor() => _hudStatusKind switch
    {
        HudStatusKind.Paused   => CautionColor(),
        HudStatusKind.Archived => SuccessText(),
        _                      => MutedColor(),
    };
}
