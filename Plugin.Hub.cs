using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// The CombatMeter hub (approved 2026-11-17, mockup combatmeter-hub.html): what the ONE launcher tile opens, in the MultiTool
// hub look (StellarMultiToolPlugin Plugin.Hub.cs, SDL "Hub card"). ToolHeader = the meter-bars mark, bold "CombatMeter",
// the meter's own live status line (Plugin.HudStatus.cs — the SAME builder the meter header polls; "Idle" when it is
// empty). Three 56 px cards:
//   Meter          — click = show / hide the meter (exactly the Toggle hotkey); trailing Shown / Hidden chip instead of ›
//   Combat History — "N runs saved" (the grouped run count, as the History header + the ⋯ popover card) ; › opens History
//   Settings       — the tab names; › opens Settings on the last-used tab
// Both › cards open their window centred on the hub and in front; an already-open child is just fronted (OpenHistory /
// OpenSettings — CLAUDE.md Rule 5 exception). In-world only (ShouldRender World && !Loading) and closed on logout.
// Per refresh: every Func is cached — the status reuses HudStatusText's packed-key cache, the subtitles re-format only
// when their inputs or the language change (Plugin-Performance-Rules §2).
public sealed partial class Plugin
{
    private const float HubWindowW    = 340f;
    private const float HubCardStrut  = 48f;                 // + the Selectable's 4 + 4 = the 56 px card pitch (MultiTool)
    private const float HubCardGap    = 8f;
    private const float HubIconTile   = 36f, HubGlyph = 20f; // mockup .c .ib: a 36 px accent tile, 20 px glyph
    private const float HubChipLabelW = 66f;                 // longest Shown / Hidden in any locale (th "ซ่อนอยู่", fil "Nakikita") at bold 12

    private IWindowControl _hubWindow = null!;
    private WindowCenterer? _hubCenterer;

    // Registered by RegisterLauncher (after History / Settings exist — the cards open them).
    private IWindowControl RegisterHubWindow()
    {
        var w = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.hub",
                Title:       "CombatMeter",   // the brand — identical in every locale
                DefaultRect: new WindowRect(860f, 220f, HubWindowW, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, Closable = true, Draggable = true,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            BuildHubRoot(),
            // REQUIRED for a Closable window: with no OnClose the framework's title-bar ✕ only deactivates the root GameObject
            // (WindowBuilder.Chrome.cs BuildCloseButton) and never clears the window's Visible flag — so the next apply
            // (WindowRenderer.SetShown: Visible + inactive root = a re-show) re-activates it: "✕ closes, then it pops right back".
            OnClose: CloseHub));
        _hubCenterer = new WindowCenterer(_services.Framework, w);
        _services.ClientState.Logout += OnLogoutHideHub;
        return w;
    }

    // Launcher tile: open centred on the screen and in front, or close when it is already open.
    private void ToggleHub()
    {
        if (_hubWindow.IsShown) { CloseHub(); return; }
        _hubCenterer?.OpenCentred(null);
    }

    // The title-bar ✕. Stop the centerer first so a close mid-settle leaves no tick subscribed.
    private void CloseHub()
    {
        _hubCenterer?.Stop();
        _hubWindow.SetVisible(false);
    }

    // Logout: close it so it neither lingers through the logout transition (the phase stays World until the login view)
    // nor pops back open at the next login. Synchronous (Update is world-gated; Game-Phases-and-Tick-Gating.md §4).
    private void OnLogoutHideHub() => CloseHub();

    private void DisposeHub()
    {
        _services.ClientState.Logout -= OnLogoutHideHub;
        _hubCenterer?.Stop();
    }

    // Show / hide the meter — the Toggle hotkey (F9) and the hub's Meter card.
    private void ToggleMeterShown() => _mainWindow.SetVisible(!_mainWindow.IsShown);

    // ── layout ────────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildHubRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(MeterIconTile(), () => "CombatMeter", new RowElement(new HudElement[]
        {
            new ConditionalElement(HudStatusHasDot, StatusDot(HudStatusDotColor)),
            new CellElement(new TextElement(HubStatusText, Color: () => _hudStatusKind == HudStatusKind.None ? MutedColor() : HudStatusColor(),
                NoWrap: true) { Ellipsize = true }, Weight: 1f),
        }, Gap: 6f)),
        HubCard(() => MeterIconPng(HubGlyph), () => _loc.T("hub.meter"), HubMeterSubtitle, MeterShownChip(), ToggleMeterShown),
        HubCard(() => HistoryIconPng(HubGlyph),() => _loc.T("history.window.title"), HistoryCardStatus, HubChevron(),
            () => OpenHistory(_hubWindow)),
        HubCard(() => GearIconPng(HubGlyph), () => _loc.T("hud.more.settings"), HubSettingsSubtitle, HubChevron(),
            () => OpenSettings(_settingsTabSel, _hubWindow)),
    }, Gap: HubCardGap);

    // The meter header's status, or "Idle" while it shows nothing (out of combat).
    private string HubStatusText()
    {
        var s = HudStatusText();
        return s.Length > 0 ? s : _loc.T("hub.status.idle");
    }

    // A raised card (CardColor — it sits directly on the window) around a Selectable row: [36 px accent icon tile]
    // [bold title over a muted subtitle] [flex] [trailing] [height strut]. Bubble OUTSIDE the Selectable (a bubble inside
    // one gets a full-width chip painted behind it; WindowBuilder-Patterns.md §BubbleElement).
    private HudElement HubCard(Func<byte[]?> icon, Func<string> title, Func<string> subtitle, HudElement trailing, Action onClick)
        => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, HubCardStrut),
            HubIconTileOf(icon),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(title, Color: () => TextColor(), NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
                new TextElement(subtitle, Color: () => MutedColor(), NoWrap: true, FontSize: 12) { Ellipsize = true },
            }, Gap: 2f), Weight: 1f),
            trailing,
            new SpacerElement(2f, HubCardStrut),   // right margin
        }, Gap: 10f), OnClick: onClick))
        {
            FillColorFn = () => Fade(CardColor()), CornerRadius = 6f, PaddingX = 0f, PaddingY = 0f,
        };

    // 36 px accent tile with the flat-white glyph tinted with the solid-fill label colour (live with the theme).
    private HudElement HubIconTileOf(Func<byte[]?> icon)
    {
        const float pad = (HubIconTile - HubGlyph) / 2f;
        return new CellElement(new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, HubGlyph),
                new ImageElement(icon, (int)HubGlyph, (int)HubGlyph) { Live = true, Tint = () => SolidLabel(AccentColor()) },
            }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(HubIconTile * 0.22f),
            PaddingX = pad, PaddingY = pad, MaxWidth = HubIconTile,
        }, Width: HubIconTile);
    }

    // Bold (styled path) so FontSize applies and a missing glyph can't fall back to a taller font (MultiTool).
    private HudElement HubChevron() => new TextElement(() => "›", Color: () => MutedColor(), NoWrap: true, FontSize: 18) { Bold = true };

    // Shown / Hidden status chip (SDL StatusChip): on = bold Success on a 22 % Success wash, off = bold Muted on a faint
    // wash. Fixed width in both states (the longest label in any locale), so flipping it never moves anything.
    private HudElement MeterShownChip() => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, ChipHeight),
            new TextElement(() => _loc.T(_mainWindow.IsShown ? "hub.meter.shown" : "hub.meter.hidden"),
                Color: () => _mainWindow.IsShown ? Readable(SuccessColor(), MeterChipFill()) : MutedColor(),
                Width: HubChipLabelW, Align: TextAlign.Center, NoWrap: true, FontSize: 12) { Bold = true, CenterCaps = true },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => Fade(MeterChipFill()), CornerRadius = RoundRadius(ChipHeight), PaddingX = ChipPadX, PaddingY = 0f,
    }, Width: HubChipLabelW + 2f * ChipPadX);

    private ColorRgba MeterChipFill() => _mainWindow.IsShown
        ? Over(SuccessColor() with { A = 0.22f }, CardColor())
        : Over(MutedColor() with { A = 0.15f }, CardColor());

    // "Party view · DPS" — re-formatted only when the view, metric or language changes.
    private int _hubMeterKey = -1;
    private string? _hubMeterLang;
    private string _hubMeterText = "";

    private string HubMeterSubtitle()
    {
        int key = (_viewMode == ViewMode.List ? 0 : 1) | (int)_metric << 1;
        string lang = _loc.Language;
        if (key == _hubMeterKey && ReferenceEquals(lang, _hubMeterLang)) return _hubMeterText;
        _hubMeterKey = key; _hubMeterLang = lang;
        _hubMeterText = _loc.TFormat("hub.meter.status",
            _loc.T(_viewMode == ViewMode.List ? "header.mode.list" : "hud.mode.party"), MetricOptions()[(int)_metric]);
        return _hubMeterText;
    }

    // "Look · Effects · Archive · Uploads" — once per language.
    private string? _hubSettingsLang;
    private string _hubSettingsText = "";

    private string HubSettingsSubtitle()
    {
        string lang = _loc.Language;
        if (ReferenceEquals(lang, _hubSettingsLang)) return _hubSettingsText;
        _hubSettingsLang = lang;
        _hubSettingsText = string.Join(" · ", _loc.T("cfg.tab.look"), _loc.T("cfg.tab.effects"),
            _loc.T("cfg.tab.archive"), _loc.T("cfg.tab.uploads"));
        return _hubSettingsText;
    }
}
