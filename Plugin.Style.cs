using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// House style (Stellar Design Language, Knowledge Base\Stellar-Design-Language.md + .claude\skills\stellar-design) — the
// ONE kit every CombatMeter window builds from (Settings now; History / meter HUD / popovers next). Ported from
// StellarAutoGatherPlugin Plugin.Style.cs (← Position ← Maestro ← MultiTool): live theme tokens, the colour maths, window
// opacity, the RECESSED disabled look (rule 22), 28 px pills and the rounded SectionPanel. Chips, tabs, icon buttons,
// dropdown / slider recipes and list tiles are in Plugin.Style.Controls.cs; the "?" + help popover in Plugin.Style.Help.cs.
//
// Toolkit limits behind some choices (WindowBuilder-Patterns.md):
//   • Plain TextElement ignores FontSize on Menu windows (fixed 14px) — only the styled path (Bold/Emphasis) honours it.
//   • Row/Column Gap 0 means the DEFAULT gap (8/12), never zero — every container here passes an explicit Gap.
//   • Bold can't be toggled live → controls whose label weight would change are bold in BOTH states (only the colour is
//     live), so nothing changes size on a toggle.
//
// Performance (Plugin-Performance-Rules.md §7): every colour here is pure struct maths on the live theme — no allocation
// inside the per-refresh Funcs. Nothing theme-derived is cached at build time, so a theme switch repaints on the next
// refresh.
public sealed partial class Plugin
{
    // ── palette: LIVE from the current framework theme ────────────────────────────────────────────────────────────
    private static readonly ColorRgba PanelShade = new(0f, 0f, 0f, 0.20f);   // panel = background darkened
    private static readonly ColorRgba TrackShade = new(0f, 0f, 0f, 0.34f);   // tab track / segmented control = deeper
    private const float TileTextMix       = 0.08f;   // raised tile = panel + 8% MenuText …
    private const float TileAccentMix     = 0.06f;   // … + a 6% accent tint (teal on teal, pink on pink)
    private const float CardTextMix       = 0.06f;   // card on the WINDOW background = background + 6% MenuText …
    private const float CardAccentMix     = 0.08f;   // … + an 8% accent tint
    private const float NeutralMix        = 0.22f;   // neutral pill = panel + 22% MenuMuted …
    private const float NeutralAccentMix  = 0.08f;   // … + an 8% accent tint, like the tiles
    private const float TintedMinContrast = 4.5f;    // labels on TINTED fills (Neutral, Caution, chips)
    private const float SolidMinContrast  = 3.0f;    // white labels on SOLID fills (Primary, Danger) — R7
    private const float ChipWashAlpha     = 0.22f;   // "on" chip = colour @22% over the surface
    private const float ChipOffAlpha      = 0.06f;   // "off" chip = MenuText @6% (the prototype's faint chip)
    private const float ChipHoverAlpha    = 0.08f;   // hover adds this much MenuText (keeps the chip's shape)
    private const float HoverMix          = 0.10f;   // pill hover = MenuText@0.10 over the fill
    private const float SelectedWashMix   = 0.22f;   // selected row / active choice = accent @22%

    private static readonly ColorRgba DangerFillFixed = Rgb(0xff4d4f, 0.85f);   // the house Danger pill (fixed)
    private static readonly ColorRgba ProblemText     = Rgb(0xff8a8b);         // house error red for TEXT
    private static readonly ColorRgba White           = new(1f, 1f, 1f);
    private static readonly ColorRgba DarkLabel       = new(0.12f, 0.12f, 0.12f, 1f);   // #1F1F1F solid-fill fallback
    private static readonly ColorRgba Transparent     = new(0f, 0f, 0f, 0f);

    private IThemeMenuColors Menu => _services.Theme.Colors;

    private ColorRgba TextColor()    => Menu.MenuText;
    private ColorRgba MutedColor()   => Menu.MenuMuted;
    private ColorRgba AccentColor()  => Menu.MenuAccent;
    private ColorRgba PanelColor()   => Over(PanelShade, Menu.MenuBackground);
    private ColorRgba TrackColor()   => Over(TrackShade, Menu.MenuBackground);
    private ColorRgba TileColor()    => Over(Menu.MenuAccent with { A = TileAccentMix },
                                             Over(Menu.MenuText with { A = TileTextMix }, PanelColor()));
    // Cards sit directly on the WINDOW background (not in a panel), where the panel-based Tile would blend in (R6).
    private ColorRgba CardColor()    => Over(Menu.MenuAccent with { A = CardAccentMix },
                                             Over(Menu.MenuText with { A = CardTextMix }, Menu.MenuBackground));
    private ColorRgba NeutralFill()  => Over(Menu.MenuAccent with { A = NeutralAccentMix },
                                             Over(Menu.MenuMuted with { A = NeutralMix }, PanelColor()));
    private ColorRgba NeutralText()  => EnsureContrast(TextColor(), NeutralFill(), TintedMinContrast);
    private ColorRgba PrimaryFill()  => Menu.MenuAccent;
    private ColorRgba PrimaryText()  => SolidLabel(PrimaryFill());
    private ColorRgba CautionFill()  => Over(CautionColor() with { A = ChipWashAlpha }, PanelColor());
    private ColorRgba CautionText()  => EnsureContrast(CautionColor(), CautionFill(), TintedMinContrast);
    // Danger = the fixed 85%-alpha red composed OPAQUE over the panel, so the window fade scales one solid colour.
    private ColorRgba DangerSolid()  => Over(DangerFillFixed, PanelColor());
    private ColorRgba DangerText()   => SolidLabel(DangerSolid());
    private ColorRgba SelectedFill() => Over(Menu.MenuAccent with { A = SelectedWashMix }, PanelColor());
    private ColorRgba ErrorText()    => EnsureContrast(ProblemText, PanelColor(), TintedMinContrast);

    // Theme status colours (fw 2.30.0, IThemeStatusColors; dark presets = the old #5FD38D / #FFCC73 / #FF4D4F).
    private ColorRgba SuccessColor() => _services.Theme.Colors.Success;
    private ColorRgba CautionColor() => _services.Theme.Colors.Caution;
    private ColorRgba DangerColor()  => _services.Theme.Colors.Danger;
    // Status text kept readable on the surface it sits on (darkens on the Light theme).
    private ColorRgba Readable(ColorRgba c, ColorRgba surface) => EnsureContrast(c, surface, TintedMinContrast);
    private ColorRgba SuccessText() => Readable(SuccessColor(), PanelColor());
    private ColorRgba CautionTextOnPanel() => Readable(CautionColor(), PanelColor());

    // ── window opacity: every self-drawn fill fades with the frame (SDL §5) ───────────────────────────────────────
    // Themes → Window Opacity fades ONLY the GlassMenu frame Image; our fills are opaque pre-blended colours, so every
    // FillColorFn goes through Fade (compose OPAQUE first, then scale the alpha). Text and pictures stay solid.
    // IChromeStyle isn't on IPluginServices; the shared INamedTheme instance implements it (ChatTools / Dungeon Hub).
    private float WindowOpacity()
    {
        try { return Math.Clamp((_services.NamedTheme as IChromeStyle)?.WindowOpacity ?? 1f, 0.3f, 1f); }
        catch { return 1f; }
    }

    private ColorRgba Fade(ColorRgba c) => c with { A = c.A * WindowOpacity() };   // Fade(Transparent) stays transparent

    // ── rule 22: pressable ≠ disabled ─────────────────────────────────────────────────────────────────────────────
    // Disabled = RECESSED: black @28 % over the surface it sits on (darker than the surface, where a pressable fill is
    // lighter), same size and radius, no outline ring, faint label (Text @32 %, raised in 0.04 steps until 2:1).
    private static readonly ColorRgba DisabledShade = new(0f, 0f, 0f, 0.28f);

    private static ColorRgba DisabledFill(ColorRgba surface) => Over(DisabledShade, surface);

    private ColorRgba DisabledLabel(ColorRgba surface)
    {
        var bg = DisabledFill(surface);
        float lb = Luminance(bg), a = 0.32f;
        while (a < 1f && ContrastRatio(Luminance(Over(TextColor() with { A = a }, bg)), lb) < 2f) a = MathF.Min(1f, a + 0.04f);
        return TextColor() with { A = a };
    }

    // The label on a SOLID fill (R7): white while white reaches 3:1 against the fill AS DRAWN (window opacity included),
    // else near-black at 4.5:1 — never a mid grey that reads as disabled.
    private ColorRgba SolidLabel(ColorRgba fill)
    {
        var seen = Over(fill with { A = WindowOpacity() }, PanelColor());
        if (ContrastRatio(1f, Luminance(seen)) >= SolidMinContrast) return White;
        return EnsureContrast(DarkLabel, seen, TintedMinContrast);
    }

    // ── colour maths (ChatTools Plugin.Tabs.cs → MultiTool / Maestro / AutoGather; pure struct math) ──────────────
    private static ColorRgba EnsureContrast(ColorRgba fg, ColorRgba bg, float minRatio)
    {
        float lb = Luminance(bg);
        // 0.179 = the luminance where white and black give equal contrast; pick whichever end gains more.
        var target = lb < 0.179f ? new ColorRgba(1f, 1f, 1f) : new ColorRgba(0f, 0f, 0f);
        for (int i = 0; i < 12 && ContrastRatio(Luminance(fg), lb) < minRatio; i++) fg = Mix(fg, target, 0.15f);
        return fg with { A = 1f };
    }

    private static float ContrastRatio(float la, float lb) => (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);
    private static float Luminance(ColorRgba c) => 0.2126f * Lin(c.R) + 0.7152f * Lin(c.G) + 0.0722f * Lin(c.B);
    private static float Lin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    // a → b by t (0 = a, 1 = b), opaque.
    private static ColorRgba Mix(ColorRgba a, ColorRgba b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);
    // Alpha-composite fg (its A = coverage) over an opaque bg.
    private static ColorRgba Over(ColorRgba fg, ColorRgba bg) => Mix(bg, fg with { A = 1f }, fg.A);
    private static ColorRgba Rgb(uint rgb, float a = 1f) =>
        new(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, a);

    // ── hover that follows the shape ──────────────────────────────────────────────────────────────────────────────
    // A normal SelectableElement paints a SQUARE hover wash. Every clickable here is a Bare selectable whose OnHover
    // flips a flag, and the surrounding Bubble's FillColorFn shows the hover — so it keeps the round shape (R2).
    private sealed class HoverFlag { public bool On; }

    private ColorRgba HoverFill(ColorRgba fill) => Over(Menu.MenuText with { A = HoverMix }, fill);

    // The corner radius for a ROUND bubble of height `side`: side / 2 − 1, NOT side / 2 (R1) — the 9-slice background's
    // min size is 2r+2, so side / 2 made every round control 2 px taller than its content.
    private static float RoundRadius(float side) => side / 2f - 1f;

    // Bold (TMP) labels sit ~1.5–2 px HIGH when centred; a 2 px spacer above moves the text down 1 px (Maestro measured).
    private static HudElement DropBold(HudElement label) => new ColumnElement(new HudElement[]
    {
        new SpacerElement(0f, 2f),
        label,
    }, Gap: 0.01f);

    // ── pill buttons ──────────────────────────────────────────────────────────────────────────────────────────────
    // ONE build for every labelled button: Bubble (fill + hover) → Bare Selectable → Row[strut, bold-13 label, strut].
    // 28 px tall = the struts; the label has a FIXED width (`labelW`, the longest label in any locale) so a label change
    // never resizes it (R10). `enabled` (polled): disabled = recessed fill + faint label, same geometry, click swallowed.
    private const float PillHeight = 28f;
    private const float PillPadX   = 12f;

    private HudElement PillButton(Func<string> label, Func<ColorRgba> fill, Func<ColorRgba> text, Action onClick,
        float labelW, Func<bool>? enabled = null)
    {
        var hover = new HoverFlag();
        bool En() => enabled == null || enabled();
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(PillPadX, PillHeight),
                DropBold(new TextElement(label, Color: () => En() ? text() : DisabledLabel(PanelColor()),
                    Width: labelW, Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true }),
                new SpacerElement(PillPadX, PillHeight),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (En()) onClick(); })
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(!En() ? DisabledFill(PanelColor()) : hover.On ? HoverFill(fill()) : fill()),
            CornerRadius = RoundRadius(PillHeight), PaddingX = 0f, PaddingY = 0f,
        };
    }

    // Severity ladder (R8): Danger = stops it for everyone · Caution = stops only my part · Primary = main action ·
    // Neutral = everything else.
    private HudElement PrimaryButton(Func<string> label, Action onClick, float labelW, Func<bool>? enabled = null) =>
        PillButton(label, PrimaryFill, PrimaryText, onClick, labelW, enabled);
    private HudElement NeutralButton(Func<string> label, Action onClick, float labelW, Func<bool>? enabled = null) =>
        PillButton(label, NeutralFill, NeutralText, onClick, labelW, enabled);
    private HudElement CautionButton(Func<string> label, Action onClick, float labelW, Func<bool>? enabled = null) =>
        PillButton(label, CautionFill, CautionText, onClick, labelW, enabled);
    private HudElement DangerButton(Func<string> label, Action onClick, float labelW, Func<bool>? enabled = null) =>
        PillButton(label, DangerSolid, DangerText, onClick, labelW, enabled);

    // Two pills side by side at equal widths filling the row (weighted cells only split equally when both halves prefer
    // the same width, so pass the same labelW to both).
    private static HudElement PillPair(HudElement left, HudElement right) => new RowElement(new HudElement[]
    {
        new CellElement(left,  Weight: 1f),
        new CellElement(right, Weight: 1f),
    }, Gap: 8f);

    // ── section panel ─────────────────────────────────────────────────────────────────────────────────────────────
    // A rounded panel (background darkened, live): bold 12 UPPERCASE caption + right-aligned muted hint (+ an optional
    // trailing control, e.g. a "?"), then the children. Directly in a Column it FILLS the width (a Bubble only hugs inside
    // a Row). The caption is a Func so it re-localizes live; ToUpper runs only when the string changes (cached), never per
    // refresh. The caption row is pinned to CaptionHeight so a panel with a 24 px "?" in its caption and one without line
    // up their bodies side by side (SDL §6).
    // ⚠️ The trailing variant has its OWN NAME: as a second params overload it silently swallowed the first child of
    // every call (C# picks the overload with more declared parameters; Maestro Link, KB SDL §4).
    private const float CaptionHeight = 24f;
    private const float PanelPadX = 10f, PanelPadY = 8f, PanelGap = 6f;

    private HudElement SectionPanel(Func<string> caption, Func<string>? hint, params HudElement[] children)
        => SectionPanelWithTrailing(caption, hint, null, children);

    private HudElement SectionPanelWithTrailing(Func<string> caption, Func<string>? hint, HudElement? trailing,
        params HudElement[] children)
    {
        string? capSrc = null, capUpper = "";
        string Cap()
        {
            string s = caption();
            if (!ReferenceEquals(s, capSrc)) { capSrc = s; capUpper = s.ToUpperInvariant(); }
            return capUpper;
        }
        var row = new List<HudElement>(5)
        {
            new SpacerElement(0f, CaptionHeight),
            new TextElement(Cap, Color: () => TextColor(), NoWrap: true, FontSize: 12) { Bold = true },
            new SpacerElement(0f),   // flexible: pushes the hint to the right edge
        };
        if (hint != null) row.Add(new TextElement(hint, Color: () => MutedColor(), NoWrap: true));
        if (trailing != null) row.Add(trailing);
        var items = new List<HudElement>(children.Length + 1) { new RowElement(row, Gap: 6f) };
        items.AddRange(children);
        return PanelBox(items);
    }

    // The bare panel shell (no caption row) — for a panel whose first row is custom (e.g. kind chips as the caption).
    private HudElement PanelBox(IReadOnlyList<HudElement> children) => new BubbleElement(new ColumnElement(children, Gap: PanelGap))
    {
        FillColorFn = () => Fade(PanelColor()), CornerRadius = 6f, PaddingX = PanelPadX, PaddingY = PanelPadY,
    };

    // A muted body line (footers, hints in a panel).
    private HudElement MutedLine(Func<string> text) => new TextElement(text, Color: () => MutedColor());
}
