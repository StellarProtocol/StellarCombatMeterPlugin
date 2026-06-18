using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// Skill breakdown surface — a uGUI window (Party chrome) opened by the History drill-in ► button. Operates on
/// the <see cref="EncounterHistoryEntry"/> captured at click-time (decoupled from live state). Header (name —
/// Skill Breakdown + meta), a column header, and a scrollable per-skill table (rank·name·sub + TOTAL/DPS/COUNT).
/// The plugin snapshots the skill rows into _skillRows each shown frame.
/// </summary>
public sealed partial class Plugin
{
    private const int MaxSkillSlots = 40;
    private const float SkillScrollHeight = 280f;
    private const float SkillColTotal = 90f, SkillColDps = 70f, SkillColCount = 64f, SkillColBar = 90f;

    private SkillBreakdownState? _skillBreakdown;
    private readonly List<SkillRow> _skillRows = new(MaxSkillSlots);

    private sealed class SkillBreakdownState
    {
        public EntityId Source;
        public EncounterHistoryEntry Session = null!;
        public Metric Metric;   // captured at open time so the window is stable if the history metric later changes
    }

    private struct SkillRow
    {
        public string Rank, Name, Sub, Total, Dps, Count;
        public float Share;
    }

    private HudElement BuildSkillBreakdownRoot()
    {
        var slots = new HudElement[MaxSkillSlots];
        for (var i = 0; i < MaxSkillSlots; i++)
        {
            var idx = i;
            string F(Func<SkillRow, string> sel) => idx < _skillRows.Count ? sel(_skillRows[idx]) : "";
            // Explicit per-skill share bar (role-coloured) makes each row read as a bar chart. Replaces the
            // old AccentRowElement wash — both encoded the same Share, so the wash was dropped to avoid
            // double-encoding. Fill is baked at build time from the source captured at open (stable).
            ColorRgba barColor = _skillBreakdown is { } sbColor ? RoleColorFor(sbColor.Source) : default;
            // Each skill is a Column of: (1) a top row — name (single-line, elastic) + numeric columns, so the
            // numbers sit on the skill-name baseline instead of vertically centring against a 2-line cell;
            // (2) the % DMG/Count/Crit/Luck sub-line; (3) a full-width role-coloured share bar (a per-skill bar
            // chart — moved off the top row so it never competes with the name for width at narrow window sizes);
            // (4) a 1-px separator so each skill reads as one bounded row. Replaces the old name+sub-stacked-in-
            // one-cell layout whose centred numbers were ambiguous about which skill they belonged to.
            slots[i] = new ColumnElement(new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    new CellElement(new TextElement(() => idx < _skillRows.Count ? $"{_skillRows[idx].Rank} {_skillRows[idx].Name}" : "", Emphasis: true, NoWrap: true), Weight: 1f),
                    NumCell(() => F(r => r.Total), SkillColTotal),
                    NumCell(() => F(r => r.Dps), SkillColDps),
                    NumCell(() => F(r => r.Count), SkillColCount),
                }, Gap: 6f),
                new TextElement(() => F(r => r.Sub), MutedCol),
                new BarElement(() => idx < _skillRows.Count ? _skillRows[idx].Share : 0f, barColor),
                new SeparatorElement(),
            }, Gap: 2f);
        }
        return new ColumnElement(new HudElement[]
        {
            new TextElement(SkillHeader, Emphasis: true),
            new TextElement(SkillMeta, MutedCol),
            BuildSkillDetailBlock(),
            new SeparatorElement(),
            new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(() => "Skill", MutedCol), Weight: 1f),
                NumCell(() => _skillBreakdown is { } sb ? MetricColumnLabel(sb.Metric) : "TOTAL", SkillColTotal, muted: true),
                NumCell(() => _skillBreakdown is { } sb ? MetricRateLabel(sb.Metric) : "DPS", SkillColDps, muted: true),
                NumCell(() => "HITS", SkillColCount, muted: true),
            }, Gap: 6f),
            new ConditionalElement(() => _skillRows.Count == 0,
                new TextElement(SkillEmptyCaption, MutedCol)),
            new ConditionalElement(() => _skillRows.Count > 0,
                new ScrollElement(new ListElement(() => _skillRows.Count, slots), SkillScrollHeight)),
        });
    }

    private string SkillHeader()
    {
        if (_skillBreakdown is not { } sb) return "Skill Breakdown";
        EntityId self = _services.CombatSnapshot.LocalEntityId;
        var name = EntityLabel.Resolve(sb.Source, self, _services.PlayerState, _services.CombatLookup, _services.PartyRoster.Members);
        return $"{name} — {MetricColumnLabel(sb.Metric)} by skill";
    }

    private string SkillEmptyCaption()
        => _skillBreakdown?.Metric switch
        {
            Metric.Taken => "No incoming damage recorded.",
            Metric.Hps   => "No healing recorded for this source.",
            _            => "No skills recorded for this source.",
        };

    private string SkillMeta()
    {
        if (_skillBreakdown is not { } sb) return "";
        var s = sb.Session;
        return $"{FormatSessionTimestampLong(s.ArchivedAtMs)}  ·  {FormatSessionDurationShort(s.CombatDurationMs)}  ·  {s.MemberCount}p";
    }

    private void RebuildSkillRows()
    {
        _skillRows.Clear();
        if (_skillBreakdown is not { } state) return;

        // Stale-session guard: close if the drilled entry was evicted (cap) or deleted (clear-all/clear-one).
        if (!_history.Contains(state.Session)) { CloseSkillBreakdown(); return; }
        if (!state.Session.Stats.TryGetValue(state.Source, out var src)) return;

        long durationMs = state.Session.CombatDurationMs;
        if (state.Metric == Metric.Taken) RebuildIncomingRows(src, durationMs);
        else                              RebuildOutgoingSkillRows(src, state.Metric, durationMs);
    }

    // DPS/HPS: per-skill outgoing rows. Metric total picks damage vs heal; pure-other skills (0 in this
    // metric) are skipped so DPS mode hides heal-only skills and HPS hides damage-only ones.
    private void RebuildOutgoingSkillRows(SourceStats src, Metric metric, long durationMs)
    {
        var rows = new List<KeyValuePair<int, SkillStats>>(src.BySkill.Count);
        long sum = 0;
        foreach (var kv in src.BySkill)
        {
            long v = SkillMetricTotal(kv.Value, metric);
            if (v <= 0) continue;
            rows.Add(kv);
            sum += v;
        }
        rows.Sort((a, b) => SkillMetricTotal(b.Value, metric).CompareTo(SkillMetricTotal(a.Value, metric)));

        for (int i = 0; i < rows.Count && _skillRows.Count < MaxSkillSlots; i++)
        {
            var sk = rows[i].Value;
            long value = SkillMetricTotal(sk, metric);
            float pct = sum > 0 ? 100f * value / sum : 0f;
            float critPct = sk.Hits > 0 ? 100f * sk.Crits / sk.Hits : 0f;
            float luckPct = sk.Hits > 0 ? 100f * sk.Luckys / sk.Hits : 0f;
            _skillRows.Add(new SkillRow
            {
                Rank = $"#{i + 1}",
                Name = ResolveSkillName(rows[i].Key),
                Sub = $"% {MetricColumnLabel(metric)} {pct:F1}%   Count {sk.Hits}   Crit {critPct:F1}%   Luck {luckPct:F1}%",
                Total = FormatAmount(value),
                Dps = FormatAmount(ComputeArchivedDps(value, durationMs)),
                Count = sk.Hits.ToString(),
                Share = sum > 0 ? (float)value / sum : 0f,
            });
        }
    }

    // Taken: incoming damage grouped by attacker skill ("what hit you").
    private void RebuildIncomingRows(SourceStats src, long durationMs)
    {
        var rows = BuildIncomingRows(src);
        long sum = 0;
        foreach (var r in rows) sum += r.Total;

        for (int i = 0; i < rows.Count && _skillRows.Count < MaxSkillSlots; i++)
        {
            var r = rows[i];
            _skillRows.Add(new SkillRow
            {
                Rank = $"#{i + 1}",
                Name = ResolveSkillName(r.SkillId),
                Sub = $"Count {r.Hits}   Max {FormatAmount(r.TopHit)}",
                Total = FormatAmount(r.Total),
                Dps = FormatAmount(ComputeArchivedDps(r.Total, durationMs)),
                Count = r.Hits.ToString(),
                Share = sum > 0 ? (float)r.Total / sum : 0f,
            });
        }
    }

    // Curated display-name overrides for damage-attributed ids whose game-table name is unhelpful — e.g. DoT/proc
    // buffs whose English Name column is empty in this client (only a source-language NameDesign exists). Mirrors
    // ZDPS's SkillOverrides; wins over the buff-table name so the lance lucky-counter proc reads "Lucky Strike
    // (Lance)" instead of "骑枪-幸运" or a raw id. Extend as ids surface — the [name] diagnostic (gated on
    // STELLAR_DIAGNOSTICS) logs each unresolved id once so they can be added here.
    private static readonly Dictionary<int, string> SkillNameOverrides = new()
    {
        [2031104] = "Lucky Strike (Lance)",   // BuffTable "Lance - Luck" / NameDesign 骑枪-幸运 — Wind Knight lance proc
    };

    // Resolve a damage-attributed "skill" id to a display name. Order: real skill (GetSkill also resolves leveled
    // skill_level_ids via the framework base map) → curated override → buff name (GetBuff().Name is the English
    // buff name when present, else the framework's NameDesign fallback). Raw "#id" only when all miss.
    private string ResolveSkillName(int skillId)
    {
        var skill = _services.GameData.Combat.GetSkill(skillId);
        if (skill is { Name: { Length: > 0 } skillName }) return skillName;

        if (SkillNameOverrides.TryGetValue(skillId, out var overrideName)) return overrideName;

        var buff = _services.GameData.Combat.GetBuff(skillId);
        if (buff is { Name: { Length: > 0 } buffName }) return buffName;

        LogUnresolvedSkillName(skillId);
        return $"#{skillId}";
    }

    // Metric-aware per-skill value: HPS reads the heal total, everything else (DPS/Taken) the damage total.
    internal static long SkillMetricTotal(SkillStats sk, Metric m)
        => m == Metric.Hps ? sk.HealTotal : sk.Total;

    /// <summary>One incoming (Taken-mode) row: damage to the drilled source grouped by the attacker's skill.</summary>
    internal readonly record struct IncomingRow(int SkillId, long Total, int Hits, long TopHit);

    // Project a source's IncomingBySkill map into rows sorted by total taken (desc).
    internal static IReadOnlyList<IncomingRow> BuildIncomingRows(SourceStats src)
    {
        var rows = new List<IncomingRow>(src.IncomingBySkill.Count);
        foreach (var (sid, inc) in src.IncomingBySkill) rows.Add(new IncomingRow(sid, inc.Total, inc.Hits, inc.TopHit));
        rows.Sort(static (a, b) => b.Total.CompareTo(a.Total));
        return rows;
    }

    // A popup dialog (opened from the History drill-in ►): free-drag + ✕ close, Party chrome. Shared by the
    // initial registration (Plugin.cs) and the drill-in rebuild below.
    private IWindowControl RegisterSkillBreakdownWindow() => _services.Windows.Register(new WindowRegistration(
        new WindowSpec(
            Id:          "combatmeter.skill-breakdown",
            Title:       "Skill Breakdown",
            DefaultRect: new WindowRect(1000f, 80f, 460f, 520f),
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.GlassMenu)   // dark-slate frosted dialog: free-drag + ✕ close (see history above)
        { StartVisible = false, HideUntilInWorld = true, Closable = true, Draggable = true,
          Resizable = true, MinWidth = 360f, MinHeight = 280f, MaxWidth = 900f, MaxHeight = 1000f },
        BuildSkillBreakdownRoot(),
        OnClose: CloseSkillBreakdown));

    // The per-skill BarElement bakes its Fill colour at element-BUILD time from _skillBreakdown.Source, so the
    // root must be rebuilt once _skillBreakdown is set for the bars to read the drilled source's role colour
    // (it is null at first registration, which would bake a transparent fill). Rebuild the window subtree
    // (preserving rect + visibility) — the framework-sanctioned Remove()+Register() pattern (mirrors
    // RebuildHistoryWindow). Fires only on drill-in (user action), never per-frame.
    private void RebuildSkillBreakdownWindow()
    {
        var rect = _skillBreakdownWindow.Rect;
        var wasShown = _skillBreakdownWindow.IsShown;
        _skillBreakdownWindow.Remove();
        _skillBreakdownWindow = RegisterSkillBreakdownWindow();
        if (rect.Width > 0f) _skillBreakdownWindow.SetRect(rect);
        _skillBreakdownWindow.SetVisible(wasShown);
    }

    private void HandleSkillBreakdownRequested(EntityId id, EncounterHistoryEntry session)
    {
        if (_skillBreakdown is { } sb && sb.Source == id && ReferenceEquals(sb.Session, session))
        {
            CloseSkillBreakdown();
            return;
        }
        _skillBreakdown = new SkillBreakdownState { Source = id, Session = session, Metric = _historyMetric };
        RebuildSkillRows();
        // Rebuild the root now that _skillBreakdown is set so BuildSkillBreakdownRoot re-resolves the baked bar
        // Fill to RoleColorFor(Source) — without this the bars keep the transparent fill baked at registration.
        RebuildSkillBreakdownWindow();
        _skillBreakdownWindow.SetVisible(true);
    }

    private void CloseSkillBreakdown()
    {
        _skillBreakdown = null;
        _skillBreakdownWindow.SetVisible(false);
    }
}
