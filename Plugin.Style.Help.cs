using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Per-setting help (house help popover; reference StellarAutoGatherPlugin Plugin.Help.cs ← Position ← Maestro). The
// window framework has no hover tooltip, so each setting row ends in a round "?" that opens ONE shared floating popover
// anchored under it:
//   [32 px accent tile with a bold "?"] [bold setting name / muted "SECTION · HELP"] … [round ✕]
//   then the help text in one raised tile, 360 px wide.
// No framework title bar (the header carries the title and the close); with ShowTitleBar = false the whole frame is the
// drag handle. Re-clicking the open "?" closes it. Entries hold LOCALIZATION KEYS, resolved at draw time, so a language
// switch re-localizes an open popover. Any CombatMeter window can call HelpDot.
public sealed partial class Plugin
{
    private readonly Dictionary<string, (string TitleKey, string BodyKey, string SectionKey)> _helpEntries = new();

    private IWindowControl _helpWindow = null!;
    private string _helpKey = "";   // which help entry the popover shows; "" = closed
    private const float HelpPopWidth    = 360f;
    private const float HelpIconSize = 32f;
    private const float HelpSize    = 24f;   // the round "?" — exactly 24 × 24 (RoundRadius, R1/R4)
    private WindowRect _helpRect;     // where the popover should sit (re-applied for a few frames after opening)
    private int _helpRepositionLeft;

    // Registers help content for `key` and returns its "?". Append it as the LAST child of a row so the "?"s line up in
    // the panel's right-edge column (rule 17). Keys are catalog keys: title (usually the row's own label key), body,
    // and the section caption shown in the subtitle.
    private HudElement HelpDot(string key, string sectionKey, string titleKey, string bodyKey)
    {
        _helpEntries[key] = (titleKey, bodyKey, sectionKey);
        var hover = new HoverFlag();
        return new CellElement(new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                // Height strut = the side; the label box sets the width. NOTHING taller than 24 may sit inside, or the
                // circle turns oval (R4). CenterGlyphs centres the drawn "?" in its box.
                new SpacerElement(0f, HelpSize),
                new TextElement(() => "?", Color: () => NeutralText(), Width: HelpSize, Align: TextAlign.Center,
                    NoWrap: true, FontSize: 12) { Bold = true, CenterGlyphs = true },
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { })                       // real work is in OnClickWithRect (needs the button's rect)
            {
                Bare = true,
                OnHover = over => hover.On = over,
                OnClickWithRect = r => ToggleHelp(key, r),
            })
        {
            FillColorFn = () => Fade(ReferenceEquals(_helpKey, key) && _helpWindow.IsShown
                                  ? Over(AccentColor() with { A = 0.35f }, NeutralFill())
                              : hover.On ? HoverFill(NeutralFill()) : NeutralFill()),
            CornerRadius = RoundRadius(HelpSize), PaddingX = 0f, PaddingY = 0f, MaxWidth = HelpSize,
        }, Width: HelpSize);
    }

    private void ToggleHelp(string key, WindowRect r)
    {
        if (ReferenceEquals(_helpKey, key) && _helpWindow.IsShown) { CloseHelp(); return; }
        _helpKey = key;
        // OnClickWithRect reports SCREEN PIXELS; SetRect takes CANVAS units — scale first, or the popover drifts off its
        // "?" whenever the UI scale isn't 1 (WindowBuilder-Patterns.md, click-to-open tooltip).
        float k = _services.Framework.ScreenWidth > 0 && _services.Framework.CanvasWidth > 1f
            ? _services.Framework.CanvasWidth / _services.Framework.ScreenWidth : 1f;
        r = new WindowRect(r.X * k, r.Y * k, r.Width * k, r.Height * k);
        float canvasH = _services.Framework.CanvasHeight > 1f ? _services.Framework.CanvasHeight : _services.Framework.ScreenHeight;
        // Below the "?", right-aligned to it, so it opens to the LEFT of the "?" column instead of covering the setting.
        float x = Math.Max(10f, r.X + r.Width - HelpPopWidth);
        float y = Math.Min(r.Y + r.Height + 4f, canvasH - 40f);
        _helpRect = new WindowRect(x, y, HelpPopWidth, 0f);
        _helpWindow.SetVisible(true);
        _helpWindow.SetRect(_helpRect);
        // Switching between "?"s: the popover is ALREADY visible, so it doesn't remount and keeps its old z position —
        // front it so it stays above the settings window (CLAUDE.md Rule 5 exception: re-opening a visible child).
        _helpWindow.BringToFront();
        _helpWindow.MarkDirty();      // re-pull title / body for the new key this frame
        // The first-ever open mounts the window and applies its DefaultRect AFTER this SetRect → re-assert the anchored
        // rect for the next few frames (one-shot Posts, not a standing per-frame subscription).
        _helpRepositionLeft = 4;
        _services.Framework.Post(HelpReposition);
    }

    private void HelpReposition()
    {
        if (_helpRepositionLeft <= 0) return;
        _helpRepositionLeft--;
        if (_helpWindow.IsShown) _helpWindow.SetRect(_helpRect);
        if (_helpRepositionLeft > 0) _services.Framework.Post(HelpReposition);
    }

    private void CloseHelp()
    {
        _helpKey = "";
        _helpRepositionLeft = 0;
        try { _helpWindow?.SetVisible(false); } catch { /* never throw from a close path */ }
    }

    private string HelpTitle() => _helpEntries.TryGetValue(_helpKey, out var e) ? _loc.T(e.TitleKey) : "";
    private string HelpBody()  => _helpEntries.TryGetValue(_helpKey, out var e) ? _loc.T(e.BodyKey)  : "";

    // "ROW · HELP" — formatted + upper-cased only when the entry or the language changes (not per refresh).
    private string? _helpSubKey, _helpSubLang;
    private string  _helpSubText = "";
    private string HelpSubtitle()
    {
        string lang = _loc.Language;
        if (!ReferenceEquals(_helpKey, _helpSubKey) || !ReferenceEquals(lang, _helpSubLang))
        {
            _helpSubKey = _helpKey; _helpSubLang = lang;
            _helpSubText = _helpEntries.TryGetValue(_helpKey, out var e)
                ? _loc.TFormat("help.subtitle", _loc.T(e.SectionKey)).ToUpper(System.Globalization.CultureInfo.InvariantCulture)
                : "";
        }
        return _helpSubText;
    }

    private IWindowControl RegisterHelpWindow()
    {
        IWindowControl w = null!;
        w = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "combatmeter.help",
                Title:       "Help",   // fallback only (no title bar is drawn); localized below
                DefaultRect: new WindowRect(900f, 200f, HelpPopWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false, ShowTitleBar = false,
              TitleProvider = () => _loc.T("help.label"),
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root: BuildHelpRoot(),
            OnClose: () => { _helpKey = ""; w!.SetVisible(false); }));
        return w;
    }

    private readonly HoverFlag _helpCloseHover = new();

    private HudElement BuildHelpRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            // 32 px accent tile with a bold "?" (the IconBox look, drawn with text instead of a PNG).
            new CellElement(new BubbleElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, HelpIconSize),
                    new TextElement(() => "?", Color: () => SolidLabel(AccentColor()), Width: HelpIconSize,
                        Align: TextAlign.Center, NoWrap: true, FontSize: 18) { Bold = true, CenterGlyphs = true },
                }, Gap: 0.01f, Justify: RowJustify.Center))
            {
                FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(HelpIconSize * 0.22f), PaddingX = 0f, PaddingY = 0f,
            }, Width: HelpIconSize),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(HelpTitle, Color: () => TextColor(), NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
                new TextElement(HelpSubtitle, Color: () => MutedColor(), NoWrap: true, FontSize: 11) { Bold = true, Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            // Icon-only ✕ (plain text — bold ✕ can draw blank): a round 24 px hover wash around a Bare selectable.
            new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, 24f),
                    new TextElement(() => "✕", Color: () => _helpCloseHover.On ? TextColor() : MutedColor(), Width: 24f,
                        Align: TextAlign.Center, NoWrap: true),
                }, Gap: 0.01f, Justify: RowJustify.Center),
                OnClick: CloseHelp) { Bare = true, OnHover = over => _helpCloseHover.On = over })
            {
                FillColorFn = () => _helpCloseHover.On ? Fade(HoverFill(Menu.MenuBackground)) : Transparent,
                CornerRadius = RoundRadius(24f), PaddingX = 0f, PaddingY = 0f,
            },
        }, Gap: 10f),
        // The help text: one raised tile (house Tile colour, theme-live), wrapping inside the 360 px popover.
        new BubbleElement(new TextElement(HelpBody, Color: () => TextColor()))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = 6f, PaddingX = 10f, PaddingY = 8f,
        },
    }, Gap: 10f);
}
