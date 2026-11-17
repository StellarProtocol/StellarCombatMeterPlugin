using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// Skill Breakdown — house style (approved redesign 2026-11-17 §5), opened by the History row's › drill-in. Operates on
/// the <see cref="EncounterHistoryEntry"/> captured at click time (decoupled from live state):
/// <code>
///   header  [40 px class crest] "Name · damage by skill" / muted "date · duration · N players"
///   TOTALS  5 × 3 stat tiles (Plugin.SkillBreakdownDetail.cs)
///   SKILLS  "N skills" — rows = tiles: icon · bold "#1 Skill" · total · muted rate + hits / muted sub-line / 4 px share bar
/// </code>
/// Every string is formatted once per open / language change (a frozen archive never changes), never per refresh.
/// Content-fit height with fixed blocks (the skill list is a fixed viewport), so the window keeps its size per player.
/// </summary>
public sealed partial class Plugin
{
    private const int   MaxSkillSlots = 40;
    private const float SkillWindowW = 480f;
    private const float SkillIconSize = 20f;
    private const float SkillIconGap = 7f;
    private const float SkillColTotal = 64f, SkillColRate = 52f, SkillColHits = 40f;
    private const float SkillTilePitch = 62f;   // ~58 px tile (5 + 20 + 3 + 18 + 3 + 4 + 5) + gap
    private const int   SkillRowsShown = 5;

    private SkillBreakdownState? _skillBreakdown;
    private readonly List<SkillRow> _skillRows = new(MaxSkillSlots);
    private WindowCenterer? _skillCenterer;
    private int _skillOffset;
    private readonly UvRect[] _skillIconUv = NewUvRects(SkillRowsShown + 2);
    private UvRect _skillCrestUv = new(0f, 0f, 1f, 1f);

    private sealed class SkillBreakdownState
    {
        public EntityId Source;
        public EncounterHistoryEntry Session = null!;
        public Metric Metric;   // captured at open time so the window is stable if the history metric later changes
        public int ProfId;      // crest
    }

    private struct SkillRow
    {
        public string Label, Sub, Total, Rate, Hits;   // Label = "#1 Skill"
        public float Share;
        public int SkillId;
        public int IconId;      // SkillId, or the parent skill for a SkillEffectTable sub-hit (SourceName)
        public bool BuffIcon;   // id is a buff, not a skill → LoadBuffIcon (SourceName)
    }

    // Header + count strings and the gauge colour, cached per open / language (RebuildSkillRows).
    private string _sbTitle = "", _sbMeta = "", _sbCount = "";
    private ColorRgba _sbGauge;
    private SkillBreakdownState? _sbBuiltFor;
    private string? _sbBuiltLang;

    private HudElement BuildSkillBreakdownRoot()
    {
        var pool = new HudElement[SkillRowsShown + 2];
        for (int i = 0; i < pool.Length; i++) pool[i] = SkillSlot(i);
        float viewport = SkillRowsShown * SkillTilePitch - ListRowGap;
        return new ColumnElement(new HudElement[]
        {
            ToolHeader(SkillCrestTile(), () => _sbTitle,
                new TextElement(() => _sbMeta, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true }),
            BuildTotalsPanel(),
            SectionPanel(() => _loc.T("skill.skills"), () => _sbCount,
                new ConditionalElement(() => _skillRows.Count == 0,
                    FixedBox(viewport, MutedLine(SkillEmptyCaption)),
                    new VirtualListElement(
                        Count:     () => _skillRows.Count,
                        RowHeight: SkillTilePitch, Pool: pool,
                        OnWindow:  i => _skillOffset = i, Height: viewport))),
        }, Gap: 8f);
    }

    // 40 px class crest (the game's profession icon, rounded); the meter mark when the class is unknown. Fixed cell: a
    // bare Conditional in a Row takes flexible width.
    private HudElement SkillCrestTile() => new CellElement(new ConditionalElement(() => (_skillBreakdown?.ProfId ?? 0) > 0,
        new GameTextureElement(
            () => _skillBreakdown is { ProfId: > 0 } sb ? _services.GameAssets.LoadProfessionIcon(sb.ProfId, out _skillCrestUv) : null,
            (int)ToolIconSize, (int)ToolIconSize, () => _skillCrestUv) { CornerRadius = 7 },
        MeterIconTile()), Width: ToolIconSize);

    private int SkillRowAt(int slot) { int i = _skillOffset + slot; return i >= 0 && i < _skillRows.Count ? i : -1; }

    private string SkillText(int slot, Func<SkillRow, string> sel) => SkillRowAt(slot) is var i and >= 0 ? sel(_skillRows[i]) : "";

    // One skill tile: [icon][bold "#1 Skill"][total][muted rate][muted hits] / muted sub-line / 4 px share bar in the
    // gauge colour; the sub-line and bar are indented under the name (icon + gap).
    private HudElement SkillSlot(int slot)
    {
        var tile = new BubbleElement(new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new CellElement(new RowElement(new HudElement[]
                    {
                        new GameTextureElement(() => SkillIconTex(slot), (int)SkillIconSize, (int)SkillIconSize, () => _skillIconUv[slot])
                            { CornerRadius = 4 },
                    }, Gap: 0.01f, Justify: RowJustify.Center), Width: SkillIconSize),
                new CellElement(new TextElement(() => SkillText(slot, r => r.Label), Color: () => TextColor(), NoWrap: true, FontSize: 13)
                    { Bold = true, Ellipsize = true }, Weight: 1f),
                SkillNum(() => SkillText(slot, r => r.Total), SkillColTotal, bold: true),
                SkillNum(() => SkillText(slot, r => r.Rate), SkillColRate, bold: false),
                SkillNum(() => SkillText(slot, r => r.Hits), SkillColHits, bold: false),
            }, Gap: SkillIconGap),
            new RowElement(new HudElement[]
            {
                new SpacerElement(SkillIconSize, 1f),
                new CellElement(new TextElement(() => SkillText(slot, r => r.Sub), Color: () => MutedColor(), NoWrap: true)
                    { Ellipsize = true }, Weight: 1f),
            }, Gap: SkillIconGap),
            new RowElement(new HudElement[]
            {
                new SpacerElement(SkillIconSize, 4f),
                new CellElement(new BarElement(() => SkillRowAt(slot) is var i and >= 0 ? _skillRows[i].Share : 0f, default)
                    {
                        Height = 4f, FillWidth = true, FillColorFn = () => Fade(_sbGauge),
                        // fw 2.33.0 opt-ins: rounded track + fill (a 4 px capsule), theme-live recessed track
                        CornerRadius = 2f, TrackColorFn = () => Fade(TrackColor()),
                    }, Weight: 1f),
            }, Gap: SkillIconGap),
        }, Gap: 3f))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 8f, PaddingY = 5f,
        };
        return new ColumnElement(new HudElement[] { tile, new SpacerElement(0f, ListRowGap) }, Gap: 0.01f);
    }

    private HudElement SkillNum(Func<string> text, float width, bool bold) => new CellElement(
        new TextElement(text, Color: () => bold ? TextColor() : MutedColor(), Width: width, Align: TextAlign.Right, NoWrap: true,
            FontSize: 13) { Bold = bold }, Width: width);

    private object? SkillIconTex(int slot)
    {
        int i = SkillRowAt(slot);
        int id = i >= 0 ? _skillRows[i].IconId : 0;
        if (id <= 0) return null;
        // A buff-sourced row (sub-buff, damage zone, proc) has no skill row → its icon is the buff's.
        return _skillRows[i].BuffIcon
            ? _services.GameAssets.LoadBuffIcon(id, out _skillIconUv[slot])
            : _services.GameAssets.LoadSkillIcon(id, out _skillIconUv[slot]);
    }

    private string SkillEmptyCaption()
        => _skillBreakdown?.Metric switch
        {
            Metric.Taken => _loc.T("skill.empty.taken"),
            Metric.Hps   => _loc.T("skill.empty.healing"),
            _            => _loc.T("skill.empty.skills"),
        };

    // Ticked at ~10 Hz while shown (Plugin.Tick.cs), but a frozen archive never changes: re-format only when the drilled
    // source or the language changes. The stale-session guard runs every tick.
    private void RebuildSkillRows()
    {
        if (_skillBreakdown is not { } state) { _skillRows.Clear(); return; }
        // Stale-session guard: close if the drilled entry was evicted (cap) or deleted (clear-all/clear-one).
        if (!_history.Contains(state.Session)) { CloseSkillBreakdown(); return; }
        string lang = _loc.Language;
        if (ReferenceEquals(state, _sbBuiltFor) && ReferenceEquals(lang, _sbBuiltLang)) return;
        _sbBuiltFor = state; _sbBuiltLang = lang;

        _skillRows.Clear();
        RebuildSkillHeader(state);
        RebuildSkillTotals();
        if (state.Session.Stats.TryGetValue(state.Source, out var src))
        {
            long durationMs = state.Session.CombatDurationMs;
            if (state.Metric == Metric.Taken) RebuildIncomingRows(src, durationMs);
            else                              RebuildOutgoingSkillRows(src, state.Metric, durationMs);
        }
        _sbCount = _loc.TFormat(PluralKey(_skillRows.Count, "skill.countOne", "skill.count"), _skillRows.Count);
    }

    private void RebuildSkillHeader(SkillBreakdownState sb)
    {
        var s = sb.Session;
        var name = s.Entities.TryGetValue(sb.Source, out var frozen) && !string.IsNullOrEmpty(frozen.Name)
            ? frozen.Name!
            : EntityLabel.Resolve(sb.Source, _services.CombatSnapshot.LocalEntityId, _services.PlayerState,
                _services.CombatLookup, _services.PartyRoster.Members);
        _sbTitle = _loc.TFormat(sb.Metric switch
        {
            Metric.Hps   => "skill.title.healing",
            Metric.Taken => "skill.title.taken",
            _            => "skill.title.damage",
        }, name);
        var when = s.ArchivedAtMs <= 0 ? "—"
            : DateTimeOffset.FromUnixTimeMilliseconds(s.ArchivedAtMs).LocalDateTime
                .ToString(_loc.T("history.date.format"), CultureInfo.InvariantCulture);
        _sbMeta = when + " · " + FormatSessionDurationShort(s.CombatDurationMs) + " · " + _loc.TFormat(PluralKey(s.MemberCount, "history.playersOne", "history.players"), s.MemberCount);
        _sbGauge = ClassPalette.TryGet(sb.ProfId, out var cc) && ActiveToggles().BarColor == BarColorMode.Class ? cc : GaugeColorFor(sb.Source);
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
                Label = $"#{i + 1} {ResolveSkillName(rows[i].Key)}",
                BuffIcon = SourceName(rows[i].Key).BuffIcon,
                IconId = SourceName(rows[i].Key).IconId,
                Sub = _loc.TFormat("skill.sub.share", pct, critPct, luckPct),
                Total = FormatAmount(value),
                Rate = FormatAmount(ComputeArchivedDps(value, durationMs)),
                Hits = sk.Hits.ToString(CultureInfo.InvariantCulture),
                Share = sum > 0 ? (float)value / sum : 0f,
                SkillId = rows[i].Key,
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
                Label = $"#{i + 1} {ResolveSkillName(r.SkillId)}",
                BuffIcon = SourceName(r.SkillId).BuffIcon,
                IconId = SourceName(r.SkillId).IconId,
                Sub = _loc.TFormat("skill.sub.takenShare", sum > 0 ? 100f * r.Total / sum : 0f, FormatAmount(r.TopHit)),
                Total = FormatAmount(r.Total),
                Rate = FormatAmount(ComputeArchivedDps(r.Total, durationMs)),
                Hits = r.Hits.ToString(CultureInfo.InvariantCulture),
                Share = sum > 0 ? (float)r.Total / sum : 0f,
                SkillId = r.SkillId,
            });
        }
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

    // A popup dialog (opened from the History drill-in ›): free-drag + ✕ close. Shared by the initial registration
    // (Plugin.cs) and the language rebuild. A NEW id: the old one carries a persisted resizable rect of the old layout.
    private IWindowControl RegisterSkillBreakdownWindow()
    {
        var w = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.skill-breakdown.v2",
                Title:       _loc.T("skill.window.title"),   // baked at registration; rebuilt on LanguageChanged
                DefaultRect: new WindowRect(1000f, 80f, SkillWindowW, 640f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, Closable = true, Draggable = true,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            BuildSkillBreakdownRoot(),
            OnClose: CloseSkillBreakdown));
        _skillCenterer?.Stop();
        _skillCenterer = new WindowCenterer(_services.Framework, w);
        _sbBuiltFor = null;   // a rebuilt tree re-reads the caches; force a re-format on the next tick
        return w;
    }

    private void HandleSkillBreakdownRequested(EntityId id, EncounterHistoryEntry session)
    {
        if (_skillBreakdown is { } sb && sb.Source == id && ReferenceEquals(sb.Session, session))
        {
            CloseSkillBreakdown();
            return;
        }
        var frozen = session.Entities.TryGetValue(id, out var es) ? es : null;
        _skillBreakdown = new SkillBreakdownState
        {
            Source = id, Session = session, Metric = _historyMetric, ProfId = ArchivedProfession(frozen, id),
        };
        _skillOffset = 0;
        RebuildSkillRows();
        // Child of History: opens centred on it and in front; already open (another player) → re-fill + front it
        // (Rule 5 exception: an already-visible child re-opened from its parent).
        if (_skillBreakdownWindow.IsShown) _skillBreakdownWindow.BringToFront();
        else _skillCenterer?.OpenCentred(_historyWindow);
    }

    private void CloseSkillBreakdown()
    {
        _skillBreakdown = null;
        _skillCenterer?.Stop();
        _skillBreakdownWindow.SetVisible(false);
    }
}
