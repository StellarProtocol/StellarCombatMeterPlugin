using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;   // UploadPhase, UploadPolicy

namespace Stellar.CombatMeter;

// Combat History — the right column (approved redesign §4 + addendum "run panel B"): three SectionPanels, top to bottom:
//   • run panel (Plugin.HistoryWindow.RunPanel.cs): bold map title + muted "date · N players · M segments"; the status
//     line (plain text, never a pill) or the Primary "Upload segment" pill · Neutral "Upload all (N)" · round Copy link /
//     Delete icon buttons; then the SEGMENTS cards (Plugin.HistoryWindow.Segments.cs);
//   • chart panel: DPS/HPS/Taken segmented control + muted hint, the LineChart + brush;
//   • player table: muted UPPERCASE header, 34 px tiles (class-colour share wash, crest, bold "#1 Name" + muted spec,
//     numbers, round ⌕ inspect, round › drill-in; a charted row = accent ring).
// Every string a Func reads is cached (rebuilt on selection / metric / language / upload-state change).
public sealed partial class Plugin
{
    private string _runTitle = "", _runMeta = "";

    private HudElement BuildHistoryDetail() => new ColumnElement(new HudElement[]
    {
        BuildRunPanel(),
        // The "Loading" overlay (Plugin.Confirm.cs) covers the chart + table only; the old content stays underneath.
        WithHistoryLoadingOverlay(new ColumnElement(new HudElement[]
        {
            BuildChartPanel(),
            BuildTablePanel(),
        }, Gap: HistGap)),
    }, Gap: HistGap);

    // Selection-dependent header strings, the segment card texts and the upload block — on selection / metric /
    // language change only (called from RebuildSessionRows).
    private void RebuildRunHeader()
    {
        Array.Fill(_segTitles, "");
        Array.Fill(_segTimes, "");
        RefreshUploadBlock();
        if (_selectedSession is not { } h)
        {
            _runTitle = _history.Count == 0 ? "" : _loc.T("history.pickRun");
            _runMeta = "";
            RefreshRunUploadState(force: true);
            return;
        }
        _runTitle = ResolveSceneName(h.SceneName);
        var when = h.ArchivedAtMs <= 0 ? "—"
            : DateTimeOffset.FromUnixTimeMilliseconds(h.ArchivedAtMs).LocalDateTime
                .ToString(_loc.T("history.date.format"), CultureInfo.InvariantCulture);
        int segs = Math.Max(1, _selectedSegments.Length);
        _runMeta = when + " · " + _loc.TFormat(PluralKey(h.MemberCount, "history.playersOne", "history.players"), h.MemberCount) + " · "
                 + (segs == 1 ? _loc.T("history.run.segmentOne") : _loc.TFormat("history.run.segments", segs));
        RebuildSegmentTexts();
        RefreshRunUploadState(force: true);
    }

    // ── upload ────────────────────────────────────────────────────────────────────────────────────────────────────
    // Why the selected archive can't be uploaded right now → no Upload pill; the status line reads "Not uploaded · <why>"
    // (rule 22: status is plain text). Re-evaluated on the ~10 Hz snapshot tick (the policy can change in Settings
    // while History is open).
    private enum UploadBlock { None, NoRunId, OldRun, Update, Off }
    private UploadBlock _uploadBlock;

    private void RefreshUploadBlock()
    {
        if (_selectedSession is not { } s) { _uploadBlock = UploadBlock.None; return; }
        // Same gates, same order, as UploadHistoryEntry: below the server floor, an Outdated run (its baked-in old
        // pluginVer is 426'd forever), the content's stats cell off, a pre-run-id archive (/run/0 would collide).
        _uploadBlock = UploadBelowFloor ? UploadBlock.Update
            : UploadStateFor(s) == UploadPhase.Outdated ? UploadBlock.OldRun
            : !UploadPolicy.Allows(EffectivePolicyFor(s, UploadArtifact.Stats), UploadTrigger.Manual) ? UploadBlock.Off
            : s.LevelUuid == 0 ? UploadBlock.NoRunId
            : UploadBlock.None;
    }

    private bool CanUploadSelected() => _selectedSession != null && _uploadBlock == UploadBlock.None;

    // ── chart panel ───────────────────────────────────────────────────────────────────────────────────────────────
    private Metric _chartHintMetric = (Metric)(-1);
    private string? _chartHintLang;
    private string _chartHint = "";

    private HudElement BuildChartPanel() => PanelBox(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            new CellElement(BuildHistoryMetricTabs(), Weight: 1f),
            new TextElement(ChartHint, Color: () => MutedColor(), NoWrap: true),
        }, Gap: 6f),
        // `nohistchart` diag switch (Plugin.Diag.cs): same height, no injected ChartGraphic.
        CmDiag.NoHistChart ? new SpacerElement(1f, 150f) : BuildHistoryChart(),
    });

    // "Damage / sec · click a player to add their line" — composed once per metric / language.
    private string ChartHint()
    {
        string lang = _loc.Language;
        if (_historyMetric == _chartHintMetric && ReferenceEquals(lang, _chartHintLang)) return _chartHint;
        _chartHintMetric = _historyMetric; _chartHintLang = lang;
        return _chartHint = MetricAxisTitle(_historyMetric) + " · " + _loc.T("history.chart.hint");
    }

    // The timeline chart: team-total (always) + a line per source toggled into _chartedSources. Axis scale + Y title
    // follow _historyMetric; rebuilt (not refreshed) on metric change so the baked axis rescales.
    private HudElement BuildHistoryChart() => new LineChartElement(
        Series:          BuildChartSeries,
        BucketSeconds:   () => _selectedSession is { } h ? SeriesBucketSeconds(h) : 1f,
        FormatY:         v => FormatAmount((long)v),
        FormatX:         FormatChartX,
        TitleY:          () => MetricAxisTitle(_historyMetric),
        TitleX:          () => _loc.T("history.chart.xTitle"),
        VisibleRange:    () => _chartVisibleRange,
        SetVisibleRange: r => _chartVisibleRange = r,
        Width:           HistDetailW - 2f * PanelPadX,
        Height:          150f,
        ShowNavigator:   true,    // Highcharts-style overview + brush
        FillWidth:       true);

    private static string FormatSeconds(float s)
    {
        var total = (int)(s < 0f ? 0f : s);
        return $"{total / 60}:{total % 60:00}";
    }

    // Span-aware X tick label: short encounters get sub-second / integer-second precision so the few visible ticks stay
    // distinct (a short span otherwise collapses every tick to "0:00"); longer ranges fall back to the m:ss clock.
    private string FormatChartX(float v)
    {
        var span = _chartVisibleRange.Max - _chartVisibleRange.Min;
        if (span < 3f) return $"{v:0.0}s";
        if (span < 60f) return $"{(int)(v < 0f ? 0f : v)}s";
        return FormatSeconds(v);
    }

    // ── label width estimate ──────────────────────────────────────────────────────────────────────────────────────
    // Drawn width of a BOLD label at `fontSize` (SDL §6 budget at 12: Latin ≈ 7.5 px/char, CJK ≈ 12.5, Thai ≈ 8 per code
    // point; scaled linearly), + 4 px slack. Build time only — pill widths are fixed from it, never per refresh.
    private static float EstimateBoldLabelW(string label, int fontSize)
    {
        float w = 0f;
        foreach (char c in label) w += c >= 0x2E80 ? 12.5f : c is >= '฀' and <= '๿' ? 8f : 7.5f;
        return MathF.Ceiling(w * fontSize / 12f) + 4f;
    }

    private static float MaxBoldLabelW(int fontSize, params string[] labels)
    {
        float w = 0f;
        foreach (var l in labels) w = MathF.Max(w, EstimateBoldLabelW(l, fontSize));
        return w;
    }
}
