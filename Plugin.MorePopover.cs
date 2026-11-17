using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// The header's "⋯ More" popover (approved redesign 2026-11-17 §2, prototype section 2). It replaces the old ▾ inline menu,
// which grew the HUD by 72 px; the HUD now never changes height. A house popover 300 wide, anchored under ⋯ (right-
// aligned to it, so it opens over the meter):
//   List view                 → SHOW panel (chips Self / Party / All, hint "who is listed")
//   Party-focus + leader      → PARTY SIZE panel (chips 5 / 20, hint "leader only")
//   always                    → ENCOUNTER panel (hint "1:42 recorded" / amber "● Paused"): Pause|Resume + Archive pills,
//                               then "✓ Archived (102 s) · History 37" for ~3 s after a manual archive
//   always                    → two nav cards: History ("37 runs saved") and Settings ("Look, archive, uploads")
// Esc / an outside click closes it (framework DismissOnOutsideClick → OnClose), like the row menu. A card closes the
// popover and opens its window centred on the meter and in front.
public sealed partial class Plugin
{
    private const float MoreW = 300f;            // the visible popover (the shell bubble)
    private const float MoreEstimateH = 330f;    // rough full height, only to decide "below / above the button"
    private const double ArchivedFeedbackS = 3.0;

    private IWindowControl _moreWindow = null!;
    private bool _moreOpen, _morePlaced;
    private WindowRect _moreRect;
    private double _moreClosedAt = double.NegativeInfinity;

    private IWindowControl RegisterMorePopover()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.more",
                Title:       "",
                DefaultRect: new WindowRect(100f, 100f, MoreW + 2f * PopoverMargin, 0f),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            { StartVisible = false, DismissOnOutsideClick = true,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            BuildMoreRoot(),
            OnClose: CloseMorePopover));   // framework: Escape / click-outside

    private HudElement BuildMoreRoot() => PopoverShell(10f, 8f, new HudElement[]
    {
        new ConditionalElement(() => _viewMode == ViewMode.List, SectionPanel(
            () => _loc.T("hud.more.show"), () => _loc.T("hud.more.showHint"),
            new RowElement(new HudElement[]
            {
                ChoiceChip(() => _loc.T("scope.self"),  () => _filter == FilterMode.Self,  () => SelectScope(FilterMode.Self)),
                ChoiceChip(() => _loc.T("scope.party"), () => _filter == FilterMode.Party, () => SelectScope(FilterMode.Party)),
                ChoiceChip(() => _loc.T("scope.all"),   () => _filter == FilterMode.All,   () => SelectScope(FilterMode.All)),
            }, Gap: 6f))),
        new ConditionalElement(() => ShowPartySizeControl, SectionPanel(
            () => _loc.T("hud.more.partySize"), () => _loc.T("hud.more.leaderOnly"),
            new RowElement(new HudElement[]
            {
                FixedChip("5",  () => _services.PartySnapshot.PartyType == PartyType.Regular5, () => RequestPartySize(PartyType.Regular5)),
                FixedChip("20", () => _services.PartySnapshot.PartyType == PartyType.Raid20,   () => RequestPartySize(PartyType.Raid20)),
            }, Gap: 6f))),
        BuildEncounterPanel(),
        NavCard(() => _historyIconPng, () => _loc.T("history.window.title"), HistoryCardStatus, OpenHistoryFromHud),
        NavCard(() => _settingsGearPng, () => _loc.T("hud.more.settings"), () => _loc.T("hud.more.settingsStatus"), OpenSettingsFromHud),
    });

    // ── ENCOUNTER ───────────────────────────────────────────────────────────────────────────────────────────────────
    private const float EncounterPillLabelW = 76f;   // longest Pause / Resume / Archive in any shipped locale (R10)

    private HudElement BuildEncounterPanel() => SectionPanelWithTrailing(() => _loc.T("hud.more.encounter"), null,
        new RowElement(new HudElement[]
        {
            new ConditionalElement(() => _paused, StatusDot(CautionColor)),
            new TextElement(EncounterHint, Color: () => _paused ? CautionTextOnPanel() : MutedColor(), NoWrap: true),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            // Resume is the main action while paused (Primary); Pause and Archive are Neutral (severity ladder R8).
            new ConditionalElement(() => _paused,
                PrimaryButton(() => _loc.T("common.resume"), TogglePause, EncounterPillLabelW),
                NeutralButton(() => _loc.T("common.pause"), TogglePause, EncounterPillLabelW)),
            NeutralButton(() => _loc.T("common.archive"), ManualArchiveFromMenu, EncounterPillLabelW),
            new SpacerElement(0f),
        }, Gap: 8f),
        new ConditionalElement(() => _services.Framework.TimeNow < _archivedFbUntil,
            new TextElement(() => _archivedFbText, Color: () => SuccessText(), NoWrap: true) { Ellipsize = true }));

    // "1:42 recorded" (re-formatted only when the shown second or the language changes) / "Paused" / nothing when idle.
    private int _encHintSecs = -1;
    private string? _encHintLang;
    private string _encHintText = "";

    private string EncounterHint()
    {
        if (_paused) return _loc.T("hud.more.paused");
        int secs = (int)EncounterElapsedSeconds();
        if (secs <= 0) return "";
        string lang = _loc.Language;
        if (secs != _encHintSecs || !ReferenceEquals(lang, _encHintLang))
        {
            _encHintSecs = secs; _encHintLang = lang;
            _encHintText = _loc.TFormat("hud.more.recorded", Clock(secs));
        }
        return _encHintText;
    }

    // Inline archive feedback where you clicked (R13), formatted once per archive (Plugin.Header.cs NotifyManualArchived).
    private string _archivedFbText = "";
    private double _archivedFbUntil = double.NegativeInfinity;

    private void NoteArchivedFeedback(long durationMs)
    {
        _archivedFbText = _loc.TFormat("hud.more.archived", durationMs / 1000, _history.Count);
        _archivedFbUntil = _services.Framework.TimeNow + ArchivedFeedbackS;
    }

    private readonly FmtCache _runsSavedFmt = new();
    private string HistoryCardStatus() => _runsSavedFmt.Get(_loc.T("hud.more.runsSaved"), _history.Count);

    // ── building blocks ─────────────────────────────────────────────────────────────────────────────────────────────
    // A 56 × 24 choice chip with a centred label (the PARTY SIZE "5" / "20"): ChoiceChip hugs its label, which would make
    // "5" a 26 px sliver. Same fills, hover and label colours as ChoiceChip.
    private HudElement FixedChip(string label, Func<bool> active, Action onClick)
    {
        var hover = new HoverFlag();
        float H() => hover.On ? ChipHoverAlpha : 0f;
        ColorRgba Fill() => active() ? Over(TextColor() with { A = H() }, Over(AccentColor() with { A = ChipWashAlpha }, PanelColor()))
                                     : Over(TextColor() with { A = ChipOffAlpha + H() }, PanelColor());
        return new CellElement(ChipShell(
            new TextElement(() => label, Color: () => active() ? Readable(AccentColor(), Fill()) : MutedColor(),
                Width: 56f, Align: TextAlign.Center, NoWrap: true, FontSize: 12) { Bold = true },
            Fill, hover, onClick, padX: 0f), Width: 56f);
    }

    // Nav card (prototype `.ncard`): Card fill (R6 — it sits on the popover background), 44 px, a 32 px accent icon tile
    // with the flat-white glyph, bold title over a muted status, and a muted "›".
    private const float NavCardH = 44f, NavIconTile = 32f, NavGlyph = 20f;

    private HudElement NavCard(Func<byte[]?> icon, Func<string> title, Func<string> status, Action onClick)
    {
        var hover = new HoverFlag();
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, NavCardH),
                new CellElement(new BubbleElement(new RowElement(new HudElement[]
                    {
                        new SpacerElement(0f, NavGlyph),
                        new ImageElement(icon, (int)NavGlyph, (int)NavGlyph) { Tint = () => SolidLabel(AccentColor()) },
                    }, Gap: 0.01f, Justify: RowJustify.Center))
                {
                    FillColorFn = () => Fade(AccentColor()), CornerRadius = 7f,
                    PaddingX = (NavIconTile - NavGlyph) / 2f, PaddingY = (NavIconTile - NavGlyph) / 2f,
                }, Width: NavIconTile),
                new CellElement(new ColumnElement(new HudElement[]
                {
                    new TextElement(title, Color: () => TextColor(), NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true },
                    SmallMuted(status, () => MutedColor()),
                }, Gap: 1f), Weight: 1f),
                new TextElement(() => "›", Color: () => MutedColor(), Width: 20f, Align: TextAlign.Center, NoWrap: true,
                    FontSize: 17) { Bold = true },
            }, Gap: 9f),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(hover.On ? HoverFill(CardColor()) : CardColor()),
            CornerRadius = 6f, PaddingX = 6f, PaddingY = 0f,
        };
    }

    // ── open / close ────────────────────────────────────────────────────────────────────────────────────────────────
    // ⋯ click (anchor = the button's SCREEN-pixel rect). A click on ⋯ while the popover is open closes it — but the
    // framework's outside-click dismiss may already have closed it a moment earlier in the same click, so a press right
    // after a dismiss is treated as that same "close" instead of re-opening it.
    private void ToggleMorePopover(WindowRect anchorScreen)
    {
        if (_moreOpen || _services.Framework.TimeNow - _moreClosedAt < 0.25) { CloseMorePopover(); return; }
        var fw = _services.Framework;
        var a = ScreenRectToCanvas(anchorScreen, fw.ScreenWidth, fw.CanvasWidth);
        float w = MoreW + 2f * PopoverMargin, canvasW = fw.CanvasWidth, canvasH = fw.CanvasHeight;
        // Right edge of the visible shell flush with the ⋯ button's right edge; 4 px below it (or above when the
        // popover would run off the bottom). The Borderless window's own 12 px margin is subtracted out.
        float x = Math.Clamp(a.X + a.Width + PopoverMargin - w, 0f, Math.Max(0f, canvasW - w));
        float below = a.Y + a.Height + 4f - PopoverMargin;
        float y = below + MoreEstimateH <= canvasH ? below : Math.Max(0f, a.Y - 4f - MoreEstimateH);
        _moreRect = new WindowRect(x, y, w, 0f);
        _moreOpen = true;
        _morePlaced = false;
        _moreWindow.SetRect(_moreRect);   // no-op while unmounted; re-asserted by TickMorePopover
        _moreWindow.SetVisible(true);
    }

    private void CloseMorePopover()
    {
        if (_moreOpen) _moreClosedAt = _services.Framework.TimeNow;
        _moreOpen = false;
        _morePlaced = false;
        _moreWindow.SetVisible(false);
    }

    // Per-tick (Plugin.Tick.cs): re-assert the anchored rect once the destroy-on-hide window has remounted (SetRect at
    // open no-ops on the unmounted window — the row-menu pattern), and close with the meter (F9 / phase gate).
    private void TickMorePopover()
    {
        if (!_moreOpen) return;
        if (!_mainWindow.IsShown) { CloseMorePopover(); return; }
        if (!_morePlaced && _moreWindow.IsShown) { _moreWindow.SetRect(_moreRect); _morePlaced = true; }
    }

    // Cards: close the popover, then open the window centred on the meter and in front (SDL §5). Already open → front
    // it only (CLAUDE.md Rule 5 exception: re-opening an already-visible child from its parent).
    private WindowCenterer? _historyCenterer;
    private IWindowControl? _historyCentererWin;

    private void OpenHistoryFromHud()
    {
        CloseMorePopover();
        if (_historyWindow.IsShown) { _historyWindow.BringToFront(); return; }
        // The history window is re-registered on a language change (Plugin.Localization.cs) → bind a fresh centerer.
        if (_historyCenterer == null || !ReferenceEquals(_historyCentererWin, _historyWindow))
        {
            _historyCenterer?.Stop();
            _historyCenterer = new WindowCenterer(_services.Framework, _historyWindow);
            _historyCentererWin = _historyWindow;
        }
        _historyCenterer.OpenCentred(_mainWindow);
    }

    private void OpenSettingsFromHud()
    {
        CloseMorePopover();
        // Edit the view the meter shows (the old Appearance entry point did the same, Plugin.SettingsWindow.cs).
        if (!_settingsWindow.IsShown || _settingsTabSel != SettingsTab.Look)
            _lookView = _viewMode == ViewMode.List ? 0 : IsRaid20View ? 2 : 1;
        OpenSettings(SettingsTab.Look, _mainWindow);
    }
}
