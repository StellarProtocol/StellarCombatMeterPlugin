using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// The header's round Pause and Archive buttons (approved redesign addendum 2026-11-17, "Pause + Archive header icons";
// _scratch\prototypes\combatmeter-redesign-APPROVED.md). They replace the ⋯ popover's old ENCOUNTER panel, so the two
// encounter actions are one click from the meter and never duplicated.
//   Pause    running → "‖" on the neutral faint HUD fill (hover lifts it)
//            paused  → ▶ on the Caution amber fill (SDL R8: stops only my part); the status line goes amber
//                      "● Paused · 1:42" (Plugin.HudStatus.cs)
//   Archive  idle    → tray-with-arrow, RECESSED (SDL R22) while nothing has been recorded — a click does nothing
//            ready   → tray-with-arrow on the neutral fill
//            clicked → flashes green ✓ for ~1 s where you clicked (R13); the status line shows green
//                      "✓ Archived (102 s) · History 38" for ~3 s; the success toast stays
// Performance (Plugin-Performance-Rules §7): built once; every Func is a bool check + struct colour maths, the glyphs are
// cached byte[]s (the Live image only re-decodes when the reference flips), and the feedback text is formatted once per
// archive — a state flip only repaints.
public sealed partial class Plugin
{
    private const double ArchiveFlashS      = 1.0;   // the button's green ✓
    private const double ArchivedFeedbackS  = 3.0;   // the status line's "✓ Archived …"

    private double _archiveFlashUntil = double.NegativeInfinity;
    private string _archivedFbText = "";
    private double _archivedFbUntil = double.NegativeInfinity;

    private bool ArchiveFlashing => _services.Framework.TimeNow < _archiveFlashUntil;
    private bool ArchivedFeedbackShown => _services.Framework.TimeNow < _archivedFbUntil;

    // "Nothing recorded" = no live stat rows — exactly ManualArchive's skip-empty gate for a manual press. O(1).
    private bool CanArchive => _stats.Count > 0;

    private HudElement BuildPauseButton() => HudStateIconButton(
        () => _paused ? _resumePng : _pausePng,
        static (p, hover) => p._paused ? (hover ? p.HoverFill(p.CautionFill()) : p.CautionFill())
                           : hover ? p.HudHoverFill() : p.HudRestFill(),
        static (p, hover) => p._paused ? p.CautionText() : hover ? p.TextColor() : p.MutedColor(),
        TogglePause);

    private HudElement BuildArchiveButton() => HudStateIconButton(
        () => ArchiveFlashing ? _checkmarkPng : _archivePng,
        static (p, hover) => p.ArchiveFlashing ? p.ArchiveFlashFill()
                           : !p.CanArchive ? DisabledFill(p.PanelColor())
                           : hover ? p.HudHoverFill() : p.HudRestFill(),
        static (p, hover) => p.ArchiveFlashing ? p.Readable(p.SuccessColor(), p.ArchiveFlashFill())
                           : !p.CanArchive ? p.DisabledLabel(p.PanelColor())
                           : hover ? p.TextColor() : p.MutedColor(),
        ArchiveFromHeader);

    // The same green wash the Settings rows flash with (Plugin.SettingsEffects.cs FlashWashAlpha).
    private ColorRgba ArchiveFlashFill() => Over(SuccessColor() with { A = FlashWashAlpha }, PanelColor());

    // A recessed button is not pressable: ignore the click rather than log a "manual-press" that can only skip-empty.
    private void ArchiveFromHeader()
    {
        if (CanArchive) ManualArchiveFromMenu();
    }

    // Header feedback for a banked MANUAL archive (Plugin.Header.cs NotifyManualArchived): formatted once per archive.
    private void NoteArchivedFeedback(long durationMs)
    {
        double now = _services.Framework.TimeNow;
        _archivedFbText = _loc.TFormat("hud.more.archived", durationMs / 1000, _history.Count);
        _archivedFbUntil = now + ArchivedFeedbackS;
        _archiveFlashUntil = now + ArchiveFlashS;
    }
}
