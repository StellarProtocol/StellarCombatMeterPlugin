using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;   // UploadPhase

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

    private const int MaxSessionSlots = MaxRetention;   // slot pool sized to the max retention (setting can change at runtime)
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
        public SessionEntry(int idx, string clock, string meta, int[] segments)
        { Index = idx; Clock = clock; Meta = meta; Segments = segments; }
        // Clock = compact "⏱ 2:14p" emphasis line; Meta = pre-joined "{dur} · {n}p · {scene}" muted line.
        // Segments = every archive of the SAME run (levelUuid), OLDEST FIRST so chip 1 is the run's first
        // archive; Index is the one this row opens (the run's main fight).
        public readonly int Index; public readonly string Clock, Meta; public readonly int[] Segments;
    }

    // Segments of the currently selected run, oldest first. Drives the detail pane's segment chips.
    private int[] _selectedSegments = System.Array.Empty<int>();

    // Chip slots for the segment picker. The element tree is built ONCE and polled, so the count is fixed
    // and surplus slots hide via ConditionalElement. 8 covers a fight + a tail per run-end stage + the
    // scene tail several times over; a run with more than 8 archives shows the first 8 and is logged below.
    private const int MaxSegmentChips = 8;

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
            BuildHistorySearchRow(),
            new ConditionalElement(() => _history.Count > 0,
                new ScrollElement(new ListElement(() => _historyView.Count, slots), HistListHeight)),
            BuildClearAllRow(),
        }, Gap: 4f);
    }

    // Footer: the 2-click "Clear all" confirm (hidden when empty); the armed label warns before the 2nd click.
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
            BuildUploadRow(),
            BuildSegmentPicker(),
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

    // ----- manual upload (detail-pane, acts on _selectedSession) -----

    // Upload control row: the per-run upload button + status text + a Copy-link affordance once uploaded.
    // State cycles per entry (UploadStateFor) so switching selection away and back shows the prior result.
    private HudElement BuildUploadRow() => new RowElement(new HudElement[]
    {
        new ButtonElement(UploadButtonLabel, UploadSelectedClicked,
            Active: () => _selectedSession is { } s && UploadStateFor(s) == UploadPhase.InFlight),
        BuildUploadRunButton(),
        // Weighted cell, not a bare NoWrap text + Spacer: the status is often a full run URL, and NoWrap text
        // in an unweighted cell forces the ROW wider instead of clipping. Adding "Upload all" pushed the row
        // 39px past the pane at the default 780f width, clipping Copy link (MEASURED, history sandbox story).
        // A weighted cell absorbs the leftover and lets the URL truncate, so Copy link keeps its place.
        new CellElement(new TextElement(UploadStatusText, MutedCol, NoWrap: true), Weight: 1f),
        new ConditionalElement(() => _selectedSession is { } s && UploadStateFor(s) == UploadPhase.Done,
            new ButtonElement(() => "Copy link", CopyUploadLink)),
    }, Gap: 8f);

    private string UploadButtonLabel()
    {
        if (_selectedSession is not { } s) return SegmentUploadVerb();
        if (s.LevelUuid == 0) return "⚠ No run id";   // pre-update archive: identity wasn't persisted
        return UploadStateFor(s) switch
        {
            UploadPhase.InFlight => "Uploading…",
            UploadPhase.Done     => "✓ Uploaded",
            UploadPhase.Failed   => "✗ Failed — Retry",
            // Not a failure and not retryable: the send was withheld by this content's upload cell.
            UploadPhase.Skipped  => "⃠ Uploads off for this content",
            // Recorded by a build below the server upload floor — the payload's baked-in old pluginVer is
            // 426'd forever (even after upgrading), so this run can't upload and it isn't retryable.
            UploadPhase.Outdated => "⚠ Old-version run",
            _                    => SegmentUploadVerb(),
        };
    }

    private string UploadStatusText()
    {
        if (_selectedSession is not { } s) return "";
        if (s.LevelUuid == 0) return "Archived before run-id was saved — re-run the fight to upload it.";
        if (UploadStateFor(s) == UploadPhase.Skipped)
            return "This content's upload is set to off — the run is still recorded locally. Turn its cell on in Settings to send it.";
        if (UploadStateFor(s) == UploadPhase.Outdated)
            return "Recorded by an out-of-date build — saved locally, but the server won't accept it (even after you update). Update the plugin so new runs upload.";
        return UploadStateFor(s) == UploadPhase.Done && UploadUrlFor(s) is { } u ? ShortRunLabel(u) : "";
    }

    private void UploadSelectedClicked()
    {
        if (_selectedSession is { } s && UploadStateFor(s) != UploadPhase.InFlight) UploadHistoryEntry(s);
    }

    private void CopyUploadLink()
    {
        if (_selectedSession is { } s && UploadUrlFor(s) is { } u)
            UnityEngine.GUIUtility.systemCopyBuffer = u;   // plugins may reference UnityEngine; IL2CPP-safe clipboard
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
             + $"Duration: {FormatRowDuration(RealDurationMs(h.EnteredAtMs, h.ArchivedAtMs), h.CombatDurationMs)}   ·   "
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
        // Full span = REAL elapsed duration (see ChartExtentSeconds): the series run to the archive
        // moment, so anchoring the window on the damage span clipped the chart short of its own data.
        var durationSeconds = _selectedSession is { } h
            ? ChartExtentSeconds(RealDurationMs(h.EnteredAtMs, h.ArchivedAtMs), h.CombatDurationMs)
            : 0f;
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
        { StartVisible = false, Closable = true, Draggable = true,
          Resizable = true, MinWidth = 600f, MinHeight = 480f, MaxWidth = 1200f, MaxHeight = 1000f,
          ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                            && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
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


    // Auto-archive segments show WHY they ended; manual/scene stay untagged (pre-v10 default).
    // Finding 5 (review round 2026-07-27): this is a SEPARATE allow-list from ArchiveReasonTag's
    // switch — a reason can be mapped there and still render with no suffix here if forgotten (as
    // "bosskill" was), indistinguishable from a manual archive. internal (not private) so
    // TriggerSuffix_covers_every_auto_reason can pin completeness against every ArchiveReason value.
    internal static string TriggerSuffix(string trigger)
        => trigger is "wipe" or "boss" or "idle" or "stage" or "bosskill" or "boundary" or "clear" or "prepare" ? $" · {trigger}" : "";

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
            // Archived rows read identity/class from the FROZEN per-player snapshot — re-resolving live here gave
            // "Player#<uid>" and a blank class once the players left AOI / the scene changed. Fall back to live
            // resolution only when the session predates the snapshot (legacy entries) or a source wasn't frozen.
            var frozen = h.Entities.TryGetValue(id, out var es) ? es : null;
            _sessionRows.Add(new SourceRow
            {
                Id = id,
                Rank = $"#{i + 1}",
                Name = !string.IsNullOrEmpty(frozen?.Name)
                    ? frozen!.Name!
                    : EntityLabel.Resolve(id, self, _services.PlayerState, _services.CombatLookup, _services.PartyRoster.Members),
                Class = ResolveArchivedClassLine(frozen, h.EnteredAtMs, h.ArchivedAtMs, id),
                Dmg = FormatAmount(value),                                              // primary = metric value
                Dps = FormatAmount(ComputeArchivedDps(value, h.CombatDurationMs)),       // rate = metric / sec
                Pct = FormatPercent(pct),
                Share = pct,
                Role = RoleColorFor(id),
            });
        }
    }

    // Class label for an ARCHIVED row: EVERY class the player actually played in THIS archive, from
    // the frozen classSpans clamped to the archive's own [EnteredAtMs, ArchivedAtMs] window, joined in
    // play order (e.g. "Verdant Oracle · Frost Mage"). The single frozen professionId (attr 220 at
    // bank time) can't express "was Oracle, ended as Frost Mage", so a clear-phase archive banked
    // after a swap mislabelled the player as the boss class (the LUz6opkvNX bug). A single-class
    // archive bakes no spans → falls back to the frozen professionId (unchanged for the common case);
    // an unfrozen/legacy row falls back to the live class line.
    private string ResolveArchivedClassLine(EntitySnapshot? frozen, long startMs, long endMs, EntityId id)
    {
        if (frozen != null)
        {
            var profs = ClassesPlayedInWindow(frozen, startMs, endMs);
            if (profs.Count > 0)
            {
                var line = ProfessionDisplayName(profs[0]);
                for (var i = 1; i < profs.Count; i++) line += " · " + ProfessionDisplayName(profs[i]);
                return line;
            }
            if (ResolveSnapProfession(frozen) is { Length: > 0 } fc) return fc;
        }
        return GetClassLine(id);
    }

    // ProfessionId → display name (mirrors ResolveSnapProfession's resolution for a single id).
    private string ProfessionDisplayName(int profId)
    {
        var prof = _services.GameData.Combat.GetProfession(profId);
        return prof is { Name: { Length: > 0 } n } ? n : $"Class {profId}";
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

    /// <summary>
    /// Real elapsed span of an archived segment: archive time minus combat start. Both fields are
    /// already persisted on every entry, so this is derivable retroactively — no schema change.
    ///
    /// This exists because <c>CombatDurationMs</c> is the DAMAGE-HIT SPAN, not elapsed time:
    /// <c>FirstHitMs</c>/<c>LastHitMs</c> are written only in the damage handler
    /// (<c>Plugin.Capture.cs</c>), so healing and damage-taken never move them. A heal-only tail has a
    /// legitimate span of 0 while seconds of wall-clock pass — which is why the owner saw `0s` rows on
    /// archives that plainly covered real time (report 2026-07-28, run 890357114281656320).
    ///
    /// Returns 0 when no combat start was recorded (otherwise <c>arch - 0</c> would report ~56 years)
    /// and clamps a backwards server clock.
    /// </summary>
    internal static long RealDurationMs(long enteredAtMs, long archivedAtMs)
    {
        if (enteredAtMs <= 0) return 0;
        var span = archivedAtMs - enteredAtMs;
        return span > 0 ? span : 0;
    }

    /// <summary>
    /// History-row duration label: real elapsed span, with the combat span in parentheses when the two
    /// differ — `8.3s (0s combat)`. Format chosen by the owner 2026-07-28. Under a minute it carries one
    /// decimal (their example was `8.3s`); at a minute and above it uses the existing m/s style. The
    /// parenthetical is omitted when both render identically, so ordinary fights stay uncluttered.
    ///
    /// Display only — <c>CombatDurationMs</c> is reported verbatim and never adjusted, because DPS
    /// divides by it.
    /// </summary>
    internal static string FormatRowDuration(long realMs, long combatMs)
    {
        if (realMs < 0) realMs = 0;
        if (combatMs < 0) combatMs = 0;
        // Suffix suppressed when both land on the same whole second — otherwise every ordinary fight
        // would repeat its own number. Compared on the VALUES, not the rendered strings, so the two
        // formats (tenths vs m/s) can't disagree about equality.
        if (realMs / 1000 == combatMs / 1000) return FormatDurationWithTenths(realMs);
        // Combat span uses the same whole-second formatter as the rest of the UI, so a 0 span reads
        // "0s combat" exactly as the owner specified — not "0.0s".
        return $"{FormatDurationWithTenths(realMs)} ({FormatSessionDurationShort(combatMs)} combat)";
    }

    /// <summary>
    /// Full x-extent for the session chart, in seconds. Uses the REAL elapsed span, not the damage
    /// span: the timeline series are bucketed relative to <c>_combatStartMs</c> (= <c>EnteredAtMs</c>,
    /// see <c>Plugin.Capture.cs</c>'s <c>TimelineFor(...).Add(..., _combatStartMs, ...)</c>), so the
    /// series domain genuinely runs to the archive moment. Showing only the damage span cut the chart
    /// short of the data it holds — owner ruling 2026-07-28: if the row reports real duration, the
    /// graph must cover the whole duration too.
    ///
    /// Falls back to the combat span when no combat start was recorded, so the chart is never 0-wide.
    /// DPS/HPS rates are NOT affected — those divide by <c>CombatDurationMs</c> and are untouched.
    /// </summary>
    internal static float ChartExtentSeconds(long realMs, long combatMs)
    {
        var ms = realMs > 0 ? realMs : combatMs;
        return ms > 0 ? ms / 1000f : 0f;
    }

    // One decimal below a minute, whole seconds in m/s form above it.
    private static string FormatDurationWithTenths(long ms)
    {
        if (ms < 0) ms = 0;
        if (ms < 60_000) return $"{ms / 1000f:0.0}s";
        long secs = ms / 1000, m = secs / 60, s = secs % 60;
        return $"{m}m {s}s";
    }

    private static long ComputeSessionMetricTotal(EncounterHistoryEntry h, Metric m)
    {
        long sum = 0;
        foreach (var s in h.Stats.Values) sum += MetricValueOf(s, m);
        return sum;
    }

    private static string FormatPercent(float fraction) => $"{fraction * 100f:F1}%";
}
