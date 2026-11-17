using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// Combat History (Shift+F9) — house style (approved redesign 2026-11-17,
/// <c>_scratch\prototypes\combatmeter-redesign-APPROVED.md</c> §4):
/// <code>
///   ToolHeader  [history icon tile] Combat History / "N runs saved · keeps M"
///   Row[ RUNS panel 280 px (search · 40 px run tiles · Danger "Clear all" → modal confirm)
///      | run panel (status + upload pills + icon buttons, SEGMENTS cards) · chart panel (DPS/HPS/Taken + LineChart) · player table ]
/// </code>
/// The chart / table panels live in Plugin.HistoryWindow.Detail.cs (+ .Table.cs), the run panel in .RunPanel.cs, the
/// SEGMENTS cards in .Segments.cs; the run grouping + the throttled snapshot
/// rebuild in Plugin.HistoryWindow.Runs.cs.
///
/// <para>Fixed size: every block has a fixed height (lists are fixed viewports; the run panel's variable parts sit in
/// equal-height FixedBoxes; confirms are modal cards over the content, Plugin.Confirm.cs), so the content never changes
/// height with the data (SDL rule 14), and <c>WindowSpec.FixedHeight</c> pins the window to <see cref="HistWindowH"/> on
/// top of that. It is not drag-resizable: a rounded SectionPanel (BubbleElement) hugs its content and can't hand slack
/// height to the table, so a taller window would only add an empty strip at the bottom.</para>
/// </summary>
public sealed partial class Plugin
{
    internal event Action<EntityId, EncounterHistoryEntry>? OnSkillBreakdownRequested;
    internal event Action<EntityId, EncounterHistoryEntry>? OnInspectRequested;

    private const int MaxSessionSlots = MaxRetention;   // run-view capacity (the retention setting can change at runtime)
    private const int MaxSourceSlots  = 24;              // player-table rows bound

    // Geometry (rule 18: widen, don't cram). 780 → 880 for the runs column + run pills; 880 → 1000 with the runs column
    // 220 → 280 (addendum "run panel B", 2026-11-17) so map names stop truncating and five segment cards fit a row.
    private const float HistWindowW  = 1000f;
    // The content-fit height of the fixed layout (title bar + ToolHeader + the right column: run panel 188 + chart 278 +
    // table 216 + gaps). Derived from the saved v3 rect (804.5 with the old 189 px run panel) − 1, rounded up; NOT yet
    // measured in game. Too small → the player table's viewport (min 0) gives way; too big → an empty strip below it.
    private const float HistWindowH  = 804f;
    private const float HistBodyW    = HistWindowW - 24f;              // GlassMenu body padding 12 each side
    private const float HistRunsW    = 280f;
    private const float HistGap      = 8f;
    private const float HistDetailW  = HistBodyW - HistRunsW - HistGap; // the right column
    private const float HistRunTileH = 40f;
    private const float HistRunPitch = HistRunTileH + ListRowGap;
    private const int   HistRunRows  = 12;                             // visible run tiles
    private const int   HistRunPool  = HistRunRows + 2;

    // rows × pitch, NOT − the last gap: each tile is centred in its pitch by RingListSlot (1 px clear above and below), so
    // the first tile's top and the last tile's bottom never sit ON the viewport's clip edge (the selected ring was cut).
    private static float HistRunViewport(int rows) => rows * HistRunPitch;
    // The list + footer block below the search box: list viewport + gap + the 28 px "Clear all" row.
    private static float HistRunsBlockH => HistRunViewport(HistRunRows) + PanelGap + PillHeight;

    private int _historyIndex = -1;   // -1 = no session selected (original history-list index)
    private EncounterHistoryEntry? _selectedSession;

    private readonly List<SessionEntry> _historyView = new(MaxSessionSlots);
    private readonly List<SourceRow> _sessionRows = new(MaxSourceSlots);
    private int _runOffset;   // first run row shown by the virtual list

    private readonly struct SessionEntry
    {
        public SessionEntry(int idx, string clock, string count, string meta, int[] segments)
        { Index = idx; Clock = clock; Count = count; Meta = meta; Segments = segments; }
        // Clock = bold "3:13 AM"; Count = muted "×4" ("" for a single archive); Meta = muted "map · length · Np [· trigger]".
        // Segments = every archive of the SAME run (levelUuid), OLDEST FIRST so card 1 is the run's first archive; Index is
        // the one this row opens (the run's main fight). All strings are built on a (throttled, dirty-keyed) rebuild only.
        public readonly int Index; public readonly string Clock, Count, Meta; public readonly int[] Segments;
    }

    // Segments of the currently selected run, oldest first. Drives the SEGMENTS cards.
    private int[] _selectedSegments = Array.Empty<int>();

    // Field struct (no constructor) — keeps clear of the analyzer's ctor-dependency cap. Every string is pre-formatted
    // on rebuild so the row Funcs only index (Plugin-Performance-Rules §2).
    private struct SourceRow
    {
        public EntityId Id;
        public string Label, Spec, Dmg, Dps, Pct;   // Label = "#1 Name"
        public float Share;
        public ColorRgba Wash;                       // class colour (share wash), gauge colour when the class is unknown
        public int ProfId;                           // crest
    }

    // ── window ────────────────────────────────────────────────────────────────────────────────────────────────────
    private WindowCenterer? _historyWinCenterer;

    // A popup dialog: free-drag + ✕ close. Shared by the initial registration (Plugin.cs) and
    // the language rebuild. A NEW id per width change: a saved rect (the old "combatmeter.history" 780 resizable, then
    // "combatmeter.history.v2" at 880) would otherwise override the new default width.
    private IWindowControl RegisterHistoryWindow()
    {
        var w = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.history.v3",
                Title:       _loc.T("history.window.title"),   // baked at registration; rebuilt on LanguageChanged
                DefaultRect: new WindowRect(900f, 160f, HistWindowW, HistWindowH),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, Closable = true, Draggable = true, FixedHeight = true,   // rule 14 belt + braces (HistWindowH)
              // NOT prebuilt (crash fix 2026-11-17). The LineChart's plot is the framework's ChartGraphic, an
              // Il2CppInterop-INJECTED MaskableGraphic: the injector leaves the class's has_references = 0, so IL2CPP
              // allocates it pointer-free and the GC never scans it — the UnityEvent Unity's serializer stores in its
              // (base-ctor-less, null) m_OnCullStateChanged is reaped, and the next serializer walk (scene load
              // UnloadUnusedAssets) runs UnityEventBase.DirtyPersistentCalls on freed memory → native crash ~1 s into
              // the first world load. Prebuild made that graphic exist from the title screen on, every login; the old
              // build only made it when History was opened. Restore with the `histprebuild` diag switch (Plugin.Diag.cs).
              Prebuild = CmDiag.HistPrebuild,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            BuildHistoryRoot(),
            OnClose: CloseHistory));
        _historyWinCenterer?.Stop();   // a rebuild replaces the window; drop the old one's centerer
        _historyWinCenterer = new WindowCenterer(_services.Framework, w);
        return w;
    }

    /// <summary>Opens History centred on <paramref name="opener"/> (null = the screen centre) and in front; already open →
    /// just fronts it. Every entry point goes through here: the HUD ⋯ › History card (opener = the meter), Shift+F9 and the
    /// launcher tile (null).</summary>
    internal void OpenHistory(IWindowControl? opener)
    {
        if (_historyWindow.IsShown) { _historyWindow.BringToFront(); return; }
        RebuildHistorySnapshots();   // fill the list now, not on the first ~10 Hz tick after the show
        _historyWinCenterer?.OpenCentred(opener);
    }

    // ── layout ────────────────────────────────────────────────────────────────────────────────────────────────────
    private readonly FmtCache _histStatusFmt = new();
    private int _historyRunCount;   // grouped runs (unfiltered), set by the snapshot rebuild

    // The whole content sits in the window's ConfirmHostElement: an open confirm (Plugin.Confirm.cs) scrims all of it.
    private HudElement BuildHistoryRoot() => new ConfirmHostElement(new ColumnElement(new HudElement[]
    {
        ToolHeader(HistoryIconTile(), () => _loc.T("history.window.title"),
            new TextElement(() => _histStatusFmt.Get(
                    _loc.T(PluralKey(_historyRunCount, "history.statusOne", "history.status")), _historyRunCount, HistoryRetention),
                Color: () => MutedColor(), NoWrap: true) { Ellipsize = true }),
        new RowElement(new HudElement[]
        {
            new CellElement(BuildRunsPanel(), Width: HistRunsW),
            new CellElement(BuildHistoryDetail(), Width: HistDetailW),
        }, Gap: HistGap),
    }, Gap: HistGap), HistoryConfirm) { ConfirmLabelWidth = HistoryConfirmLabelW };

    // 40 px accent tile + the white rewind-clock glyph (pre-drawn per size, Plugin.MeterIcon.cs HistoryIconPng) tinted
    // with the solid-fill label colour — live with the theme like MeterIconTile. Live: re-picks the exact-size bitmap
    // after a UI-scale change (the picker hands out the same cached arrays, so an unchanged scale costs no re-decode).
    private HudElement HistoryIconTile() => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(8f, ToolIconSize),
            new ImageElement(() => HistoryIconPng(24f), 24, 24) { Live = true, Tint = () => SolidLabel(AccentColor()) },
            new SpacerElement(8f, 1f),
        }, Gap: 0.01f))
    {
        FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(ToolIconSize * 0.22f),
        PaddingX = 0f, PaddingY = 0f, MaxWidth = ToolIconSize,
    }, Width: ToolIconSize);

    // A block exactly `height` tall (as long as its children fit), children top-aligned: Row[height strut, Fill
    // Conditional[Column[children…]]]. The always-true Fill Conditional stretches to the strut's height and its column
    // keeps the children at the top (KB "top-align inside a row"). Variants swapped by a Conditional all use it with the
    // same height, so the swap never changes the window height.
    // ⚠️ It used to be Column[children…, VerticalFill] with Gap PanelGap: the gap before the filler + the filler's 1 px
    // made a FULL box 7 px taller than `height` while an empty one stayed at `height` — the History window was 7 px
    // taller for a multi-segment run than for a single-segment one (and the RUNS panel likewise with vs without runs).
    private static HudElement FixedBox(float height, params HudElement[] children) => new RowElement(new HudElement[]
    {
        new SpacerElement(1f, height),
        new CellElement(new ConditionalElement(() => true, new ColumnElement(children, Gap: PanelGap), Fill: true),
            Weight: 1f),
    }, Gap: 0.01f);

    // ── RUNS panel ────────────────────────────────────────────────────────────────────────────────────────────────
    private readonly FmtCache _runsHintFmt = new();

    private HudElement BuildRunsPanel() => SectionPanel(() => _loc.T("history.runs"),
        () => _runsHintFmt.Get("{0}", _historyView.Count),
        new RowElement(new HudElement[]
        {
            new SpacerElement(1f, PillHeight),
            new InputElement(() => _historySearch, _ => { }, HistRunsW - 2f * PanelPadX - 7f, OnChange: s => _historySearch = s)
                { SingleLine = true, Placeholder = () => _loc.T("history.search") },
        }, Gap: 6f),
        new ConditionalElement(() => _history.Count == 0,
            FixedBox(HistRunsBlockH, MutedLine(() => _loc.T("history.empty.noEncounters"))),
            FixedBox(HistRunsBlockH, RunList(HistRunRows), new RowElement(new HudElement[]
            {
                new SpacerElement(0f),   // flexible: the pill sits at the right edge
                DangerButton(() => _loc.T("history.clearAll"), AskClearAll,
                    EstimateBoldLabelW(_loc.T("history.clearAll"), 13)),
            }, Gap: 0.01f))));

    private HudElement RunList(int rows)
    {
        var pool = new HudElement[Math.Min(HistRunPool, rows + 2)];
        for (int i = 0; i < pool.Length; i++) pool[i] = RunSlot(i);
        return new VirtualListElement(
            Count:     () => _historyView.Count,
            RowHeight: HistRunPitch, Pool: pool,
            OnWindow:  i => _runOffset = i, Height: HistRunViewport(rows));
    }

    // One run tile (40 px + 2 px gap): bold time + muted ×N over the muted "map · length · Np [· trigger]" line.
    // Selected = accent wash + accent ring; hover = Text@0.10 over the tile (round, via the Bare selectable).
    private HudElement RunSlot(int slot)
    {
        var hover = new HoverFlag();
        var tile = new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(2f, HistRunTileH),   // height strut + (with the 6 px gap) the 8 px left inset
                new CellElement(new ColumnElement(new HudElement[]
                {
                    new RowElement(new HudElement[]
                    {
                        new TextElement(() => RunAt(slot) is { } e ? e.Clock : "", Color: () => TextColor(), NoWrap: true, FontSize: 13)
                            { Bold = true },
                        new TextElement(() => RunAt(slot) is { } e ? e.Count : "", Color: () => MutedColor(), NoWrap: true, FontSize: 12)
                            { Bold = true },
                    }, Gap: 4f),
                    new TextElement(() => RunAt(slot) is { } e ? e.Meta : "", Color: () => MutedColor(), NoWrap: true)
                        { Ellipsize = true },
                }, Gap: 0.01f), Weight: 1f),
                new SpacerElement(2f, 1f),             // right inset
            }, Gap: 6f),
            OnClick: () => { if (RunAt(slot) is { } e) SelectSession(e.Index); })
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(RunTileFill(slot, hover.On)),
            OutlineColorFn = () => RunSelected(slot) ? Fade(AccentColor() with { A = 0.75f }) : null,
            CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
        return RingListSlot(tile);
    }

    // A pooled row for a CLIPPED list whose tiles wear a selection ring: [1 px, tile, 1 px] instead of ListSlot's
    // [tile, 2 px]. Same pitch and the same 2 px between tiles, but with a viewport of rows × pitch no tile edge lies on
    // the RectMask2D edge — flush, the scroll content's sub-pixel offset shaved the ring's top/bottom row (2026-11-17).
    // With a 2 px row gap 1 px is all the room there is; it covers the sub-pixel cut (the ring is drawn INSIDE the tile).
    private const float RingListPad = ListRowGap / 2f;

    private static HudElement RingListSlot(HudElement tile) => new ColumnElement(new HudElement[]
    {
        new SpacerElement(0f, RingListPad),
        tile,
        new SpacerElement(0f, ListRowGap - RingListPad),
    }, Gap: 0.01f);

    private SessionEntry? RunAt(int slot)
    {
        int i = _runOffset + slot;
        return i >= 0 && i < _historyView.Count ? _historyView[i] : null;
    }

    private bool RunSelected(int slot) => RunAt(slot) is { } e && Array.IndexOf(e.Segments, _historyIndex) >= 0;

    private ColorRgba RunTileFill(int slot, bool hovered)
    {
        var fill = RunSelected(slot) ? Over(AccentColor() with { A = SelectedWashMix }, TileColor()) : TileColor();
        return hovered ? HoverFill(fill) : fill;
    }

    // ── selection ─────────────────────────────────────────────────────────────────────────────────────────────────
    private void SelectSession(int historyIndex)
    {
        _historyIndex = historyIndex;
        _selectedSession = historyIndex >= 0 && historyIndex < _history.Count ? _history[historyIndex] : null;
        // The run's segments NOW (the snapshot tick would only catch up ~100 ms later, showing the old run's cards).
        foreach (var e in _historyView)
            if (Array.IndexOf(e.Segments, historyIndex) >= 0) { _selectedSegments = e.Segments; break; }
        // A new session => no carried-over chart lines, and the visible (zoom) window resets to the full span.
        _chartedSources.Clear();
        _chartSourcesVersion++;   // mark the cached chart series stale
        // Full span = REAL elapsed duration (see ChartExtentSeconds): the series run to the archive moment, so anchoring
        // the window on the damage span clipped the chart short of its own data.
        var durationSeconds = _selectedSession is { } h
            ? ChartExtentSeconds(RealDurationMs(h.EnteredAtMs, h.ArchivedAtMs), h.CombatDurationMs)
            : 0f;
        _chartVisibleRange = (0f, durationSeconds);
        RebuildSessionRows();
    }

    // ── drill-in (›) / inspect (⌕) ────────────────────────────────────────────────────────────────────────────────
    private bool DrillOpen(int row)
        => row >= 0 && row < _sessionRows.Count && _selectedSession is { } h && _skillBreakdown is { } sb
           && sb.Source == _sessionRows[row].Id && ReferenceEquals(sb.Session, h);

    private void DrillIn(int row)
    {
        if (row < 0 || row >= _sessionRows.Count || _selectedSession is not { } h) return;
        OnSkillBreakdownRequested?.Invoke(_sessionRows[row].Id, h);
    }

    // Shown only for a player row whose session captured a snapshot (id.IsPlayer && Entities has the key).
    private bool InspectAvailable(int row)
        => row >= 0 && row < _sessionRows.Count && _selectedSession is { } h
           && _sessionRows[row].Id.IsPlayer && h.Entities.ContainsKey(_sessionRows[row].Id);

    private bool InspectOpen(int row)
        => row >= 0 && row < _sessionRows.Count && _selectedSession is { } h && _snapshot is { } s
           && s.Source == _sessionRows[row].Id && ReferenceEquals(s.Session, h);

    private void InspectRow(int row)
    {
        if (row < 0 || row >= _sessionRows.Count || _selectedSession is not { } h) return;
        OnInspectRequested?.Invoke(_sessionRows[row].Id, h);
    }

    // Auto-archive segments show WHY they ended; manual/scene stay untagged (pre-v10 default).
    // Finding 5 (review round 2026-07-27): this is a SEPARATE allow-list from ArchiveReasonTag's switch — a reason can
    // be mapped there and still render with no suffix here if forgotten (as "bosskill" was), indistinguishable from a
    // manual archive. internal (not private) so TriggerSuffix_covers_every_auto_reason can pin completeness against
    // every ArchiveReason value. The run tile shows the READABLE name (SegmentLabel) wherever this is non-empty.
    internal static string TriggerSuffix(string trigger)
        => trigger is "wipe" or "boss" or "idle" or "stage" or "bosskill" or "boundary" or "clear" or "prepare" ? $" · {trigger}" : "";

    /// <summary>Player-facing name of a stored trigger tag ("bosskill" → "Boss kill"): the archive-reason catalog
    /// (<c>archive.reason.*</c>, shared with Settings › Archive) plus the two content tags of a pre-boss cut. An unknown
    /// tag shows as stored. Called on rebuild only (it builds a key string).</summary>
    private string SegmentLabel(string? trigger)
    {
        var tag = string.IsNullOrEmpty(trigger) ? "manual" : trigger!;
        var key = "archive.reason." + tag;
        var label = _loc.T(key);
        return label == key ? tag : label;
    }

    // ── player table rows ─────────────────────────────────────────────────────────────────────────────────────────
    private void RebuildSessionRows()
    {
        _sessionRows.Clear();
        RebuildRunHeader();
        if (_selectedSession is not { } h || h.Stats.Count == 0) return;

        var metric = _historyMetric;
        long metricTotal = ComputeSessionMetricTotal(h, metric);
        var rows = new List<KeyValuePair<EntityId, SourceStats>>(h.Stats.Count);
        foreach (var kv in h.Stats) rows.Add(kv);
        rows.Sort((a, b) => MetricValueOf(b.Value, metric).CompareTo(MetricValueOf(a.Value, metric)));

        EntityId self = _services.CombatSnapshot.LocalEntityId;
        for (int i = 0; i < rows.Count && _sessionRows.Count < MaxSourceSlots; i++)
        {
            var id = rows[i].Key; var s = rows[i].Value;
            long value = MetricValueOf(s, metric);
            var pct = metricTotal > 0 ? (float)value / metricTotal : 0f;
            // Archived rows read identity/class from the FROZEN per-player snapshot — re-resolving live here gave
            // "Player#<uid>" and a blank class once the players left AOI / the scene changed. Fall back to live
            // resolution only when the session predates the snapshot (legacy entries) or a source wasn't frozen.
            var frozen = h.Entities.TryGetValue(id, out var es) ? es : null;
            var name = ArchivedName(frozen, id, self);
            int prof = ArchivedProfession(frozen, id);
            _sessionRows.Add(new SourceRow
            {
                Id = id,
                Label = $"#{i + 1} {name}",
                Spec = ResolveArchivedClassLine(frozen, h.EnteredAtMs, h.ArchivedAtMs, id),
                Dmg = FormatAmount(value),                                              // primary = metric value
                Dps = FormatAmount(ComputeArchivedDps(value, h.CombatDurationMs)),       // rate = metric / sec
                Pct = FormatPercent(pct),
                Share = pct,
                Wash = ClassPalette.TryGet(prof, out var cc) ? cc : GaugeColorFor(id),
                ProfId = prof,
            });
        }
    }

    // The crest / wash class of an archived row: the frozen base class (never a Battle Imagine transform), else live.
    private int ArchivedProfession(EntitySnapshot? frozen, EntityId id)
    {
        int prof = frozen != null ? PlayableClass.ResolveActorProfession(frozen) : 0;
        return prof > 0 ? prof : id.IsPlayer ? ResolveProfessionId(id) : 0;
    }

    // The display name of an archived source: the frozen snapshot's name, else live resolution (legacy entries / a
    // source that wasn't frozen). Shared by the table rows and the chart legend so both always show the same name —
    // the legend used to resolve live only and showed "Player#<uid>" for anyone who had left AOI.
    private string ArchivedName(EntitySnapshot? frozen, EntityId id, EntityId self)
        => !string.IsNullOrEmpty(frozen?.Name)
            ? frozen!.Name!
            : EntityLabel.Resolve(id, self, _services.PlayerState, _services.CombatLookup, _services.PartyRoster.Members);
}
