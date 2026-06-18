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

    internal static string MetricColumnLabel(Metric m) => m switch
    {
        Metric.Hps   => "HEAL",
        Metric.Taken => "TAKEN",
        _            => "DMG",
    };

    internal static string MetricRateLabel(Metric m) => m switch
    {
        Metric.Hps   => "HPS",
        Metric.Taken => "DTPS",
        _            => "DPS",
    };

    internal static string MetricAxisTitle(Metric m) => m switch
    {
        Metric.Hps   => "Healing / sec",
        Metric.Taken => "Damage taken / sec",
        _            => "Damage / sec",
    };

    // History-window metric toggle (mirrors Plugin.Header.cs MetricItem).
    private HudElement BuildHistoryMetricRow() => new RowElement(new HudElement[]
    {
        HistoryMetricItem("DPS", Metric.Dps),
        HistoryMetricItem("HPS", Metric.Hps),
        HistoryMetricItem("Taken", Metric.Taken),
    }, Gap: 6f);

    private HudElement HistoryMetricItem(string label, Metric m)
        => new ButtonElement(() => label, () => SelectHistoryMetric(m), Active: () => _historyMetric == m);

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
