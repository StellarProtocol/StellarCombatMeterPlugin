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
///   Row[ RUNS panel 220 px (search · 40 px run tiles · Danger "Clear all" → inline confirm)
///      | run panel (upload pills, segments) · chart panel (DPS/HPS/Taken + LineChart) · player table ]
/// </code>
/// The run / chart / table panels live in Plugin.HistoryWindow.Detail.cs; the run grouping + the throttled snapshot
/// rebuild in Plugin.HistoryWindow.Runs.cs.
///
/// <para>Content-fit height, fixed width: every block has a fixed height (lists are fixed viewports, the confirm swaps
/// keep their size), so the window never grows or shrinks with the data (SDL rule 14). It is no longer drag-resizable:
/// a rounded SectionPanel (BubbleElement) hugs its content and can't hand slack height to the table, so a taller
/// window would only add an empty strip at the bottom.</para>
/// </summary>
public sealed partial class Plugin
{
    internal event Action<EntityId, EncounterHistoryEntry>? OnSkillBreakdownRequested;
    internal event Action<EntityId, EncounterHistoryEntry>? OnInspectRequested;

    private const int MaxSessionSlots = MaxRetention;   // run-view capacity (the retention setting can change at runtime)
    private const int MaxSourceSlots  = 24;              // player-table rows bound

    // Geometry (rule 18: widen, don't cram — the old 780 px window could not fit the 220 px runs column + the run pills).
    private const float HistWindowW  = 880f;
    private const float HistBodyW    = HistWindowW - 24f;              // GlassMenu body padding 12 each side
    private const float HistRunsW    = 220f;
    private const float HistGap      = 8f;
    private const float HistDetailW  = HistBodyW - HistRunsW - HistGap; // the right column
    private const float HistRunTileH = 40f;
    private const float HistRunPitch = HistRunTileH + ListRowGap;
    private const int   HistRunRows  = 12;                             // visible run tiles
    private const int   HistRunRowsConfirming = HistRunRows - 4;       // while the Clear-all confirm takes the bottom
    private const int   HistRunPool  = HistRunRows + 2;

    private static float HistRunViewport(int rows) => rows * HistRunPitch - ListRowGap;
    // The list + footer block below the search box: list viewport + gap + the 28 px "Clear all" row. The confirm variant
    // trades four run rows for the confirm box, inside the same height.
    private static float HistRunsBlockH => HistRunViewport(HistRunRows) + PanelGap + PillHeight;

    private int _historyIndex = -1;   // -1 = no session selected (original history-list index)
    private EncounterHistoryEntry? _selectedSession;

    // Clear all: Danger pill → an inline confirm panel ("Delete all N runs?" + consequence + Danger/Neutral). Any other
    // interaction (selecting a run) cancels it.
    private bool _clearAllConfirm;

    private readonly List<SessionEntry> _historyView = new(MaxSessionSlots);
    private readonly List<SourceRow> _sessionRows = new(MaxSourceSlots);
    private int _runOffset;   // first run row shown by the virtual list

    private readonly struct SessionEntry
    {
        public SessionEntry(int idx, string clock, string count, string meta, int[] segments)
        { Index = idx; Clock = clock; Count = count; Meta = meta; Segments = segments; }
        // Clock = bold "3:13 AM"; Count = muted "×4" ("" for a single archive); Meta = muted "map · length · Np [· trigger]".
        // Segments = every archive of the SAME run (levelUuid), OLDEST FIRST so chip 1 is the run's first archive; Index is
        // the one this row opens (the run's main fight). All strings are built on a (throttled, dirty-keyed) rebuild only.
        public readonly int Index; public readonly string Clock, Count, Meta; public readonly int[] Segments;
    }

    // Segments of the currently selected run, oldest first. Drives the run panel's segment chips.
    private int[] _selectedSegments = Array.Empty<int>();

    // Chip slots for the segment picker (built once, polled). A real RAID banks 13 archives per session (measured
    // 2026-08-25, Clash! Field of Forgotten Illusions ×2: prepare/bosskill/cut per stage); 24 covers that with room.
    private const int MaxSegmentChips = 24;

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

    // A popup dialog: free-drag + ✕ close. Shared by the initial registration (Plugin.cs), the metric-change rebuild and
    // the language rebuild. A NEW id: the old "combatmeter.history" carries a persisted 780-wide resizable rect.
    private IWindowControl RegisterHistoryWindow()
    {
        var w = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.history.v2",
                Title:       _loc.T("history.window.title"),   // baked at registration; rebuilt on LanguageChanged
                DefaultRect: new WindowRect(900f, 160f, HistWindowW, 760f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, Closable = true, Draggable = true,
              // ~40 pooled tiles + the chart: build it in the background instead of in the click that opens it
              // (Plugin-Performance-Rules §5). Every value lambda is hidden-safe (the lists read 0 rows until shown).
              Prebuild = true,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            BuildHistoryRoot(),
            OnClose: CloseHistory));
        _historyWinCenterer?.Stop();   // a rebuild replaces the window; drop the old one's centerer
        _historyWinCenterer = new WindowCenterer(_services.Framework, w);
        return w;
    }

    /// <summary>Opens History centred on <paramref name="opener"/> (null = the screen centre) and in front; already open →
    /// just fronts it. The HUD's ⋯ › History card should call this with the meter window.</summary>
    internal void OpenHistory(IWindowControl? opener)
    {
        if (_historyWindow.IsShown) { _historyWindow.BringToFront(); return; }
        RebuildHistorySnapshots();   // fill the list now, not on the first ~10 Hz tick after the show
        _historyWinCenterer?.OpenCentred(opener);
    }

    // The LineChartElement bakes axis ticks at build time; rebuild the window subtree (preserving rect + visibility) so a
    // metric change rescales the Y axis. Framework-sanctioned Remove()+Register() pattern.
    private void RebuildHistoryWindow()
    {
        var rect = _historyWindow.Rect;
        var wasShown = _historyWindow.IsShown;
        _historyWindow.Remove();
        _historyWindow = RegisterHistoryWindow();
        if (rect.Width > 0f) _historyWindow.SetRect(rect);
        _historyWindow.SetVisible(wasShown);
    }

    // ── layout ────────────────────────────────────────────────────────────────────────────────────────────────────
    private readonly FmtCache _histStatusFmt = new();
    private int _historyRunCount;   // grouped runs (unfiltered), set by the snapshot rebuild

    private HudElement BuildHistoryRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(HistoryIconTile(), () => _loc.T("history.window.title"),
            new TextElement(() => _histStatusFmt.Get(_loc.T("history.status"), _historyRunCount, HistoryRetention),
                Color: () => MutedColor(), NoWrap: true) { Ellipsize = true }),
        new RowElement(new HudElement[]
        {
            new CellElement(BuildRunsPanel(), Width: HistRunsW),
            new CellElement(BuildHistoryDetail(), Width: HistDetailW),
        }, Gap: HistGap),
    }, Gap: HistGap);

    // 40 px accent tile + the white history glyph (Resources/history.png, the launcher icon) tinted with the solid-fill
    // label colour — live with the theme like MeterIconTile. Live: the PNG is loaded by RegisterLauncher, which runs after
    // this window is registered.
    private HudElement HistoryIconTile() => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(8f, ToolIconSize),
            new ImageElement(() => _historyIconPng, 24, 24) { Live = true, Tint = () => SolidLabel(AccentColor()) },
            new SpacerElement(8f, 1f),
        }, Gap: 0.01f))
    {
        FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(ToolIconSize * 0.22f),
        PaddingX = 0f, PaddingY = 0f, MaxWidth = ToolIconSize,
    }, Width: ToolIconSize);

    // A block pinned to (at least) `height`, its children top-aligned: Row[height strut, Column[children…, filler]].
    // Two variants swapped by a Conditional both use it, so the swap never changes the window height.
    private static HudElement FixedBox(float height, params HudElement[] children)
    {
        var col = new List<HudElement>(children.Length + 1);
        col.AddRange(children);
        col.Add(VerticalFill());
        return new RowElement(new HudElement[]
        {
            new SpacerElement(1f, height),
            new CellElement(new ColumnElement(col, Gap: PanelGap), Weight: 1f),
        }, Gap: 0.01f);
    }

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
            new ConditionalElement(() => _clearAllConfirm,
                FixedBox(HistRunsBlockH, RunList(HistRunRowsConfirming), ClearAllConfirmBox()),
                FixedBox(HistRunsBlockH, RunList(HistRunRows), new RowElement(new HudElement[]
                {
                    new SpacerElement(0f),   // flexible: the pill sits at the right edge
                    DangerButton(() => _loc.T("history.clearAll"), () => _clearAllConfirm = true,
                        EstimateBoldLabelW(_loc.T("history.clearAll"), 13)),
                }, Gap: 0.01f)))));

    // Each variant gets its OWN pool (a pooled element can live in one place only); only one is ever shown.
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
        return ListSlot(tile);
    }

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

    // Inline confirm (SDL "destructive actions confirm"): bold question + muted consequence + the Danger/Neutral pair, on
    // a faint red wash. Lives inside the fixed list block, so it never changes the window height. The pair is STACKED
    // (full-width pills): side by side each pill gets ~60 px of label in this 220 px column — too narrow for
    // "キャンセル" / "Kanselahin".
    private readonly FmtCache _clearAllAskFmt = new();
    // 220 − 2 × panel padding − 2 × confirm-box padding, minus the pill's own padding.
    private const float ConfirmPairLabelW = HistRunsW - 4f * PanelPadX - 2f * PillPadX;

    private HudElement ClearAllConfirmBox() => new BubbleElement(new ColumnElement(new HudElement[]
    {
        new TextElement(() => _clearAllAskFmt.Get(_loc.T("history.clearAll.ask"), _historyRunCount),
            Color: () => TextColor(), FontSize: 13) { Bold = true },
        new TextElement(() => _loc.T("history.clearAll.consequence"), Color: () => MutedColor()),
        DangerButton(() => _loc.T("history.delete.confirm"), ConfirmClearAll, ConfirmPairLabelW),
        NeutralButton(() => _loc.T("cfg.cancel"), () => _clearAllConfirm = false, ConfirmPairLabelW),
    }, Gap: PanelGap))
    {
        FillColorFn = () => Fade(Over(DangerColor() with { A = 0.10f }, PanelColor())),
        CornerRadius = TileRadius, PaddingX = PanelPadX, PaddingY = PanelPadY,
    };

    private void ConfirmClearAll()
    {
        _clearAllConfirm = false;
        ClearAllHistory();
    }

    // ── selection ─────────────────────────────────────────────────────────────────────────────────────────────────
    private void SelectSession(int historyIndex)
    {
        _clearAllConfirm = false;   // any other interaction cancels the pending confirms
        _deleteRunConfirm = false;
        _historyIndex = historyIndex;
        _selectedSession = historyIndex >= 0 && historyIndex < _history.Count ? _history[historyIndex] : null;
        // The run's segments NOW (the snapshot tick would only catch up ~100 ms later, showing the old run's chips).
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
            var name = !string.IsNullOrEmpty(frozen?.Name)
                ? frozen!.Name!
                : EntityLabel.Resolve(id, self, _services.PlayerState, _services.CombatLookup, _services.PartyRoster.Members);
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
}
