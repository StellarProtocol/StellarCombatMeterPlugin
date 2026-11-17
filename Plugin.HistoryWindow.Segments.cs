using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;   // UploadPhase

namespace Stellar.CombatMeter;

// Combat History SEGMENTS cards — approved addendum "run panel B" (2026-11-17). One card per archive of the selected run,
// oldest first, so a grouped run row can be drilled into without putting every archive back in the list:
//   36 px, r6, Tile fill · bold "N · <reason>" (readable reason, SegmentLabel) · muted "<start m:ss> · <length>" ·
//   state dot top-right (green = uploaded, amber = uploading, hollow muted ring = not yet). Selected = accent wash + ring.
// Duplicate reasons are expected and honest (two run-end stages really are two "Stage" segments); the number tells them
// apart. Columns fit the width (≥ SegCardMinW each); up to two rows show in full with no scrollbar, a third row makes the
// card area scroll, so the chart and table never move further down. A single-segment run hides the area — its height
// stays reserved, exactly (rule 14: the window never grows or shrinks with the selection).
public sealed partial class Plugin
{
    // Card slots (built once, polled). A real RAID banks 13 archives per session (measured 2026-08-25, Clash! Field of
    // Forgotten Illusions ×2: prepare/bosskill/cut per stage); 24 covers that with room.
    private const int MaxSegmentChips = 24;

    private const float SegCardH      = 36f;
    private const float SegCardMinW   = 125f;
    private const float SegCardGap    = 6f;
    private const float SegPitch      = SegCardH + SegCardGap;   // the VirtualList RowHeight: a card row + its gap below
    private const int   SegRowsShown  = 2;
    private const float SegScrollLane = 12f;   // the viewport's scrollbar lane (framework inset 9 px + slack)
    private const float SegHeadH      = 18f;
    // Two WHOLE pitches (2 × 42 = 84), not 2 × 36 + 6 = 78: the virtual list's content is Count × RowHeight, the last
    // row's 6 px gap included, so 78 was short of two rows → the AutoHide scrollbar showed with only 2 rows and the
    // clamped scroll sat at the bottom, cutting the first row's top edge (2026-11-17). With 84 two rows fit exactly
    // (the last gap is empty space under them); a third row overflows by a whole pitch and scrolls.
    private const float SegGridH      = SegRowsShown * SegPitch;
    // Header + gap + grid, exactly: FixedBox adds nothing, so both branches below are this tall (rule 14).
    private const float SegBlockH     = SegHeadH + PanelGap + SegGridH;

    // Card texts, rebuilt on selection / language change only (RebuildSegmentTexts).
    private readonly string[] _segTitles = new string[MaxSegmentChips];
    private readonly string[] _segTimes  = new string[MaxSegmentChips];

    private int _segRowOffset;        // first card ROW shown by the virtual list
    private int[]? _segListRun;       // the segment array the list last showed (a new run → scroll back to the top)

    private int SegShown => Math.Min(_selectedSegments.Length, MaxSegmentChips);

    private HudElement BuildSegmentArea() => new ConditionalElement(() => _selectedSegments.Length > 1,
        FixedBox(SegBlockH, SegmentHeader(), SegmentGrid()),
        FixedBox(SegBlockH));

    private readonly UpperCache _segCaption = new();

    // SEGMENTS (bold 11 uppercase muted) … legend "● uploaded · ○ not yet". The legend dots are the SAME bubbles the cards
    // use (not "●"/"○" glyphs, whose look depends on the font fallback).
    private HudElement SegmentHeader() => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, SegHeadH),
        new TextElement(() => _segCaption.Get(_loc.T("history.segments")), Color: () => MutedColor(), NoWrap: true, FontSize: 11)
            { Bold = true },
        new SpacerElement(0f),   // flexible: the legend sits at the right edge
        StatusDot(() => SuccessColor()),
        SegHint(() => _loc.T("history.segments.uploaded")),
        SegHint(() => "·"),
        HollowDot(),
        SegHint(() => _loc.T("history.segments.notYet")),
    }, Gap: 4f);

    private HudElement SegHint(Func<string> text)
        => new TextElement(text, Color: () => MutedColor(), NoWrap: true, FontSize: 11) { RichEmoji = true };

    // The "not uploaded yet" dot: an 8 px muted RING (outline, no fill).
    private HudElement HollowDot() => new CellElement(new BubbleElement(new SpacerElement(DotSize, DotSize))
    {
        FillColorFn = () => Transparent, OutlineColorFn = () => Fade(MutedColor()),
        CornerRadius = RoundRadius(DotSize), PaddingX = 0f, PaddingY = 0f, MaxWidth = DotSize,
    }, Width: DotSize);

    private HudElement SegmentGrid()
    {
        float avail = HistDetailW - 2f * PanelPadX - SegScrollLane;
        int cols = Math.Max(1, (int)((avail + SegCardGap) / (SegCardMinW + SegCardGap)));
        float cardW = MathF.Floor((avail - (cols - 1) * SegCardGap) / cols);
        // A virtual list of card ROWS (pool = the 2 visible + 2), not a ScrollElement over a ListElement: each pooled
        // slot is exactly RowHeight tall whatever its cards measure, so the content height is exactly rows × pitch (the
        // old list summed the cards' own heights, and the 0.01 gaps inside a card already made a row 36.02 tall), and
        // ResetScroll puts a newly selected run back at its first row.
        var pool = new HudElement[SegRowsShown + 2];
        for (int line = 0; line < pool.Length; line++)
        {
            var cells = new HudElement[cols];
            for (int c = 0; c < cols; c++)
            {
                int ln = line, col = c;
                int Slot() => (_segRowOffset + ln) * cols + col;   // card index this cell shows now
                cells[c] = new CellElement(new ConditionalElement(() => Slot() < SegShown,
                    SegmentCard(Slot, cardW)), Width: cardW);
            }
            // Row + the 6 px gap below it = one 42 px pitch (a VirtualList slot stretches its child to RowHeight).
            pool[line] = new ColumnElement(new HudElement[]
            {
                new RowElement(cells, Gap: SegCardGap),
                new SpacerElement(0f, SegCardGap),
            }, Gap: 0.01f);
        }
        return new VirtualListElement(
            Count:     () => (SegShown + cols - 1) / cols,
            RowHeight: SegPitch, Pool: pool,
            OnWindow:  i => _segRowOffset = i, Height: SegGridH)
        {
            FixedHeight = true,   // never squeezed by the fixed-height window
            ResetScroll = () =>
            {
                if (ReferenceEquals(_segListRun, _selectedSegments)) return false;   // same run: keep the scroll
                _segListRun = _selectedSegments;
                return true;
            },
        };
    }

    // One card. The texts get a FIXED width (card − insets − dot) so the bubble is exactly cardW wide whatever the label
    // (a Bubble hugs its content inside a Row). `slot` = the card index the pooled cell shows now (it moves with the scroll).
    private HudElement SegmentCard(Func<int> slot, float cardW)
    {
        const float inset = 8f, gap = 6f;
        float textW = cardW - 2f * inset - gap - DotSize;
        var hover = new HoverFlag();
        bool Selected() => slot() is var s && s < SegShown && _selectedSegments[s] == _historyIndex;
        ColorRgba Fill()
        {
            var f = Selected() ? Over(AccentColor() with { A = SelectedWashMix }, TileColor()) : TileColor();
            return hover.On ? HoverFill(f) : f;
        }
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(inset - gap, SegCardH),   // height strut + (with the gap) the left inset
                new ColumnElement(new HudElement[]
                {
                    new TextElement(() => SegAt(_segTitles, slot()), Color: () => TextColor(), Width: textW, NoWrap: true, FontSize: 12)
                        { Bold = true, Ellipsize = true },
                    new TextElement(() => SegAt(_segTimes, slot()), Color: () => MutedColor(), Width: textW, NoWrap: true, FontSize: 11)
                        { RichEmoji = true, Ellipsize = true },
                }, Gap: 0.01f),
                // Dot pinned to the top-right: a column of [6 px, dot, filler] the card's height.
                new ColumnElement(new HudElement[]
                {
                    new SpacerElement(0f, 6f),
                    SegmentDot(slot),
                    new SpacerElement(0f, SegCardH - 6f - DotSize - 0.02f),   // − the two 0.01 gaps: exactly 36
                }, Gap: 0.01f),
                new SpacerElement(inset - gap, 1f),          // right inset
            }, Gap: gap),
            OnClick: () => { if (slot() is var s && s < SegShown) SelectSession(_selectedSegments[s]); })
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(Fill()),
            OutlineColorFn = () => Selected() ? Fade(AccentColor() with { A = 0.75f }) : null,
            CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
    }

    private HudElement SegmentDot(Func<int> slot) => new ConditionalElement(
        () => PhaseAt(slot()) is UploadPhase.Done or UploadPhase.InFlight,
        StatusDot(() => PhaseAt(slot()) == UploadPhase.Done ? SuccessColor() : CautionColor()),
        HollowDot());

    // Bounds-safe reads for a pooled cell: the last pool row can index past MaxSegmentChips (5 columns × 5 rows = 25 > 24).
    private static string SegAt(string[] a, int s) => (uint)s < (uint)a.Length ? a[s] ?? "" : "";
    private UploadPhase PhaseAt(int s) => (uint)s < (uint)_segPhases.Length ? _segPhases[s] : UploadPhase.Idle;

    /// <summary>"N · Boss kill" and "0:42 · 1m 12s" per card. The start is the offset from the run's first recorded
    /// combat start; the length is the segment's real elapsed span (<see cref="RealDurationMs"/>). Both read
    /// <c>EnteredAtMs</c>, which a legacy archive may lack (0) — that card shows "—" for the missing value.</summary>
    private void RebuildSegmentTexts()
    {
        long runStart = 0;
        foreach (var idx in _selectedSegments)
            if (idx >= 0 && idx < _history.Count && _history[idx].EnteredAtMs > 0
                && (runStart == 0 || _history[idx].EnteredAtMs < runStart))
                runStart = _history[idx].EnteredAtMs;
        for (int i = 0; i < _selectedSegments.Length && i < MaxSegmentChips; i++)
        {
            int idx = _selectedSegments[i];
            if (idx < 0 || idx >= _history.Count) continue;
            var e = _history[idx];
            _segTitles[i] = SmallNumber(i + 1) + " · " + SegmentLabel(e.Trigger);
            var start = runStart > 0 && e.EnteredAtMs > 0 ? FormatSeconds((e.EnteredAtMs - runStart) / 1000f) : "—";
            var length = e.EnteredAtMs > 0 && e.ArchivedAtMs > 0
                ? FormatDurationWithTenths(RealDurationMs(e.EnteredAtMs, e.ArchivedAtMs)) : "—";
            _segTimes[i] = start + " · " + length;
        }
    }
}
