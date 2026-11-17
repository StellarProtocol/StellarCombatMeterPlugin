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
// apart. Columns fit the width (≥ SegCardMinW each); at most two rows show, then the card area scrolls, so the chart and
// table never move further down. A single-segment run hides the area — its height stays reserved (rule 14: the window
// never grows or shrinks with the selection).
public sealed partial class Plugin
{
    // Card slots (built once, polled). A real RAID banks 13 archives per session (measured 2026-08-25, Clash! Field of
    // Forgotten Illusions ×2: prepare/bosskill/cut per stage); 24 covers that with room.
    private const int MaxSegmentChips = 24;

    private const float SegCardH      = 36f;
    private const float SegCardMinW   = 125f;
    private const float SegCardGap    = 6f;
    private const float SegScrollLane = 12f;   // the ScrollElement's scrollbar lane
    private const float SegHeadH      = 18f;
    private const float SegGridH      = 2f * SegCardH + SegCardGap;
    private const float SegBlockH     = SegHeadH + PanelGap + SegGridH;

    // Card texts, rebuilt on selection / language change only (RebuildSegmentTexts).
    private readonly string[] _segTitles = new string[MaxSegmentChips];
    private readonly string[] _segTimes  = new string[MaxSegmentChips];

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
        var lines = new HudElement[(MaxSegmentChips + cols - 1) / cols];
        for (int r = 0; r < lines.Length; r++)
        {
            var cells = new HudElement[cols];
            for (int c = 0; c < cols; c++)
            {
                int slot = r * cols + c;
                cells[c] = new CellElement(new ConditionalElement(() => slot < _selectedSegments.Length,
                    SegmentCard(slot, cardW)), Width: cardW);
            }
            lines[r] = new RowElement(cells, Gap: SegCardGap);
        }
        return new ScrollElement(new ListElement(
            () => Math.Min(lines.Length, (_selectedSegments.Length + cols - 1) / cols), lines) { Gap = SegCardGap },
            SegGridH) { FixedHeight = true };
    }

    // One card. The texts get a FIXED width (card − insets − dot) so the bubble is exactly cardW wide whatever the label
    // (a Bubble hugs its content inside a Row).
    private HudElement SegmentCard(int slot, float cardW)
    {
        const float inset = 8f, gap = 6f;
        float textW = cardW - 2f * inset - gap - DotSize;
        var hover = new HoverFlag();
        bool Selected() => slot < _selectedSegments.Length && _selectedSegments[slot] == _historyIndex;
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
                    new TextElement(() => _segTitles[slot], Color: () => TextColor(), Width: textW, NoWrap: true, FontSize: 12)
                        { Bold = true, Ellipsize = true },
                    new TextElement(() => _segTimes[slot], Color: () => MutedColor(), Width: textW, NoWrap: true, FontSize: 11)
                        { RichEmoji = true, Ellipsize = true },
                }, Gap: 0.01f),
                // Dot pinned to the top-right: a column of [6 px, dot, filler] the card's height.
                new ColumnElement(new HudElement[]
                {
                    new SpacerElement(0f, 6f),
                    SegmentDot(slot),
                    new SpacerElement(0f, SegCardH - 6f - DotSize),
                }, Gap: 0.01f),
                new SpacerElement(inset - gap, 1f),          // right inset
            }, Gap: gap),
            OnClick: () => { if (slot < _selectedSegments.Length) SelectSession(_selectedSegments[slot]); })
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(Fill()),
            OutlineColorFn = () => Selected() ? Fade(AccentColor() with { A = 0.75f }) : null,
            CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
    }

    private HudElement SegmentDot(int slot) => new ConditionalElement(
        () => _segPhases[slot] is UploadPhase.Done or UploadPhase.InFlight,
        StatusDot(() => _segPhases[slot] == UploadPhase.Done ? SuccessColor() : CautionColor()),
        HollowDot());

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
