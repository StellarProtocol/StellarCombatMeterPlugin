using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// History-window metric state + the pure metric→value/label selectors. The history surface owns its own
/// metric (<see cref="_historyMetric"/>) independent of the live meter's <c>_metric</c>: switching it re-sorts /
/// re-labels the contribution table and (Task 3) rebuilds the chart subtree so the chart's baked Y axis rescales.
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

    // History-window metric toggle (mirrors Plugin.Header.cs MetricItem).
    private HudElement BuildHistoryMetricRow() => new RowElement(new HudElement[]
    {
        HistoryMetricItem("list.metric.dps", Metric.Dps),
        HistoryMetricItem("list.metric.hps", Metric.Hps),
        HistoryMetricItem("list.metric.taken", Metric.Taken),
    }, Gap: 6f);

    private HudElement HistoryMetricItem(string key, Metric m)
        => new ButtonElement(() => _loc.T(key), () => SelectHistoryMetric(m), Active: () => _historyMetric == m);

    private void SelectHistoryMetric(Metric m)
    {
        if (_historyMetric == m) return;
        _historyMetric = m;
        RebuildSessionRows();   // re-sort/re-label the contribution table
        // The LineChartElement bakes its Y-tick label values (and X tick labels) at element-BUILD time, so a
        // plain MarkDirty (which only re-polls values) leaves the axis scaled to the previous metric's
        // magnitude. Tear the window down + re-register a fresh tree so BuildYTicks re-derives from the new
        // metric's peak — the framework-sanctioned Remove()+Register() rebuild (mirrors StatInspector's
        // column-count change; see WindowService.Register).
        RebuildHistoryWindow();
    }
}
