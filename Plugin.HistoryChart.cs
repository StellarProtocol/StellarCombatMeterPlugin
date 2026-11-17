using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// Projects the selected session's frozen per-source <see cref="SourceSeries"/> into <see cref="ChartSeries"/>
/// for the history timeline chart: always a muted team-total line (summed per bucket at build) plus one
/// line per source toggled onto the chart via <see cref="ToggleChartSource"/>, in its archived gauge colour (class or
/// role, per Appearance › Bar colour) — or, when that colour is already on the chart, the next unused fallback colour.
///
/// <para><b>Ref-diff caching:</b> the chart's binding re-meshes only when its <c>Series()</c> reference
/// changes (<c>ReferenceEquals</c>). So <see cref="BuildChartSeries"/> returns a cached list across frames
/// where nothing changed (skipping the per-frame remesh), and builds a <em>new</em> <see cref="List{T}"/>
/// instance whenever its small dirty signature changes — the selected session reference, the history metric,
/// or a version int bumped by <see cref="ToggleChartSource"/>. A fresh instance (rather than mutating the
/// cached one in place) is what lets the ref-diff observe a toggle-after-mount and re-mesh the chart.</para>
/// </summary>
public sealed partial class Plugin
{
    private const int MaxChartSources = 8;     // top-N series captured per spec §5.2
    private readonly ChartedSet _chartedSources = new();   // rows toggled onto the chart, in toggle order
    private (float Min, float Max) _chartVisibleRange;

    // The always-present team-total line (sits above the role-coloured source lines) uses the active theme's
    // neutral menu-text colour, read at build so it follows the active/custom theme (the chart rebuilds on
    // language change and re-skins on a full theme switch). Source lines keep their registry GaugeColorFor.
    private ColorRgba TeamTotalColor => _services.Theme.Colors.MenuText;

    // Stable chart-series buffer + its dirty signature. The cached list INSTANCE is replaced (not mutated in
    // place) on each rebuild so the chart's ChartBinding ref-diff (ReferenceEquals on Series()) detects the
    // change and re-meshes; an unchanged frame returns the SAME instance to skip the per-frame remesh.
    private List<ChartSeries> _chartSeries = new();
    private EncounterHistoryEntry? _chartSeriesSession;
    private Metric _chartSeriesMetric = (Metric)(-1);
    private int _chartSeriesBuiltVersion = -1;
    private int _chartSourcesVersion;             // bumped by ToggleChartSource

    private static long[] ChannelOf(SourceSeries s, Metric m) => m switch
    {
        Metric.Hps   => s.Healing,
        Metric.Taken => s.Taken,
        _            => s.Dealt,
    };

    internal static long[] TeamTotalSeries(EncounterHistoryEntry h, Metric m)
    {
        int len = 0;
        foreach (var s in h.Series.Values)
        {
            var ch = ChannelOf(s, m);
            if (ch.Length > len) len = ch.Length;
        }
        var total = new long[len];
        foreach (var s in h.Series.Values)
        {
            var ch = ChannelOf(s, m);
            for (int i = 0; i < ch.Length; i++) total[i] += ch[i];
        }
        return total;
    }

    // Build the ChartSeries set: team total (emphasis) + one per toggled source present in the session.
    // Returns the SAME cached instance on a clean frame; a NEW list instance whenever the dirty signature
    // changed, so the chart's ReferenceEquals ref-diff observes the change and re-meshes (see class summary).
    private IReadOnlyList<ChartSeries> BuildChartSeries()
    {
        if (!ChartSeriesDirty()) return _chartSeries;

        var series = new List<ChartSeries>();   // fresh instance so the chart's ref-diff re-meshes
        _chartSeriesSession      = _selectedSession;
        _chartSeriesMetric       = _historyMetric;
        _chartSeriesBuiltVersion = _chartSourcesVersion;
        _chartSeriesBarColor     = ActiveToggles().BarColor;

        if (_selectedSession is not { } h) { _chartSeries = series; return _chartSeries; }

        long teamTotalValue = ComputeSessionMetricTotal(h, _historyMetric);
        series.Add(new ChartSeries(
            _loc.T("history.chart.teamTotal"), TeamTotalColor,
            ToFloat(SeriesOrBucketZero(TeamTotalSeries(h, _historyMetric), teamTotalValue)),
            Emphasis: true));

        // Name + colour from the FROZEN snapshot, exactly like the table row (ArchivedName / ArchivedProfession). The live
        // EntityLabel.Resolve + GaugeColorFor(id) used before gave "Player#<uid>" and — the live class being unknown once
        // a player left AOI, which RoleClassifier reads as DPS — the same red line for everyone.
        EntityId self = _services.CombatSnapshot.LocalEntityId;
        _chartColors.Clear();
        _chartUsed.Clear();
        _chartUsed.Add(TeamTotalColor);   // no source line may look like the team total either
        foreach (var id in _chartedSources)   // charted order: an earlier line keeps its colour, a later clash moves
            if (h.Series.TryGetValue(id, out var s))
            {
                var frozen = h.Entities.TryGetValue(id, out var es) ? es : null;
                var color = DistinctChartColor(ArchivedGaugeColor(ArchivedProfession(frozen, id)));
                _chartColors[id] = color;
                long sourceTotal = h.Stats.TryGetValue(id, out var st) ? MetricValueOf(st, _historyMetric) : 0L;
                series.Add(new ChartSeries(
                    ArchivedName(frozen, id, self),
                    color,
                    ToFloat(SeriesOrBucketZero(ChannelOf(s, _historyMetric), sourceTotal))));
            }

        _chartSeries = series;
        return _chartSeries;
    }

    private bool ChartSeriesDirty()
        => !ReferenceEquals(_chartSeriesSession, _selectedSession)
           || _chartSeriesMetric != _historyMetric
           || _chartSeriesBuiltVersion != _chartSourcesVersion
           || _chartSeriesBarColor != ActiveToggles().BarColor;   // Appearance › Bar colour switched while open

    // ── charted-line colours ─────────────────────────────────────────────────────────────────────────────────────
    // Per charted source, filled on a series rebuild only; the table's charted-row outline reads it (one dictionary
    // lookup per poll, no allocation) so a row's ring matches its line.
    private readonly Dictionary<EntityId, ColorRgba> _chartColors = new();
    private readonly List<ColorRgba> _chartUsed = new();
    private BarColorMode _chartSeriesBarColor = (BarColorMode)(-1);

    // Lines that would repeat a colour already on the chart take the next unused one of these instead. Fixed (not
    // theme colours) and mid-bright, so every one reads on the dark plot and none is near-white (the team total).
    private static readonly ColorRgba[] ChartFallbackColors =
    {
        new(0.31f, 0.76f, 0.97f, 1f),   // sky blue
        new(1.00f, 0.72f, 0.30f, 1f),   // amber
        new(0.73f, 0.41f, 0.78f, 1f),   // violet
        new(0.51f, 0.78f, 0.52f, 1f),   // green
        new(0.94f, 0.38f, 0.57f, 1f),   // pink
        new(1.00f, 0.95f, 0.46f, 1f),   // yellow
        new(0.30f, 0.71f, 0.67f, 1f),   // teal
        new(0.90f, 0.45f, 0.45f, 1f),   // coral
        new(0.63f, 0.53f, 0.50f, 1f),   // taupe
        new(0.56f, 0.64f, 0.98f, 1f),   // periwinkle
    };

    // The archived row's gauge colour under the active Appearance › Bar colour: the class colour (Class mode, class
    // known), else the role colour of the ARCHIVED class — same rule as the live GaugeColorFor, minus the live lookup.
    private ColorRgba ArchivedGaugeColor(int prof)
    {
        if (_chartSeriesBarColor == BarColorMode.Class && ClassPalette.TryGet(prof, out var c)) return c;
        return (RoleClassifier.Classify(prof) switch
        {
            Role.Tank   => _roleTankSlot,
            Role.Healer => _roleHealerSlot,
            _           => _roleDpsSlot,
        }).Value;
    }

    private ColorRgba DistinctChartColor(ColorRgba wanted)
    {
        var pick = wanted;
        if (IsChartColorUsed(pick))
        {
            pick = ChartFallbackColors[_chartUsed.Count % ChartFallbackColors.Length];   // all taken → cycle
            foreach (var f in ChartFallbackColors)
                if (!IsChartColorUsed(f)) { pick = f; break; }
        }
        _chartUsed.Add(pick);
        return pick;
    }

    private bool IsChartColorUsed(ColorRgba c)
    {
        foreach (var u in _chartUsed)
            if (MathF.Abs(u.R - c.R) + MathF.Abs(u.G - c.G) + MathF.Abs(u.B - c.B) < 0.15f) return true;
        return false;
    }

    /// <summary>The line colour of a charted source (null = not on the chart), for the table row's outline.</summary>
    private ColorRgba? ChartColorOf(EntityId id) => _chartColors.TryGetValue(id, out var c) ? c : null;

    // Sub-bucket encounters never accumulate a bucketed timeline (the encounter ended before the first bucket
    // closed), so the per-bucket channel is empty and the chart would scan an all-zero/empty window → peak 0 →
    // the degenerate 1/0/0/0 Y axis. Fall back to a single bucket-0 point carrying the source's metric total so
    // the peak (and therefore the axis) reflects real data. Non-empty channels pass through unchanged.
    internal static long[] SeriesOrBucketZero(long[] channel, long fallbackTotal)
        => channel.Length > 0 ? channel : new[] { fallbackTotal };

    private float SeriesBucketSeconds(EncounterHistoryEntry h)
    {
        foreach (var s in h.Series.Values) return s.BucketMs / 1000f;
        return 1f;
    }

    private static IReadOnlyList<float> ToFloat(long[] a)
    {
        var f = new float[a.Length];
        for (int i = 0; i < a.Length; i++) f[i] = a[i];
        return f;
    }

    private void ToggleChartSource(EntityId id)
    {
        if (!_chartedSources.Remove(id)) _chartedSources.Add(id);
        _chartSourcesVersion++;   // mark the cached series stale
    }
}

/// <summary>The charted sources in TOGGLE order (a HashSet reuses freed slots, so after a remove its enumeration order
/// is not the order the lines were added — and the colour de-dupe keeps the EARLIER line's colour). At most a handful
/// of entries, so a list scan is cheaper than hashing. The struct enumerator keeps <c>foreach</c> allocation-free.</summary>
internal sealed class ChartedSet
{
    private readonly List<EntityId> _ids = new();
    public int Count => _ids.Count;
    public bool Contains(EntityId id) => _ids.Contains(id);
    public void Add(EntityId id) { if (!_ids.Contains(id)) _ids.Add(id); }
    public bool Remove(EntityId id) => _ids.Remove(id);
    public void Clear() => _ids.Clear();
    public List<EntityId>.Enumerator GetEnumerator() => _ids.GetEnumerator();
}
