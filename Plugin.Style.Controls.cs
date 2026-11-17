using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// More house-kit building blocks (Stellar Design Language §4; reference StellarAutoGatherPlugin Plugin.Style.Blocks.cs,
// StellarCooldownBarPlugin Plugin.Settings.Rows.cs): on/off chip, choice chip, segmented tab strip, round icon button,
// pill dropdown, round-knob slider, the 28 px setting row, the header icon tile + ToolHeader, the status dot, raised list
// tiles (26 px + 2 px gap) and a format cache. Shared by every CombatMeter window.
public sealed partial class Plugin
{
    // ── chips ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // 24 px round chips (r11 = RoundRadius(24)). Bold can't toggle live, so BOTH states are bold 12 (SDL §6: "when width
    // is tight, make both states bold at an explicit FontSize so they size alike") — only the fill + label colour are
    // live, so a click never changes a chip's size or moves its neighbours. Hover adds MenuText to the wash (keeps the
    // shape). Disabled = RECESSED (rule 22): sunken fill + faint label, click swallowed; an active-but-disabled choice
    // keeps a faint accent tint so you can still read which one is set.
    private const float ChipHeight     = 24f;
    private const float ChipPadX       = 9f;
    private const float OnOffChipWidth = 64f;   // fits the longest "On"/"Off" in every shipped locale at bold 12

    // On/off chip, 64 × 24 in both states: On = bold Success on a Success wash, Off = bold Muted on a faint wash.
    private HudElement ToggleChip(Func<bool> on, Action<bool> set, Func<bool>? enabled = null)
    {
        var hover = new HoverFlag();
        bool En() => enabled == null || enabled();
        float H() => hover.On ? ChipHoverAlpha : 0f;
        ColorRgba Fill() => !En() ? DisabledFill(PanelColor())
            : on() ? Over(SuccessColor() with { A = ChipWashAlpha + H() }, PanelColor())
            : Over(TextColor() with { A = ChipOffAlpha + H() }, PanelColor());
        ColorRgba Label() => !En() ? DisabledLabel(PanelColor()) : on() ? Readable(SuccessColor(), Fill()) : MutedColor();
        return new CellElement(ChipShell(
            new TextElement(() => on() ? _loc.T("style.on") : _loc.T("style.off"), Color: () => Label(),
                Width: OnOffChipWidth, Align: TextAlign.Center, NoWrap: true, FontSize: 12) { Bold = true },
            Fill, hover, () => { if (En()) set(!on()); }, padX: 0f), Width: OnOffChipWidth);
    }

    // One option of a choice group (accent on) — or, with `color`, a kind chip (Debuffs red / Buffs green). Hugs its
    // label (+ 9 px each side); the label never changes, and the window is rebuilt on a language change, so the width is
    // stable. Optional `enabled` → recessed while the parent setting is off.
    private HudElement ChoiceChip(Func<string> label, Func<bool> active, Action onClick, Func<bool>? enabled = null,
        Func<ColorRgba>? color = null)
    {
        var hover = new HoverFlag();
        bool En() => enabled == null || enabled();
        ColorRgba Hue() => color != null ? color() : AccentColor();
        float H() => hover.On ? ChipHoverAlpha : 0f;
        ColorRgba Fill() => !En()
            ? active() ? Over(Hue() with { A = 0.16f }, DisabledFill(PanelColor())) : DisabledFill(PanelColor())
            : active() ? Over(TextColor() with { A = H() }, Over(Hue() with { A = ChipWashAlpha }, PanelColor()))
            : Over(TextColor() with { A = ChipOffAlpha + H() }, PanelColor());
        ColorRgba Label() => !En() ? DisabledLabel(PanelColor()) : active() ? Readable(Hue(), Fill()) : MutedColor();
        return ChipShell(new TextElement(label, Color: () => Label(), NoWrap: true, FontSize: 12) { Bold = true },
            Fill, hover, () => { if (En()) onClick(); }, padX: ChipPadX);
    }

    private HudElement ChipShell(HudElement label, Func<ColorRgba> fill, HoverFlag hover, Action onClick, float padX)
        => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ChipHeight),   // width 0 + height > 0 = a non-flexible height strut
                DropBold(label),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(fill()), CornerRadius = RoundRadius(ChipHeight), PaddingX = padX, PaddingY = 0f,
        };

    // Estimated drawn width of a bold-12 label (SDL §6 budget: Latin ≈ 7.5 px/char, CJK ≈ 12.5, Thai ≈ 8 per code point)
    // — for laying chips out at BUILD time only (wrapping a long chip group onto a second line). Never per refresh.
    private static float EstimateChipWidth(string label)
    {
        float w = 0f;
        foreach (char c in label) w += c >= 0x2E80 ? 12.5f : c is >= '฀' and <= '๿' ? 8f : 7.5f;
        return w + 2f * ChipPadX;
    }

    // ── segmented tab strip ───────────────────────────────────────────────────────────────────────────────────────
    // A recessed Track bubble (r = RoundRadius(30)) holding 24 px round segments: active = SOLID accent + contrast label,
    // inactive = muted label on nothing (hover Text@0.08). Bold in both states → a switch never resizes a tab. Hugs left.
    private const float TabSegH = 24f, TabTrackPad = 3f;

    private HudElement TabStrip(IReadOnlyList<Func<string>> labels, Func<int> active, Action<int> select)
    {
        var tabs = new HudElement[labels.Count];
        for (int i = 0; i < labels.Count; i++)
        {
            int idx = i;
            var hover = new HoverFlag();
            tabs[i] = new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, TabSegH),
                    DropBold(new TextElement(labels[i],
                        Color: () => active() == idx ? SolidLabel(AccentColor()) : hover.On ? TextColor() : MutedColor(),
                        NoWrap: true, FontSize: 13) { Bold = true }),
                }, Gap: 0.01f, Justify: RowJustify.Center),
                OnClick: () => select(idx)) { Bare = true, OnHover = over => hover.On = over })
            {
                FillColorFn = () => active() == idx ? Fade(AccentColor())
                                  : hover.On ? Fade(Over(TextColor() with { A = ChipHoverAlpha }, TrackColor())) : Transparent,
                CornerRadius = RoundRadius(TabSegH), PaddingX = 12f, PaddingY = 0f,
            };
        }
        return new RowElement(new HudElement[]
        {
            new BubbleElement(new RowElement(tabs, Gap: 4f))
            {
                FillColorFn = () => Fade(TrackColor()), CornerRadius = RoundRadius(TabSegH + 2f * TabTrackPad),
                PaddingX = TabTrackPad, PaddingY = TabTrackPad,
            },
            new SpacerElement(0f),   // flexible: the track hugs its tabs at the left
        }, Gap: 0.01f);
    }

    // ── round icon button (28 × 28) ───────────────────────────────────────────────────────────────────────────────
    // Bubble (r13, transparent at rest) → Bare Selectable → centred PLAIN glyph (a bold "✕" can draw blank, SDL §6).
    // `remove` = the house "Remove hover": red wash + #ff8a8b glyph; otherwise hover = Text@0.10.
    private const float IconButtonSize = 28f;
    private static readonly ColorRgba RemoveWash = Rgb(0xff4d4f, 0.22f);

    private HudElement IconButton(Func<string> glyph, Action onClick, bool remove = false)
    {
        var hover = new HoverFlag();
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, IconButtonSize),
                new TextElement(glyph, Color: () => hover.On ? (remove ? Readable(ProblemText, Over(RemoveWash, PanelColor())) : TextColor())
                                                             : MutedColor(),
                    Width: IconButtonSize, Align: TextAlign.Center, NoWrap: true) { CenterGlyphs = true },
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => !hover.On ? Transparent : Fade(remove ? Over(RemoveWash, PanelColor()) : HoverFill(PanelColor())),
            CornerRadius = RoundRadius(IconButtonSize), PaddingX = 0f, PaddingY = 0f, MaxWidth = IconButtonSize,
        }, Width: IconButtonSize);
    }

    // ── pill dropdown + round-knob slider ─────────────────────────────────────────────────────────────────────────
    // The framework draws the Pill dropdown (28 px Neutral capsule, bold caption, muted ▾; fw ≥2.29 for the radius).
    // Fill / hover / label are passed so it fades with the window like every other fill (FillColorFn = final colour).
    private DropdownElement PillDropdown(Func<int> selected, Func<IReadOnlyList<string>> options, Action<int> onSelect,
        float width) => new(selected, options, onSelect, Width: width)
    {
        Style = DropdownStyle.Pill, CornerRadius = RoundRadius(PillHeight),
        FillColorFn = () => Fade(NeutralFill()),
        HoverFillColorFn = () => Fade(Over(TextColor() with { A = 0.08f }, NeutralFill())),
        LabelColorFn = () => NeutralText(),
    };

    // SquareHandle = a knob exactly HandleSize square (the default handle stretches into a capsule).
    private const float SliderKnobSize = 14f;

    private static SliderElement RoundSlider(Func<float> get, Action<float> set, float min, float max,
        Func<bool>? enabled = null) => new(get, set, min, max, enabled) { SquareHandle = true, HandleSize = SliderKnobSize };

    // ── setting row ───────────────────────────────────────────────────────────────────────────────────────────────
    // ONE 28 px row: [label (weighted, ellipsized)] [controls…] [round "?"]. The "?" is the LAST child of every row so the
    // "?"s line up in the panel's right-edge column (rule 17). `child` = a sub-setting of the row above: indented 12 px
    // and muted (its controls take their own `enabled` → recessed while the parent is off).
    private const float SettingRowH = 28f;
    private const float ChildIndent = 12f;

    private HudElement SettingRow(Func<string> label, bool child, params HudElement[] controls)
    {
        var items = new List<HudElement>(controls.Length + 3) { new SpacerElement(0f, SettingRowH) };
        if (child) items.Add(new SpacerElement(ChildIndent - 6f, 1f));
        items.Add(new CellElement(new TextElement(label, Color: () => child ? MutedColor() : TextColor(), NoWrap: true)
            { Ellipsize = true }, Weight: 1f));
        items.AddRange(controls);
        return new RowElement(items, Gap: 6f);
    }

    // ONE always-present line at a fixed height: a 0-width height strut pins the row so bold / empty / changing text
    // never changes the panel height; the content sits in a weighted cell so it ellipsizes instead of widening.
    private static HudElement FixedLine(HudElement content, float height = SettingRowH) => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, height),
        new CellElement(content, Weight: 1f),
    }, Gap: 0.01f);

    // ── icon tile + tool header ───────────────────────────────────────────────────────────────────────────────────
    // The CombatMeter mark: a 40 px tile in the LIVE accent with two flat meter bars drawn as bubbles (no PNG, so it is
    // crisp at every UI scale and the bars take the solid-fill label colour — white, or near-black on a light accent).
    private const float ToolIconSize = 40f;

    private HudElement MeterIconTile(float size = ToolIconSize)
    {
        float barW = MathF.Round(size * 0.15f), barH = MathF.Round(size * 0.4f), gap = MathF.Round(size * 0.1f);
        HudElement Bar() => new BubbleElement(new SpacerElement(barW, barH))
        {
            FillColorFn = () => SolidLabel(AccentColor()), CornerRadius = 2f, PaddingX = 0f, PaddingY = 0f,
        };
        return new CellElement(new BubbleElement(new RowElement(new HudElement[] { Bar(), Bar() }, Gap: gap))
        {
            FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(size * 0.22f),
            PaddingX = (size - 2f * barW - gap) / 2f, PaddingY = (size - barH) / 2f,
        }, Width: size);
    }

    // Window title block: [40 px icon] [bold title over a live status line]. The text column sits in a weighted Cell so a
    // long status ellipsizes instead of widening the window.
    private HudElement ToolHeader(HudElement icon, Func<string> title, HudElement status) => new RowElement(new HudElement[]
    {
        icon,
        new CellElement(new ColumnElement(new HudElement[]
        {
            new TextElement(title, Color: () => TextColor(), Emphasis: true, NoWrap: true) { Ellipsize = true },
            status,
        }, Gap: 2f), Weight: 1f),
    }, Gap: 10f);

    // An 8 px round dot (radius 3 = RoundRadius(8)) in a live state colour — a bubble, not a "●" glyph, so its size
    // doesn't depend on the font fallback.
    private const float DotSize = 8f;

    private HudElement StatusDot(Func<ColorRgba> color) => new CellElement(new BubbleElement(new SpacerElement(DotSize, DotSize))
    {
        FillColorFn = () => Fade(color()), CornerRadius = RoundRadius(DotSize), PaddingX = 0f, PaddingY = 0f,
        MaxWidth = DotSize,
    }, Width: DotSize);

    // ── list tiles ────────────────────────────────────────────────────────────────────────────────────────────────
    // ⚠️ A VirtualList slot STRETCHES its one child to the full RowHeight, so a 26 px tile in a 28 px slot filled all 28
    // and the rows touched (Maestro, 2026-11-06). Each pooled row is ListSlot(tile) = [26 px tile, 2 px spacer].
    private const float ListTileHeight = 26f;
    private const float ListRowGap     = 2f;
    private const float ListRowPitch   = ListTileHeight + ListRowGap;   // the VirtualList RowHeight
    private const float TileRadius     = 6f;

    private static HudElement ListSlot(HudElement tile) => new ColumnElement(new HudElement[]
    {
        tile,
        new SpacerElement(0f, ListRowGap),
    }, Gap: 0.01f);

    // Viewport height that shows exactly `rows` rows: the last row's 2 px gap falls outside, so the list ends flush.
    private static float ListViewportHeight(int rows) => rows * ListRowPitch - ListRowGap;

    // ── format cache ──────────────────────────────────────────────────────────────────────────────────────────────
    // A live count / time readout polled every refresh must not format a new string each poll (Plugin-Performance-Rules
    // §2): re-format only when the format string (language) or the numbers change.
    private sealed class FmtCache
    {
        private string? _fmt;
        private long _a = long.MinValue, _b = long.MinValue;
        private string _text = "";

        public string Get(string fmt, long a, long b = 0)
        {
            if (ReferenceEquals(fmt, _fmt) && a == _a && b == _b) return _text;
            _fmt = fmt; _a = a; _b = b;
            try { _text = string.Format(System.Globalization.CultureInfo.InvariantCulture, fmt, a, b); }
            catch (FormatException) { _text = fmt; }
            return _text;
        }
    }
}
