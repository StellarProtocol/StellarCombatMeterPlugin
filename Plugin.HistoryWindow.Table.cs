using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Combat History — the player table panel (approved redesign §4): a muted UPPERCASE header (PLAYER / DMG / DPS / %),
// then 34 px rounded tiles in a virtual list: class-colour share wash, 18 px crest, bold "#1 Name" + muted spec, the
// three metric numbers, a round ⌕ inspect button (only when a snapshot exists — the slot is kept otherwise, so the
// columns never shift) and a round › drill-in (‹ while its breakdown is open). A click on the tile toggles that
// player's chart line; a charted row wears a ring in its line colour.
//
// The share wash is the framework AccentRowElement in its fw 2.33.0 rounded mode (CornerRadius = the tile radius → no
// stripe, left corners follow the tile, straight right cut) at the approved 22 % wash.
public sealed partial class Plugin
{
    private const float TblTileH   = 34f;
    private const float TblPitch   = TblTileH + ListRowGap;
    private const int   TblRows    = 5;                          // visible rows (scroll for more)
    private const float TblColAmt  = 64f, TblColRate = 56f, TblColPct = 48f;
    private const float TblBtn     = IconButtonSize;             // 28 px round buttons
    private const float TblCrest   = 18f;
    private const float TblWashAlpha = 0.22f;                    // approved mockup `.trow .sh` opacity
    private const float AccentRowPadX = 6f, AccentRowPadY = 3f;  // AccentRowElement's own padding (WindowBuilder.MeterRow)

    private int _srcOffset;   // first table row shown by the virtual list
    private readonly UvRect[] _tblCrestUv = NewUvRects(TblRows + 2);

    // Upper-cased header labels, re-cased only when the source string changes (a language / metric switch).
    private sealed class UpperCache
    {
        private string? _src;
        private string _up = "";
        public string Get(string s)
        {
            if (!ReferenceEquals(s, _src)) { _src = s; _up = s.ToUpperInvariant(); }
            return _up;
        }
    }

    private readonly UpperCache _hdrPlayer = new(), _hdrAmt = new(), _hdrRate = new();

    private HudElement BuildTablePanel()
    {
        var pool = new HudElement[TblRows + 2];
        for (int i = 0; i < pool.Length; i++) pool[i] = TableSlot(i);
        // Header + list in their own column at PanelGap − 2: the list viewport is 2 px taller (rows × pitch, RingListSlot
        // keeps a charted row's ring off the clip edges), so the panel — and the fixed-height window — stay the same height.
        return PanelBox(new HudElement[] { new ColumnElement(new HudElement[]
        {
            // Header columns line up with the tile columns: the tile's AccentRow pads 6 px each side, so the PLAYER caption
            // starts at the crest (6 + 1 px strut + 6 gap) and the right spacer covers both buttons + the 6 px pad.
            new RowElement(new HudElement[]
            {
                new SpacerElement(AccentRowPadX + 1f, 16f),
                new CellElement(HeaderText(() => _hdrPlayer.Get(_loc.T("history.col.player")), 0f, TextAlign.Left), Weight: 1f),
                HeaderText(() => _hdrAmt.Get(MetricColumnLabel(_historyMetric)), TblColAmt, TextAlign.Right),
                HeaderText(() => _hdrRate.Get(MetricRateLabel(_historyMetric)), TblColRate, TextAlign.Right),
                HeaderText(() => "%", TblColPct, TextAlign.Right),
                new SpacerElement(2f * TblBtn + 6f + AccentRowPadX, 1f),
            }, Gap: 6f),
            new VirtualListElement(
                Count:     () => _sessionRows.Count,
                RowHeight: TblPitch, Pool: pool,
                OnWindow:  i => _srcOffset = i, Height: TblRows * TblPitch),
        }, Gap: PanelGap - ListRowGap) });
    }

    private HudElement HeaderText(Func<string> text, float width, TextAlign align)
    {
        var t = new TextElement(text, Color: () => MutedColor(), Width: width, Align: align, NoWrap: true, FontSize: 11)
            { Bold = true, Ellipsize = true };
        return width > 0f ? new CellElement(t, Width: width) : t;
    }

    private int TblRow(int slot) { int i = _srcOffset + slot; return i >= 0 && i < _sessionRows.Count ? i : -1; }

    private string TblText(int slot, Func<SourceRow, string> sel) => TblRow(slot) is var i and >= 0 ? sel(_sessionRows[i]) : "";

    private HudElement TableSlot(int slot)
    {
        var hover = new HoverFlag();
        var row = new RowElement(new HudElement[]
        {
            new SpacerElement(1f, TblTileH - 2f * AccentRowPadY),   // height strut → a 34 px tile
            new CellElement(new RowElement(new HudElement[]
                {
                    new GameTextureElement(() => TblCrestTex(slot), (int)TblCrest, (int)TblCrest, () => _tblCrestUv[slot])
                        { CornerRadius = 4 },
                }, Gap: 0.01f, Justify: RowJustify.Center), Width: TblCrest),
            new CellElement(new RowElement(new HudElement[]
            {
                new TextElement(() => TblText(slot, r => r.Label), Color: () => TextColor(), NoWrap: true, FontSize: 13)
                    { Bold = true, Ellipsize = true },
                new CellElement(new TextElement(() => TblText(slot, r => r.Spec), Color: () => MutedColor(), NoWrap: true)
                    { Ellipsize = true }, Weight: 1f),
            }, Gap: 6f), Weight: 1f),
            TblNum(() => TblText(slot, r => r.Dmg), TblColAmt),
            TblNum(() => TblText(slot, r => r.Dps), TblColRate),
            TblNum(() => TblText(slot, r => r.Pct), TblColPct),
            new CellElement(new ConditionalElement(() => InspectAvailable(TblRow(slot)),
                InspectButton(slot), new SpacerElement(TblBtn, TblBtn)), Width: TblBtn),
            DrillButton(slot),
        }, Gap: 6f);
        var wash = new AccentRowElement(row,
            () => TblRow(slot) is var i and >= 0 ? _sessionRows[i].Wash : default,
            () => TblRow(slot) is var i and >= 0 ? _sessionRows[i].Share : 0f)
            { CornerRadius = TileRadius, WashAlpha = TblWashAlpha };
        var tile = new BubbleElement(new SelectableElement(wash,
            OnClick: () => { if (TblRow(slot) is var i and >= 0) ToggleChartSource(_sessionRows[i].Id); })
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(hover.On ? HoverFill(TileColor()) : TileColor()),
            OutlineColorFn = () => TblOutline(slot),
            CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
        return RingListSlot(tile);
    }

    private bool TblCharted(int slot) => TblRow(slot) is var i and >= 0 && _chartedSources.Contains(_sessionRows[i].Id);

    // A charted row wears its LINE colour as the ring (so the row ↔ line pairing reads at a glance); accent only until
    // the chart has rebuilt its series with this row in it.
    private ColorRgba? TblOutline(int slot)
    {
        if (!TblCharted(slot)) return null;
        var line = ChartColorOf(_sessionRows[TblRow(slot)].Id) ?? AccentColor();
        return Fade(line with { A = 0.75f });
    }

    private object? TblCrestTex(int slot)
    {
        int i = TblRow(slot);
        int prof = i >= 0 ? _sessionRows[i].ProfId : 0;
        return prof > 0 ? _services.GameAssets.LoadProfessionIcon(prof, out _tblCrestUv[slot]) : null;
    }

    private static HudElement TblNum(Func<string> text, float width) => new CellElement(
        new TextElement(text, Width: width, Align: TextAlign.Right, NoWrap: true, FontSize: 13) { Bold = true }, Width: width);

    // Round ⌕ inspect (28 × 28): the baked white magnifier PNG, tinted muted → text on hover; accent wash while its
    // Session Snapshot is open.
    private HudElement InspectButton(int slot)
    {
        var hover = new HoverFlag();
        bool Open() => InspectOpen(TblRow(slot));
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(6f, TblBtn),
                new ImageElement(() => _inspectIconPng, 16, 16)
                    { Tint = () => hover.On || Open() ? TextColor() : MutedColor() },
                new SpacerElement(6f, 1f),
            }, Gap: 0.01f),
            OnClick: () => InspectRow(TblRow(slot))) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Open() ? Fade(Over(AccentColor() with { A = 0.35f }, TileColor()))
                              : hover.On ? Fade(HoverFill(TileColor())) : Transparent,
            CornerRadius = RoundRadius(TblBtn), PaddingX = 0f, PaddingY = 0f, MaxWidth = TblBtn,
        };
    }

    // Round › drill-in (28 × 28) on a faint fill; ‹ + accent wash while this player's Skill Breakdown is open.
    private HudElement DrillButton(int slot)
    {
        var hover = new HoverFlag();
        bool Open() => DrillOpen(TblRow(slot));
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, TblBtn),
                new TextElement(() => Open() ? "‹" : "›", Color: () => hover.On || Open() ? TextColor() : MutedColor(),
                    Width: TblBtn, Align: TextAlign.Center, NoWrap: true, FontSize: 16) { Bold = true, CenterGlyphs = true },
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => DrillIn(TblRow(slot))) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(Open() ? Over(AccentColor() with { A = 0.35f }, TileColor())
                : Over(TextColor() with { A = hover.On ? 0.14f : 0.08f }, TileColor())),
            CornerRadius = RoundRadius(TblBtn), PaddingX = 0f, PaddingY = 0f, MaxWidth = TblBtn,
        }, Width: TblBtn);
    }
}
