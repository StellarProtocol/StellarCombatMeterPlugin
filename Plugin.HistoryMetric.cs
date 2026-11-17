using System;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// History-window metric state + the pure metric→value/label selectors. The history surface owns its own
/// metric (<see cref="_historyMetric"/>) independent of the live meter's <c>_metric</c>: switching it re-sorts /
/// re-labels the contribution table and swaps the chart series in place (the chart re-pulls its Y ticks live).
/// The selectors are <c>static</c> + pure so the projection logic is unit-testable without a Unity host.
/// </summary>
public sealed partial class Plugin
{
    private Metric _historyMetric = Metric.Dps;

    internal static long MetricValueOf(SourceStats s, Metric m) => m switch
    {
        Metric.Hps   => s.TotalHealing,
        Metric.Taken => s.TotalTaken,
        _            => s.TotalDamage,
    };

    // Pure metric→catalog-key mappers (static, unit-testable); the instance labels below resolve them via _loc
    // so table headers / axis titles switch language live. DPS/HPS/DTPS acronyms are kept per language.
    internal static string MetricColumnKey(Metric m) => m switch
    {
        Metric.Hps   => "list.col.heal",
        Metric.Taken => "list.col.taken",
        _            => "list.col.dmg",
    };
    internal static string MetricRateKey(Metric m) => m switch
    {
        Metric.Hps   => "list.rate.hps",
        Metric.Taken => "list.rate.dtps",
        _            => "list.rate.dps",
    };
    internal static string MetricAxisKey(Metric m) => m switch
    {
        Metric.Hps   => "history.axis.heal",
        Metric.Taken => "history.axis.taken",
        _            => "history.axis.dmg",
    };

    private string MetricColumnLabel(Metric m) => _loc.T(MetricColumnKey(m));
    private string MetricRateLabel(Metric m)   => _loc.T(MetricRateKey(m));
    private string MetricAxisTitle(Metric m)   => _loc.T(MetricAxisKey(m));

    // History-window metric switch: the house segmented control (DPS / HPS / Taken) in the chart panel's caption row.
    // Tab order = the Metric enum order (Dps, Hps, Taken).
    private static readonly Metric[] HistoryMetricTabs = { Metric.Dps, Metric.Hps, Metric.Taken };

    private HudElement BuildHistoryMetricTabs() => TabStrip(new Func<string>[]
        {
            () => _loc.T("list.metric.dps"), () => _loc.T("list.metric.hps"), () => _loc.T("list.metric.taken"),
        },
        () => Array.IndexOf(HistoryMetricTabs, _historyMetric),
        i => SelectHistoryMetric(HistoryMetricTabs[i]));

    // True while a metric switch recomputes the table rows + chart series. Today that work runs synchronously on the
    // click (it is O(players + buckets) of plain managed maths), so the flag is only ever set inside this method — it is
    // the hook a future "Loading…" overlay keys on if a measured switch ever proves slow (owner 2026-11-17, design
    // pending: child window vs a framework layer element).
    private bool _historyLoading;

    // IN PLACE — the window is never torn down (fix 2026-11-17, "History disappears and comes back when I switch DPS →
    // HPS"). It used to Remove() + Register() the whole window so the LineChart's "baked" Y ticks would rescale; but
    // History is no longer prebuilt (crash fix 61935ab), so the fresh window had no tree until the framework built and
    // mounted it on a later tick — the window vanished in between, and the run-list / segment scroll positions reset.
    // That rebuild has been unnecessary since framework 1.13 (8fcab9a4): the chart's Y tick labels and both axis titles
    // are re-pulled live every refresh. Everything metric-dependent here is a Func or a keyed cache: the table header
    // (MetricColumnLabel via FmtCache), the rows (RebuildSessionRows), the chart hint (ChartHint keyed on the metric) and
    // the series (BuildChartSeries keyed on the metric → a new list instance → the chart re-meshes). The selection,
    // charted players and zoom window are untouched.
    private void SelectHistoryMetric(Metric m)
    {
        if (_historyMetric == m) return;
        _historyMetric = m;
        _historyLoading = true;
        long t0 = CmDiag.HistPerf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        RebuildSessionRows();   // re-sort/re-label the contribution table
        BuildChartSeries();     // prime the series cache now (the chart's next poll gets this same list — no double work)
        _historyLoading = false;
        if (CmDiag.HistPerf)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _services.Log.Info($"[HistPerf] metric switch → {m}: {ms:0.00} ms (rows={_sessionRows.Count}, " +
                $"sources={_selectedSession?.Series.Count ?? 0}, charted={_chartedSources.Count})");
        }
    }
}
