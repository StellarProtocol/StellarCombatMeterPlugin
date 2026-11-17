using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Modal confirms (framework 2.33.0 ConfirmHostElement, approved addendum "Modal confirm dialog (framework layer)",
// _scratch\prototypes\combatmeter-redesign-APPROVED.md) + the History "Loading" overlay (LayerStackElement).
//
// ONE ConfirmController per window (History, Settings); each window's whole content is wrapped once in a
// ConfirmHostElement, so the scrim locks everything below the title bar while the window's ✕ and drag stay live
// (✕ / Esc = Cancel). They replace the old inline confirm panels (Clear all box, delete row, Effects footer row) — those
// were extra layout variants that had to keep the window height; the modal card needs none.
//
// Severity ladder: Danger = data loss (Clear all, Delete), Caution = resets my own choices (Untick all), Primary = a
// normal action (Upload all). "Don't ask again" only on Upload all — never on a delete.
//
// The request texts are composed on the click (a cold path) — never per refresh.
public sealed partial class Plugin
{
    // Both persist "don't ask again" in the plugin's own "combatmeter" section (key confirm.dontAsk.<key>).
    // Lazy: _prefs is assigned in the constructor; the windows (and so these) are built after it.
    private ConfirmController? _historyConfirmCtl, _settingsConfirmCtl;
    private ConfirmController HistoryConfirm => _historyConfirmCtl ??= new ConfirmController { DontAskStore = _prefs };
    private ConfirmController SettingsConfirm => _settingsConfirmCtl ??= new ConfirmController { DontAskStore = _prefs };

    // Fixed confirm-pill label widths (shared by every request on a host, so the pill never changes size): the widest
    // label in ANY locale, bold 13 (EstimateBoldLabelW): History = fil "Burahin lahat" ("Delete all") ≈ 110, the rest
    // ("Hapus semua", "Upload", "アップロード") fit; Settings = "Untick all" only → the same width its footer pill has used
    // since the redesign (UntickLabelW, sized for id "Hapus centang"; Thai's combining marks make the estimate overshoot).
    private const float HistoryConfirmLabelW  = 110f;
    private const float SettingsConfirmLabelW = UntickLabelW;

    // ── History ───────────────────────────────────────────────────────────────────────────────────────────────────
    private void AskClearAll()
    {
        HistoryConfirm.Show(new ConfirmRequest(
            _loc.TFormat(PluralKey(_historyRunCount, "history.clearAll.askOne", "history.clearAll.ask"), _historyRunCount),
            _loc.T("history.clearAll.message"),
            ClearAllHistory)
        {
            Subtitle = _loc.T("history.window.title"),
            Emphasis = _loc.T("history.delete.consequence"),
            ConfirmLabel = _loc.T("history.clearAll.confirmLabel"),
            CancelLabel = _loc.T("cfg.cancel"),
            Severity = ConfirmSeverity.Danger,
        });
    }

    // Deletes ONE archive (the selected segment). A multi-segment run says "segment"; a single-archive run IS the run.
    // The target is captured now: a selection change while the card is open can't happen (the scrim blocks the list), but
    // the archive is re-checked by reference on confirm anyway (an auto-archive may have shifted indices meanwhile).
    private void AskDeleteSelected()
    {
        if (_selectedSession is not { } target) return;
        bool segment = _selectedSegments.Length > 1;
        string length = target.EnteredAtMs > 0 && target.ArchivedAtMs > 0
            ? FormatDurationWithTenths(RealDurationMs(target.EnteredAtMs, target.ArchivedAtMs)) : "—";
        string subtitle, message;
        if (segment)
        {
            int pos = Array.IndexOf(_selectedSegments, _historyIndex) + 1;
            int others = _selectedSegments.Length - 1;
            subtitle = _loc.TFormat("history.segment.numbered", pos) + " · " + SegmentLabel(target.Trigger) + " · " + length;
            message = others == 1 ? _loc.T("history.delete.segmentMsgOne") : _loc.TFormat("history.delete.segmentMsg", others);
        }
        else
        {
            subtitle = _runTitle + " · " + length;
            message = _loc.T("history.delete.runMsg");
        }
        HistoryConfirm.Show(new ConfirmRequest(
            _loc.T(segment ? "history.delete.askSegment" : "history.delete.askRun"), message,
            () => { int i = _history.IndexOf(target); if (i >= 0) DeleteSession(i); })
        {
            Subtitle = subtitle,
            Emphasis = _loc.T("history.delete.consequence"),
            ConfirmLabel = _loc.T("history.delete.confirm"),
            CancelLabel = _loc.T("cfg.cancel"),
            Severity = ConfirmSeverity.Danger,
        });
    }

    // "Upload all (N)" → Primary confirm with "Don't ask again" (approved CORRECTION 2026-11-17). Single-segment upload
    // stays one click. Ticked once, Show() runs QueueRunUpload at once without opening.
    private void AskUploadAll()
    {
        HistoryConfirm.Show(new ConfirmRequest(
            _loc.TFormat(PluralKey(_selectedSegments.Length, "history.uploadAll.askOne", "history.uploadAll.ask"), _selectedSegments.Length),
            _loc.T("history.uploadAll.message"),
            QueueRunUpload)
        {
            Subtitle = _runTitle,
            ConfirmLabel = _loc.T("history.uploadAll.confirm"),
            CancelLabel = _loc.T("cfg.cancel"),
            Severity = ConfirmSeverity.Primary,
            DontAskAgainKey = "history.uploadAll",
        });
    }

    // ── Settings › Effects ────────────────────────────────────────────────────────────────────────────────────────
    private void AskUntickAll()
    {
        bool buffs = _dcTab == 1;
        string kind = buffs ? "buffs" : "debuffs";
        int ticked = DcTickedCount();
        string askKey = buffs ? PluralKey(ticked, "cfg.effects.confirm.buffsOne", "cfg.effects.confirm.buffs")
                              : PluralKey(ticked, "cfg.effects.confirm.debuffsOne", "cfg.effects.confirm.debuffs");
        SettingsConfirm.Show(new ConfirmRequest(
            _loc.TFormat(askKey, ticked),
            _loc.T((DcExcludeMode ? "cfg.effects.untick.msgAll." : "cfg.effects.untick.msgOnly.") + kind),
            ConfirmUntickAll)
        {
            Subtitle = _loc.T("cfg.tab.effects") + " · " + _loc.T(buffs ? "debuffcfg.tab.buffs" : "debuffcfg.tab.debuffs"),
            ConfirmLabel = _loc.T("cfg.effects.untickAll"),
            CancelLabel = _loc.T("cfg.cancel"),
            Severity = ConfirmSeverity.Caution,
        });
    }

    // ── History loading overlay ───────────────────────────────────────────────────────────────────────────────────
    // Over the chart + player table while _historyLoading (Plugin.HistoryMetric.cs) has lasted > 150 ms — a shorter
    // load would only flicker. The overlay is built once with the window; Visible / Background are polled at the window
    // refresh rate (only a change toggles or repaints it). The dots cycle through precomputed strings — no per-refresh
    // allocation; the dots sit in a fixed-width cell so the centred label doesn't jitter as they grow.
    private const double HistoryLoadingDelayS = 0.15;
    private const float  LoadingDotsW = 18f;
    private static readonly string[] LoadingDots = { "", ".", "..", "..." };

    private bool HistoryLoadingShown()
        => _historyLoading && _services.Framework.TimeNow - _historyLoadingSince > HistoryLoadingDelayS;

    private HudElement WithHistoryLoadingOverlay(HudElement content) => new LayerStackElement(content,
        new StackLayer(new RowElement(new HudElement[]
        {
            new TextElement(() => _loc.T("history.loading"), Color: () => TextColor(), NoWrap: true, FontSize: 14)
                { Bold = true },
            new TextElement(() => LoadingDots[(int)(_services.Framework.TimeNow * 3.0) & 3], Color: () => TextColor(),
                Width: LoadingDotsW, NoWrap: true, FontSize: 14) { Bold = true },
        }, Gap: 0.01f))
        {
            Visible = HistoryLoadingShown,
            BlocksInput = true,
            Background = () => Fade(PanelColor() with { A = 0.70f }),
            Center = true,
        });
}
