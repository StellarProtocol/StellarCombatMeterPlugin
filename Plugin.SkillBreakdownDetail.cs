using System;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// The TOTALS panel of the Skill Breakdown window (approved redesign §5): a 5 × 3 grid of stat tiles (muted bold
/// caption over a bold value; a blank cell shows a muted "—") with fixed widths, so values never shift. DPS/HPS show
/// outgoing aggregates (Total, rate, Crit%, Luck%, Max, Hits, Kills, Uptime, the "other" metric, the ZDPS-parity
/// splits). Taken mode is metric-aware: its stats are derived from the incoming side (<see cref="SourceStats.TotalTaken"/>
/// + <see cref="SourceStats.IncomingBySkill"/>), since the outgoing crit/kill/uptime fields don't describe damage
/// taken. Values come from the captured <see cref="Plugin.EncounterHistoryEntry"/>, keyed off the metric captured at
/// open time, and are formatted ONCE per open / language change (<see cref="RebuildSkillTotals"/>), never per refresh.
/// </summary>
public sealed partial class Plugin
{
    private const int   SkillStatCells = 15;
    private const float SkillStatGap = 4f;
    private const float SkillStatW = (SkillWindowW - 24f - 2f * PanelPadX - 4f * SkillStatGap) / 5f;

    // Grid order → value slot (SkillDetail). Row 1: total · rate · crit · luck · max; row 2: hits · kills/skills · uptime ·
    // the other metric · active DPS (overheal in HPS); row 3: the crit / lucky / crit-lucky splits · shield break
    // (effective in HPS) · blank.
    private static readonly int[] SkillStatOrder = { 0, 1, 2, 8, 3, 4, 5, 6, 7, 13, 9, 10, 11, 12, -1 };

    private readonly string[] _sbCap = NewFilled(SkillStatCells, "");
    private readonly string[] _sbVal = NewFilled(SkillStatCells, "—");
    private readonly bool[]   _sbBlank = new bool[SkillStatCells];

    private HudElement BuildTotalsPanel()
    {
        var rows = new HudElement[3];
        for (int r = 0; r < 3; r++)
        {
            var cells = new HudElement[5];
            for (int c = 0; c < 5; c++) cells[c] = SkillStatTile(r * 5 + c);
            rows[r] = new RowElement(cells, Gap: SkillStatGap);
        }
        return SectionPanel(() => _loc.T("skill.totals"), null, new ColumnElement(rows, Gap: SkillStatGap));
    }

    // One 42 px stat tile: caption muted bold 11, value bold 14 (muted "—" when the cell is blank); both texts fixed to
    // the tile width and centred, so a value change never moves its neighbours.
    private HudElement SkillStatTile(int i) => new CellElement(new BubbleElement(new ColumnElement(new HudElement[]
        {
            new TextElement(() => _sbCap[i], Color: () => MutedColor(), Width: SkillStatW, Align: TextAlign.Center,
                NoWrap: true, FontSize: 11) { Bold = true, Ellipsize = true },
            new TextElement(() => _sbVal[i], Color: () => _sbBlank[i] ? MutedColor() : TextColor(), Width: SkillStatW,
                Align: TextAlign.Center, NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
        }, Gap: 0.01f))
    {
        FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 4f, MaxWidth = SkillStatW,
    }, Width: SkillStatW);

    private void RebuildSkillTotals()
    {
        for (int i = 0; i < SkillStatCells; i++)
        {
            int slot = SkillStatOrder[i];
            string cap = slot < 0 ? "" : SkillDetailCaption(slot);
            _sbBlank[i] = cap.Length == 0;
            _sbCap[i] = cap.ToUpperInvariant();
            _sbVal[i] = _sbBlank[i] ? "—" : SkillDetail(slot);
        }
    }

    // Value slot → caption (catalog keys are already upper-case in English; re-cased for the grid anyway). "" = the cell
    // has no meaning in this metric (blank).
    private string SkillDetailCaption(int slot)
    {
        bool taken = IsTakenDetail;
        return slot switch
        {
            0 => _loc.T("skill.detail.total"),
            1 => DetailRateKey(),
            2 => taken ? "" : _loc.T("skill.detail.critPct"),
            3 => _loc.T("skill.detail.max"),
            4 => _loc.T("skill.col.hits"),
            5 => taken ? _loc.T("skill.detail.skills") : _loc.T("skill.detail.kills"),
            6 => taken ? "" : _loc.T("skill.detail.uptime"),
            7 => DetailOtherKey(),
            8 => taken ? "" : _loc.T("skill.detail.luckPct"),
            >= 9 and <= 13 => DetailSplitKey(slot - 9),
            _ => "",
        };
    }

    // Row-3 labels per metric (Taken has no outgoing splits).
    private string DetailSplitKey(int col)
    {
        if (_skillBreakdown is not { } sb || sb.Metric == Metric.Taken) return "";
        return sb.Metric == Metric.Hps
            ? col switch { 0 => _loc.T("skill.detail.cheal"), 1 => _loc.T("skill.detail.lheal"), 2 => _loc.T("skill.detail.clheal"), 3 => _loc.T("skill.detail.effective"), _ => _loc.T("skill.detail.overheal") }
            : col switch { 0 => _loc.T("skill.detail.cdmg"), 1 => _loc.T("skill.detail.ldmg"), 2 => _loc.T("skill.detail.cldmg"), 3 => _loc.T("skill.detail.shldBrk"), _ => _loc.T("skill.detail.active") };
    }

    private bool IsTakenDetail => _skillBreakdown?.Metric == Metric.Taken;

    private string DetailRateKey() => _skillBreakdown is { } sb ? MetricRateLabel(sb.Metric) : _loc.T("list.rate.dps");

    private string DetailOtherKey() => _skillBreakdown?.Metric == Metric.Hps ? _loc.T("list.col.dmg") : _loc.T("list.col.heal");

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
            // Row 3 — ZDPS-parity splits, metric-aware.
            9  => sb.Metric == Metric.Hps ? FormatAmount(s.CritHealing) : FormatAmount(s.CritDamage),
            10 => sb.Metric == Metric.Hps ? FormatAmount(s.LuckyHealing) : FormatAmount(s.LuckyDamage),
            11 => sb.Metric == Metric.Hps ? FormatAmount(s.CritLuckyHealing) : FormatAmount(s.CritLuckyDamage),
            12 => sb.Metric == Metric.Hps ? FormatAmount(s.EffectiveHealing) : FormatAmount(s.ShieldBreak),
            13 => sb.Metric == Metric.Hps
                ? FormatAmount(Math.Max(0, s.TotalHealing - s.EffectiveHealing))
                : FormatAmount(ActiveDps(s)),
            _ => "—",
        };
    }

    /// <summary>Damage over the source's own active span (first→last hit) — ZDPS "Active DPS".</summary>
    private static long ActiveDps(SourceStats s)
    {
        var span = s.LastHitMs - s.FirstHitMs;
        return span > 0 ? s.TotalDamage * 1000 / span : 0;
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
