using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// House-kit pieces for the meter HUD and its popovers (approved redesign 2026-11-17 §1–§2,
// _scratch\prototypes\combatmeter-redesign-APPROVED.md). The HUD floats over the game world with no window frame, so its
// controls carry their OWN faint fill at rest (the prototype's `.icb.f`) instead of the transparent-at-rest icon button
// the framed windows use (Plugin.Style.Controls.cs IconButton). Every fill is theme-live and fades with the window opacity
// (SDL §5); pictures and text stay solid.
//
// Performance (Plugin-Performance-Rules.md §7): these are built ONCE; every Func below is struct maths on the live theme or
// returns a cached string — nothing allocates per refresh.
public sealed partial class Plugin
{
    private const float HudControlH = 28f;    // header row / icon buttons / dropdown
    private const float HudIconPx   = 16;     // glyph inside a 28 px round icon button
    private const float HudSegH     = 24f;    // List | Party segment (r11) inside a 28 px track (r13)
    private const float HudSegPad   = 2f;

    // A HUD control's rest fill: MenuText @8 % over the panel colour (opaque, then faded) — a faint raised chip that still
    // reads over the world. Hover lifts it to 14 %.
    private ColorRgba HudRestFill()  => Over(TextColor() with { A = 0.08f }, PanelColor());
    private ColorRgba HudHoverFill() => Over(TextColor() with { A = 0.14f }, PanelColor());

    // ── round 28 × 28 icon button with a flat-white PNG glyph (tinted muted / text on hover) ─────────────────────────
    // `onRect` (optional) receives the button's SCREEN-pixel rect on click, for anchoring a popover under it.
    private HudElement HudIconButton(Func<byte[]?> png, Action onClick, Action<WindowRect>? onRect = null)
        => HudStateIconButton(png, static (p, hover) => hover ? p.HudHoverFill() : p.HudRestFill(),
            static (p, hover) => hover ? p.TextColor() : p.MutedColor(), onClick, onRect, live: false);

    // The same button with a STATE-driven look (Pause ↔ Resume amber, Archive ↔ flash green / recessed): `fill` and
    // `tint` get the hover flag and return the un-faded fill / glyph colour; `live` makes the glyph follow `png` (the
    // framework's byte[]-reference poll — return cached arrays only, so a state flip only repaints). The delegates are
    // static (this passed in) so no closure beyond the hover flag is created, and only at build time.
    private HudElement HudStateIconButton(Func<byte[]?> png, Func<Plugin, bool, ColorRgba> fill,
        Func<Plugin, bool, ColorRgba> tint, Action onClick, Action<WindowRect>? onRect = null, bool live = true)
    {
        var hover = new HoverFlag();
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, HudControlH),
                new ImageElement(png, (int)HudIconPx, (int)HudIconPx) { Tint = () => tint(this, hover.On), Live = live },
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over, OnClickWithRect = onRect })
        {
            FillColorFn = () => Fade(fill(this, hover.On)),
            CornerRadius = RoundRadius(HudControlH), PaddingX = (HudControlH - HudIconPx) / 2f, PaddingY = 0f,
            MaxWidth = HudControlH,
        }, Width: HudControlH);
    }

    // The same 28 × 28 round shape, RECESSED (rule 22: disabled = sunken fill + faint label, no ring) and showing a short
    // live label — the ready-check button while its cooldown runs ("18"). `label` must return a cached string.
    private HudElement HudRecessedLabel(Func<string> label) => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, HudControlH),
            new TextElement(label, Color: () => DisabledLabel(PanelColor()), Width: HudControlH, Align: TextAlign.Center,
                NoWrap: true, FontSize: 12) { Bold = true, CenterGlyphs = true },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => Fade(DisabledFill(PanelColor())),
        CornerRadius = RoundRadius(HudControlH), PaddingX = 0f, PaddingY = 0f, MaxWidth = HudControlH,
    }, Width: HudControlH);

    // ── List | Party segmented control ──────────────────────────────────────────────────────────────────────────────
    // A 28 px Track bubble (r13, 2 px inset) holding 24 px segments (r11): active = SOLID accent + contrast label, inactive
    // = muted label (hover Text@8 %). Both states bold 12, so a switch never resizes a segment (R10).
    private HudElement HudSegmented(IReadOnlyList<Func<string>> labels, Func<int> active, Action<int> select)
    {
        var segs = new HudElement[labels.Count];
        for (int i = 0; i < labels.Count; i++)
        {
            int idx = i;
            var hover = new HoverFlag();
            segs[i] = new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, HudSegH),
                    DropBold(new TextElement(labels[i],
                        Color: () => active() == idx ? SolidLabel(AccentColor()) : hover.On ? TextColor() : MutedColor(),
                        NoWrap: true, FontSize: 12) { Bold = true }),
                }, Gap: 0.01f, Justify: RowJustify.Center),
                OnClick: () => select(idx)) { Bare = true, OnHover = over => hover.On = over })
            {
                FillColorFn = () => active() == idx ? Fade(AccentColor())
                                  : hover.On ? Fade(Over(TextColor() with { A = ChipHoverAlpha }, TrackColor())) : Transparent,
                CornerRadius = RoundRadius(HudSegH), PaddingX = 10f, PaddingY = 0f,
            };
        }
        return new BubbleElement(new RowElement(segs, Gap: HudSegPad))
        {
            FillColorFn = () => Fade(TrackColor()), CornerRadius = RoundRadius(HudControlH),
            PaddingX = HudSegPad, PaddingY = HudSegPad,
        };
    }

    // ── empty state ─────────────────────────────────────────────────────────────────────────────────────────────────
    // ONE row-sized slot (owner 2026-11-17: "only the size of one player slot, more transparent"): the 48 px height and
    // r6 of a list meter row (fw WindowBuilder.MeterRow MeterRowHeight / MeterRoundTileR), on one line — bold title, then
    // the optional muted line after a "·", ellipsized when narrow (it sits in a weighted cell). The 0-width height strut
    // pins the row at 48 whatever the text. The caller drops it at the top of the list area; the rest stays empty.
    // The fill is the RECESSED family (black @28 % over the panel) at only EmptySlotAlpha of its old opacity, × window
    // opacity, so the game shows through like the empty party slots. Texts are catalog lookups / a cached composite.
    private const float EmptySlotH = 48f;
    private const float EmptySlotAlpha = 0.4f;

    private ColorRgba EmptySlotFill()
    {
        var c = DisabledFill(PanelColor());
        return Fade(c with { A = c.A * EmptySlotAlpha });
    }

    private HudElement EmptyStateSlot(Func<string> title, Func<string> line) => new BubbleElement(new RowElement(new HudElement[]
    {
        new SpacerElement(0f, EmptySlotH),
        new TextElement(title, Color: () => TextColor(), NoWrap: true, FontSize: 13) { Bold = true },
        new CellElement(new ConditionalElement(() => line().Length > 0,
            new TextElement(line, Color: () => MutedColor(), NoWrap: true, FontSize: 12) { RichEmoji = true, Ellipsize = true }),
            Weight: 1f),
    }, Gap: 6f))
    {
        FillColorFn = () => EmptySlotFill(), CornerRadius = 6f, PaddingX = 12f, PaddingY = 0f,
    };

    // ── house popover shell ─────────────────────────────────────────────────────────────────────────────────────────
    // The prototype's `.pop`: window background, 1 px accent @45 % ring, r6. The popovers are Borderless windows (whose
    // root adds a fixed 12 px transparent margin — WindowBuilder.Chrome.cs), so this bubble IS the visible popover.
    private const float PopoverMargin = 12f;   // the Borderless window's own content padding around the shell

    private HudElement PopoverShell(float pad, float gap, IReadOnlyList<HudElement> children)
        => new BubbleElement(new ColumnElement(children, Gap: gap))
        {
            FillColorFn = () => Fade(Menu.MenuBackground),
            OutlineColorFn = () => Fade(AccentColor() with { A = 0.45f }),
            CornerRadius = 6f, PaddingX = pad, PaddingY = pad,
        };

    // A small muted secondary line (12 px, regular weight). Plain TextElement ignores FontSize on Menu windows (fixed
    // 14 px); RichEmoji switches it to the regular TMP face, which honours FontSize (SDL §6).
    private static HudElement SmallMuted(Func<string> text, Func<ColorRgba?> color, bool wrap = false)
        => new TextElement(text, Color: color, NoWrap: !wrap, FontSize: 12) { RichEmoji = true, Ellipsize = !wrap };

    // ── number labels ───────────────────────────────────────────────────────────────────────────────────────────────
    // Small non-negative integers as cached strings (ranks, seconds, counts) — no per-refresh int.ToString().
    private static readonly string[] SmallNumbers = BuildSmallNumbers(100);

    private static string[] BuildSmallNumbers(int n)
    {
        var a = new string[n + 1];
        for (int i = 0; i <= n; i++) a[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return a;
    }

    private static string SmallNumber(int v)
        => (uint)v < (uint)SmallNumbers.Length ? SmallNumbers[v] : v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // "41%" for 0..100 — the meter row's share label, cached (it was a fresh interpolated string per row per refresh).
    private static readonly string[] SharePercentLabels = BuildSharePercents();

    private static string[] BuildSharePercents()
    {
        var a = new string[101];
        for (int i = 0; i <= 100; i++) a[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
        return a;
    }

    private static string SharePercentLabel(double share)
    {
        int p = (int)Math.Round(share * 100d, MidpointRounding.AwayFromZero);
        return SharePercentLabels[Math.Clamp(p, 0, 100)];
    }
}
