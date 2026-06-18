using System;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// The source detail block shown above the per-skill list in the Skill Breakdown window — a two-row grid of
/// metric-appropriate aggregate stats for the drilled source. DPS/HPS show outgoing aggregates (Total, rate,
/// Crit%, Max, Hits, Kills, Uptime, and the "other" metric). Taken mode is metric-aware: its stats are derived
/// from the incoming side (<see cref="SourceStats.TotalTaken"/> + <see cref="SourceStats.IncomingBySkill"/>),
/// since the outgoing crit/kill/uptime fields don't describe damage taken. Values come from the captured
/// <see cref="Plugin.EncounterHistoryEntry"/>, keyed off the metric captured at open time.
/// </summary>
public sealed partial class Plugin
{
    private HudElement BuildSkillDetailBlock() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            DetailStat(() => "TOTAL", () => SkillDetail(0)),
            DetailStat(() => DetailRateKey(), () => SkillDetail(1)),
            DetailStat(() => DetailKey2(), () => SkillDetail(2)),
            DetailStat(() => DetailKeyLuck(), () => SkillDetail(8)),
            DetailStat(() => "MAX", () => SkillDetail(3)),
        }, Gap: 10f),
        new RowElement(new HudElement[]
        {
            DetailStat(() => "HITS", () => SkillDetail(4)),
            DetailStat(() => DetailKey5(), () => SkillDetail(5)),
            DetailStat(() => DetailKey6(), () => SkillDetail(6)),
            DetailStat(() => DetailOtherKey(), () => SkillDetail(7)),
            // Empty 5th cell keeps row 2 column-aligned with row 1 (which gained LUCK %).
            new CellElement(new TextElement(() => ""), Weight: 1f),
        }, Gap: 10f),
    }, Gap: 4f);

    private HudElement DetailStat(Func<string> key, Func<string> value)
        => new CellElement(new ColumnElement(new HudElement[]
        {
            new TextElement(key, MutedCol),
            new TextElement(value, Emphasis: true),
        }, Gap: 0f), Weight: 1f);

    private bool IsTakenDetail => _skillBreakdown?.Metric == Metric.Taken;

    private string DetailRateKey() => _skillBreakdown is { } sb ? MetricRateLabel(sb.Metric) : "DPS";

    // Taken mode replaces the outgoing-only CRIT%/LUCK%/KILLS/UPTIME slots with taken-appropriate labels.
    private string DetailKey2() => IsTakenDetail ? "" : "CRIT %";
    private string DetailKeyLuck() => IsTakenDetail ? "" : "LUCK %";
    private string DetailKey5() => IsTakenDetail ? "SKILLS" : "KILLS";
    private string DetailKey6() => IsTakenDetail ? "" : "UPTIME";

    private string DetailOtherKey() => _skillBreakdown?.Metric == Metric.Hps ? "DMG" : "HEAL";

    // Index → formatted aggregate value for the drilled source under its captured metric.
    private string SkillDetail(int slot)
    {
        if (_skillBreakdown is not { } sb || !sb.Session.Stats.TryGetValue(sb.Source, out var s)) return "—";
        if (sb.Metric == Metric.Taken) return TakenDetail(slot, s, sb.Session.CombatDurationMs);

        long dur = sb.Session.CombatDurationMs;
        long total = MetricValueOf(s, sb.Metric);
        float crit = s.Hits > 0 ? 100f * s.Crits / s.Hits : 0f;
        float luck = s.Hits > 0 ? 100f * s.Luckys / s.Hits : 0f;
        return slot switch
        {
            0 => FormatAmount(total),
            1 => FormatAmount(ComputeArchivedDps(total, dur)),
            2 => $"{crit:F0}%",
            3 => FormatAmount(s.TopHit),
            4 => s.Hits.ToString(),
            5 => s.Kills.ToString(),
            6 => $"{ComputeUptime(s.FirstHitMs, s.LastHitMs, dur) * 100f:F0}%",
            7 => sb.Metric == Metric.Hps ? FormatAmount(s.TotalDamage) : FormatAmount(s.TotalHealing),
            8 => $"{luck:F0}%",
            _ => "—",
        };
    }

    // Taken-mode detail: every stat derives from the incoming side. CRIT%/UPTIME have no taken analogue
    // (deaths aren't tracked) so those slots render "—"; SKILLS counts distinct attacker skills.
    private string TakenDetail(int slot, SourceStats s, long dur)
        => slot switch
        {
            0 => FormatAmount(s.TotalTaken),
            1 => FormatAmount(ComputeArchivedDps(s.TotalTaken, dur)),
            3 => FormatAmount(TakenMaxHit(s)),
            4 => TakenHitCount(s).ToString(),
            5 => s.IncomingBySkill.Count.ToString(),
            7 => FormatAmount(s.TotalHealing),
            _ => "—",   // slots 2 (CRIT %) and 6 (UPTIME) have no taken-side meaning
        };

    /// <summary>Biggest single incoming hit across all attacker skills (Taken-mode "MAX").</summary>
    internal static long TakenMaxHit(SourceStats s)
    {
        long max = 0;
        foreach (var inc in s.IncomingBySkill.Values) if (inc.TopHit > max) max = inc.TopHit;
        return max;
    }

    /// <summary>Total incoming hit count summed across all attacker skills (Taken-mode "HITS").</summary>
    internal static int TakenHitCount(SourceStats s)
    {
        int hits = 0;
        foreach (var inc in s.IncomingBySkill.Values) hits += inc.Hits;
        return hits;
    }
}
