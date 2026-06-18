using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// History surface — a uGUI master-detail window (Party chrome). Left pane: a scrollable session list (newest
/// first, SelectableElement rows). Right pane: the selected session's per-source table (rank·name·class +
/// TOTAL DMG / DPS / %DMG columns) with a drill-in ► button that fires <see cref="OnSkillBreakdownRequested"/>.
/// The plugin snapshots both panes into _historyView / _sessionRows each shown frame.
/// </summary>
public sealed partial class Plugin
{
    internal event Action<EntityId, EncounterHistoryEntry>? OnSkillBreakdownRequested;
    internal event Action<EntityId, EncounterHistoryEntry>? OnInspectRequested;

    private const int MaxSessionSlots = HistoryCapacity;   // session list bound
    private const int MaxSourceSlots  = 24;                 // detail rows bound
    private const float HistListHeight   = 300f;
    private const float HistDetailHeight = 260f;
    // Detail table columns (right-aligned numerics) — 3 metric-aware numerics + drill ►:
    // DPS: DMG·DPS·% ; HPS: HEAL·HPS·% ; Taken: TAKEN·DTPS·%.
    // Inspect + drill are equal-sized action buttons (same button Width + same cell width) so the row's two
    // affordances read as a matched pair. ActionBtnW pins the button; the cell is a hair wider for centering slack.
    private const float ColPrimary = 64f, ColRate = 56f, ColPct = 44f;
    private const float ActionBtnW = 30f, ColDrill = 34f, ColInspect = 34f;

    private int _historyIndex = -1;   // -1 = no session selected (original history-list index)
    private EncounterHistoryEntry? _selectedSession;

    // Clear-all is a 2-click confirm to guard against a misclick wiping up to 50 sessions: first click arms it
    // (label flips to "Confirm?"), second click within the same visit clears. Any other interaction re-disarms.
    private bool _clearAllArmed;

    private readonly List<SessionEntry> _historyView = new(MaxSessionSlots);
    private readonly List<SourceRow> _sessionRows = new(MaxSourceSlots);

    private readonly struct SessionEntry
    {
        public SessionEntry(int idx, string clock, string meta)
        { Index = idx; Clock = clock; Meta = meta; }
        // Clock = compact "⏱ 2:14p" emphasis line; Meta = pre-joined "{dur} · {n}p · {scene}" muted line.
        public readonly int Index; public readonly string Clock, Meta;
    }

    // Field struct (no constructor) — keeps clear of the analyzer's ctor-dependency cap.
    private struct SourceRow
    {
        public EntityId Id;
        public string Rank, Name, Class, Dmg, Dps, Pct;
        public float Share;
        public ColorRgba Role;
    }

    // The session list pane is a FIXED width (not proportional): wide enough for a typical map name + the meta
    // line on one row each, so a name like "Asteria Plains" reads cleanly instead of wrapping to ~5 lines in a
    // narrow proportional column. The detail pane keeps the remaining width via its Weight.
    private const float HistListWidth = 180f;

    private HudElement BuildHistoryRoot() => new RowElement(new HudElement[]
    {
        new CellElement(BuildSessionList(), Width: HistListWidth),
        new SeparatorElement(Vertical: true),
        new CellElement(BuildSessionDetail(), Weight: 2f),
    }, Gap: 8f);

    private HudElement BuildSessionList()
    {
        var slots = new HudElement[MaxSessionSlots];
        for (var i = 0; i < MaxSessionSlots; i++)
        {
            var idx = i;
            slots[i] = new SelectableElement(
                new ColumnElement(new HudElement[]
                {
                    new TextElement(() => idx < _historyView.Count ? "⏱ " + _historyView[idx].Clock : "", Emphasis: true, NoWrap: true),
                    new TextElement(() => idx < _historyView.Count ? _historyView[idx].Meta : "", MutedCol, NoWrap: true),
                }, Gap: 1f),
                OnClick: () => { if (idx < _historyView.Count) SelectSession(_historyView[idx].Index); },
                Selected: () => idx < _historyView.Count && _historyView[idx].Index == _historyIndex);
        }
        return new ColumnElement(new HudElement[]
        {
            new ConditionalElement(() => _history.Count == 0,
                new TextElement(() => "No archived encounters yet.", MutedCol)),
            new ConditionalElement(() => _history.Count > 0,
                new ScrollElement(new ListElement(() => _historyView.Count, slots), HistListHeight)),
            BuildClearAllRow(),
        }, Gap: 4f);
    }

    // Footer of the session pane: the 2-click "Clear all" confirm. Hidden when there's nothing to clear. The
    // armed label warns explicitly so a second click is a deliberate confirmation, not a repeat misclick.
    private HudElement BuildClearAllRow() => new ConditionalElement(() => _history.Count > 0,
        new RowElement(new HudElement[]
        {
            new SpacerElement(),   // push the button to the right edge
            new ButtonElement(
                () => _clearAllArmed ? "Confirm clear all?" : "Clear all",
                ClearAllClicked,
                Active: () => _clearAllArmed),
        }, Gap: 6f));

    private void ClearAllClicked()
    {
        if (!_clearAllArmed) { _clearAllArmed = true; return; }   // first click arms
        _clearAllArmed = false;
        ClearAllHistory();
    }

    private HudElement BuildSessionDetail()
    {
        var slots = new HudElement[MaxSourceSlots];
        for (var i = 0; i < MaxSourceSlots; i++) slots[i] = BuildSourceRowSlot(i);
        var table = new ColumnElement(new HudElement[]
        {
            BuildHistoryMetricRow(),
            BuildSessionSummaryRow(),
            BuildHistoryChart(),
            BuildDetailHeaderRow(),
            new ScrollElement(new ListElement(() => _sessionRows.Count, slots), HistDetailHeight),
        });
        return new ColumnElement(new HudElement[]
        {
            new ConditionalElement(() => _selectedSession is null,
                new TextElement(() => "Pick a session on the left.", MutedCol)),
            // Fill:true so the detail column grows to the (resizable) window height and the table's ScrollElement
            // (the only flexible-height child in `table`) absorbs the slack — the chart + navigator keep their
            // fixed heights and stay stacked above it. When the window is dragged SHORT the table area shrinks
            // (and scrolls its rows) instead of the chart squishing and the top-anchored navigator overlapping
            // the table. The chart root pins minHeight==preferredHeight (WindowBuilder.LineChart) so it can never
            // be squished below the navigator's offset; this Fill routes the deficit to the scroll.
            new ConditionalElement(() => _selectedSession is not null, table, Fill: true),
        });
    }

    // The timeline chart: team-total (always) + a line per source toggled into _chartedSources. Axis scale +
    // Y title follow _historyMetric; rebuilt (not refreshed) on metric change so the baked axis rescales.
    private HudElement BuildHistoryChart() => new LineChartElement(
        Series:          BuildChartSeries,
        BucketSeconds:   () => _selectedSession is { } h ? SeriesBucketSeconds(h) : 1f,
        FormatY:         v => FormatAmount((long)v),
        FormatX:         FormatChartX,
        TitleY:          () => MetricAxisTitle(_historyMetric),
        TitleX:          () => "Encounter time (m:ss)",
        VisibleRange:    () => _chartVisibleRange,
        SetVisibleRange: r => _chartVisibleRange = r,
        Width:           360f,    // minimum/initial width; FillWidth lets the plot grow with the window
        Height:          180f,
        ShowNavigator:   true,    // Highcharts-style overview + brush (replaces the −/+/Reset bar + scrollbar)
        FillWidth:       true);   // plot + navigator stretch to fill the detail pane; reflows on resize

    // Metric-aware column header — the primary value column + rate column relabel with _historyMetric.
    private HudElement BuildDetailHeaderRow() => new RowElement(new HudElement[]
    {
        new CellElement(new TextElement(() => "Source", MutedCol), Weight: 1f),
        NumCell(() => MetricColumnLabel(_historyMetric), ColPrimary, muted: true),
        NumCell(() => MetricRateLabel(_historyMetric), ColRate, muted: true),
        NumCell(() => "%", ColPct, muted: true),
        new CellElement(new TextElement(() => ""), Width: ColInspect),
        new CellElement(new TextElement(() => ""), Width: ColDrill),
    }, Gap: 6f);

    // One source row: AccentRowElement (metric-share stripe) wrapped in a SelectableElement so a body click
    // toggles the chart line, while the inner ► ButtonElement keeps its own hit area for the drill-in.
    private HudElement BuildSourceRowSlot(int i)
    {
        var idx = i;
        string F(Func<SourceRow, string> sel) => idx < _sessionRows.Count ? sel(_sessionRows[idx]) : "";
        var row = new RowElement(new HudElement[]
        {
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => idx < _sessionRows.Count ? $"{_sessionRows[idx].Rank} {_sessionRows[idx].Name}" : "", Emphasis: true),
                new TextElement(() => F(r => r.Class), MutedCol),
            }, Gap: 0f), Weight: 1f),
            NumCell(() => F(r => r.Dmg), ColPrimary),
            NumCell(() => F(r => r.Dps), ColRate),
            NumCell(() => F(r => r.Pct), ColPct),
            // Inspect (frozen entity snapshot) — own hit area, shown only when this row is a player WITH a
            // captured snapshot. The button label/visibility track InspectAvailable so non-player or
            // no-snapshot rows show nothing (no clobbering the body click-to-chart or the ► drill).
            // Magnifier icon (procedural, baked once) instead of the old ○/◉ glyph — clearer "inspect" affordance,
            // matching the Entity Inspector's profile-card action. Active() highlights it while the snapshot is open.
            new CellElement(new ConditionalElement(() => InspectAvailable(idx),
                new ButtonElement(() => "", () => InspectRow(idx),
                    Active: () => InspectOpen(idx), Width: ActionBtnW, Icon: () => _inspectIconPng)), Width: ColInspect),
            new CellElement(new ButtonElement(() => DrillLabel(idx), () => DrillIn(idx),
                Active: () => DrillOpen(idx), Width: ActionBtnW), Width: ColDrill),
        }, Gap: 6f);
        var accent = new AccentRowElement(row,
            () => idx < _sessionRows.Count ? _sessionRows[idx].Role : default,
            () => idx < _sessionRows.Count ? _sessionRows[idx].Share : 0f);
        return new SelectableElement(accent,
            OnClick:  () => { if (idx < _sessionRows.Count) ToggleChartSource(_sessionRows[idx].Id); },
            Selected: () => idx < _sessionRows.Count && _chartedSources.Contains(_sessionRows[idx].Id));
    }

    // Right-aligned fixed-width numeric column cell.
    private HudElement NumCell(Func<string> text, float width, bool muted = false)
        => new CellElement(new TextElement(text, muted ? MutedCol : (Func<ColorRgba?>?)null, Align: TextAlign.Right), Width: width);

    private static string FormatSeconds(float s)
    {
        var total = (int)(s < 0f ? 0f : s);
        return $"{total / 60}:{total % 60:00}";
    }

    // Span-aware X tick label: short encounters get sub-second / integer-second precision so the few visible
    // ticks stay distinct (a short span otherwise collapses every tick to "0:00"); longer ranges fall back to
    // the m:ss clock. Span is read from the live zoom window so it adapts as the navigator/zoom changes it.
    private string FormatChartX(float v)
    {
        var span = _chartVisibleRange.Max - _chartVisibleRange.Min;
        if (span < 3f) return $"{v:0.0}s";
        if (span < 60f) return $"{(int)(v < 0f ? 0f : v)}s";
        return FormatSeconds(v);
    }

    // Summary line + a right-aligned "Delete this session" affordance for the currently selected session. The
    // detail-pane button avoids per-row hit-area conflicts with the SelectableElement rows in the session list.
    private const float DeleteSessionBtnW = 132f;

    private HudElement BuildSessionSummaryRow() => new RowElement(new HudElement[]
    {
        new CellElement(new TextElement(SessionSummary, Emphasis: true), Weight: 1f),
        // Fixed width so the row RESERVES the button's space — otherwise the weight-1 summary cell takes the full
        // width and the button overdraws the wrapped meta text when the window is narrowed (#delete-overlap).
        new ButtonElement(() => "Delete session", DeleteSelectedSession,
            Enabled: () => _selectedSession is not null, Width: DeleteSessionBtnW),
    }, Gap: 6f);

    private void DeleteSelectedSession()
    {
        if (_historyIndex >= 0) DeleteSession(_historyIndex);
    }

    // Detail summary as "label: value" fields. Map resolves the raw scene id to a friendly map/dungeon name
    // (live game table — falls back to "Scene {id}" / the raw token off-line); Scene shows the raw id alongside.
    private string SessionSummary()
    {
        if (_selectedSession is not { } h) return "";
        return $"Map: {ResolveSceneName(h.SceneName)}   ·   "
             + $"Time: {FormatSessionTimestampLong(h.ArchivedAtMs)}   ·   "
             + $"Duration: {FormatSessionDurationShort(h.CombatDurationMs)}   ·   "
             + $"Players: {h.MemberCount}   ·   "
             + $"Scene: {h.SceneName ?? "—"}";
    }

    // Resolve a stored scene token (a raw id string like "7") to a friendly map/dungeon name via the live game
    // World table. Falls back to "Scene {id}" for an unknown numeric id, or the raw token when non-numeric/empty.
    private string ResolveSceneName(string? sceneToken)
    {
        if (string.IsNullOrEmpty(sceneToken)) return "Unknown";
        if (!int.TryParse(sceneToken, out var id)) return sceneToken;
        var name = _services.GameData.World.GetScene(id)?.Name;
        return string.IsNullOrEmpty(name) ? $"Scene {id}" : name;
    }

    private void SelectSession(int historyIndex)
    {
        _clearAllArmed = false;   // any other interaction disarms the clear-all confirm
        _historyIndex = historyIndex;
        _selectedSession = historyIndex >= 0 && historyIndex < _history.Count ? _history[historyIndex] : null;
        // A new session => no carried-over chart lines, and the visible (zoom) window resets to the full span.
        _chartedSources.Clear();
        _chartSourcesVersion++;   // mark the cached chart series stale
        var durationSeconds = _selectedSession is { } h ? h.CombatDurationMs / 1000f : 0f;
        _chartVisibleRange = (0f, durationSeconds);
        RebuildSessionRows();
    }

    // A popup dialog (opened from the ≡ menu): free-drag + ✕ close. EditModeDragOnly defaults false, so it
    // drags freely (not editor-managed) even though it wears the Party chrome. Shared by the initial
    // registration (Plugin.cs) and the metric-change rebuild below.
    private IWindowControl RegisterHistoryWindow() => _services.Windows.Register(new WindowRegistration(
        new WindowSpec(
            Id:          "combatmeter.history",
            Title:       "Combat History",
            DefaultRect: new WindowRect(900f, 380f, 780f, 640f),
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.GlassMenu)
        // MinHeight 480 is a secondary guard so the window can't be dragged absurdly short: the detail column's
        // fixed content (metric + summary rows + the ~256-px chart+legend+navigator block + the table header)
        // plus a couple of table rows fit at 480, so even at the floor the chart → navigator → table stay
        // stacked with the table scrolling. The real fix is the Fill:true table scroll + the chart-root minHeight
        // floor (WindowBuilder.LineChart) — this just stops a degenerate drag-to-nothing.
        { StartVisible = false, HideUntilInWorld = true, Closable = true, Draggable = true,
          Resizable = true, MinWidth = 600f, MinHeight = 480f, MaxWidth = 1200f, MaxHeight = 1000f },
        BuildHistoryRoot(),
        OnClose: CloseHistory));

    // The LineChartElement bakes axis ticks at build time; rebuild the window subtree (preserving rect +
    // visibility) so a metric change rescales the Y axis. Framework-sanctioned Remove()+Register() pattern.
    private void RebuildHistoryWindow()
    {
        var rect = _historyWindow.Rect;
        var wasShown = _historyWindow.IsShown;
        _historyWindow.Remove();
        _historyWindow = RegisterHistoryWindow();
        // Position actually survives via the Draggable window's persisted LayoutStorage rect (restored on the
        // next mount); this SetRect is belt-and-suspenders (a no-op while Token is null pre-Tick) and the
        // explicit guard matters only if this window ever becomes non-Draggable/non-persisted.
        if (rect.Width > 0f) _historyWindow.SetRect(rect);
        _historyWindow.SetVisible(wasShown);
    }

    // ----- drill-in (►) -----

    private string DrillLabel(int idx) => DrillOpen(idx) ? "◄" : "►";

    private bool DrillOpen(int idx)
        => idx < _sessionRows.Count && _selectedSession is { } h && _skillBreakdown is { } sb
           && sb.Source == _sessionRows[idx].Id && ReferenceEquals(sb.Session, h);

    private void DrillIn(int idx)
    {
        if (idx >= _sessionRows.Count || _selectedSession is not { } h) return;
        OnSkillBreakdownRequested?.Invoke(_sessionRows[idx].Id, h);
    }

    // ----- inspect (frozen entity snapshot, issue #5) -----

    // Shown only for a player row whose session captured a snapshot (id.IsPlayer && Entities has the key).
    private bool InspectAvailable(int idx)
        => idx < _sessionRows.Count && _selectedSession is { } h
           && _sessionRows[idx].Id.IsPlayer && h.Entities.ContainsKey(_sessionRows[idx].Id);

    private bool InspectOpen(int idx)
        => idx < _sessionRows.Count && _selectedSession is { } h && _snapshot is { } s
           && s.Source == _sessionRows[idx].Id && ReferenceEquals(s.Session, h);

    private void InspectRow(int idx)
    {
        if (idx >= _sessionRows.Count || _selectedSession is not { } h) return;
        OnInspectRequested?.Invoke(_sessionRows[idx].Id, h);
    }

    // ----- snapshots -----

    private void RebuildHistorySnapshots()
    {
        _historyView.Clear();
        for (int i = _history.Count - 1; i >= 0; i--)   // newest first
        {
            var h = _history[i];
            var dur = FormatSessionDurationShort(h.CombatDurationMs);
            var map = ResolveSceneName(h.SceneName);
            _historyView.Add(new SessionEntry(
                i,
                FormatSessionClock(h.ArchivedAtMs),
                $"{map} · {dur} · {h.MemberCount}p"));
        }
        // Keep the selected session in sync (it may have been evicted).
        if (_historyIndex >= 0 && _historyIndex < _history.Count) _selectedSession = _history[_historyIndex];
        else { _selectedSession = null; _historyIndex = -1; _chartedSources.Clear(); _chartSourcesVersion++; }
        RebuildSessionRows();
    }

    private void RebuildSessionRows()
    {
        _sessionRows.Clear();
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
            _sessionRows.Add(new SourceRow
            {
                Id = id,
                Rank = $"#{i + 1}",
                Name = EntityLabel.Resolve(id, self, _services.PlayerState, _services.CombatLookup, _services.PartyRoster.Members),
                Class = GetClassLine(id),
                Dmg = FormatAmount(value),                                              // primary = metric value
                Dps = FormatAmount(ComputeArchivedDps(value, h.CombatDurationMs)),       // rate = metric / sec
                Pct = FormatPercent(pct),
                Share = pct,
                Role = RoleColorFor(id),
            });
        }
    }

    // ----- pure formatting helpers (carried over from the IMGUI build) -----

    private static long ComputeArchivedDps(long totalDamage, long durationMs)
    {
        long secs = durationMs / 1000; if (secs < 1) secs = 1; return totalDamage / secs;
    }

    // Compact session-list clock: "h:mm" + a single lowercase a/p meridiem (e.g. 2:14p, 11:05a).
    private static string FormatSessionClock(long ms)
    {
        if (ms <= 0) return "—";
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        int hour12 = dt.Hour % 12; if (hour12 == 0) hour12 = 12;
        char ap = dt.Hour < 12 ? 'a' : 'p';
        return $"{hour12}:{dt.Minute:00}{ap}";
    }

    private static string FormatSessionTimestampLong(long ms)
        => ms <= 0 ? "—" : DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("M/d/yyyy, h:mm:ss tt");

    private static string FormatSessionDurationShort(long durationMs)
    {
        long secs = durationMs / 1000; if (secs < 0) secs = 0;
        long m = secs / 60, s = secs % 60;
        return m > 0 ? $"{m}m {s}s" : $"{s}s";
    }

    private static long ComputeSessionMetricTotal(EncounterHistoryEntry h, Metric m)
    {
        long sum = 0;
        foreach (var s in h.Stats.Values) sum += MetricValueOf(s, m);
        return sum;
    }

    private static string FormatPercent(float fraction) => $"{fraction * 100f:F1}%";
}
