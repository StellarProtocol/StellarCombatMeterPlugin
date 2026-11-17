using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// CombatMeter-local, read-only "Session Snapshot" window (Phase 1 / MVP of issue #5), house style (approved redesign
/// 2026-11-17 §5). Opened from the History row's ⌕ inspect, it renders a frozen <see cref="EntitySnapshot"/> captured at
/// archive time:
/// <code>
///   header  [40 px class crest] "Name · Session Snapshot" / muted "Lv · Ability Score · HP"
///   KEY STATS | GEAR   (two columns)
///   SKILLS
///   FASHION (muted line, only when present)
/// </code>
/// IDs are frozen; every NAME / ICON re-resolves LIVE from the static <c>IGameData</c> tables, so a language switch /
/// table update is honoured. This is NOT a full Entity-Inspector clone — the real cross-plugin relay is Phase 2.
///
/// Snapshot pattern (mirrors the rest of the plugin): row strings are baked into flat lists on open / language change
/// (Plugin.SessionSnapshot.Build.cs), so the element Funcs only index a cached list — no per-poll table scan.
/// </summary>
public sealed partial class Plugin
{
    private const int MaxSnapStatRows = 24;     // key-stats bound
    private const int MaxSnapGearRows = 12;     // gear list bound
    private const int MaxSnapSkillRows = 12;    // skills list bound
    private const float SnapWindowW = 460f;
    private const float SnapScrollHeight = 460f;
    private const float SnapScrollLane = 12f;   // room for the scroll bar beside the panels
    private const float SnapColW = (SnapWindowW - 24f - 8f - SnapScrollLane) / 2f;
    private const float SnapIconSize = 20f;

    // Curated key-stat ids (a subset of the Entity Inspector's Overview — the headline numbers). Labels resolve
    // live via IGameData.Combat.GetAttribute; absent ids are skipped (the snapshot stores non-zero attrs only).
    private static readonly int[] SnapKeyStatIds =
    {
        11330, 11340, 11320, 11710, 11930, 11780, 11790, 11800,
    };
    private const int AttrLevel = 10000, AttrProfessionId = 220;

    private sealed class SnapshotState
    {
        public EntityId Source;
        public EncounterHistoryEntry Session = null!;   // for the stale-session guard (mirrors SkillBreakdownState)
        public EntitySnapshot Snap = null!;
        public int ProfId;
        public string Identity = "";
        public string SubIdentity = "";
        public string FashionLine = "";
        public readonly List<(string Label, string Value)> Stats = new(MaxSnapStatRows);
        public readonly List<(string Slot, string Name)> Gear = new(MaxSnapGearRows);
        public readonly List<(string Name, string Sub)> Skills = new(MaxSnapSkillRows);
        public readonly List<string> Fashion = new();
    }

    private SnapshotState? _snapshot;
    private UvRect _snapCrestUv = new(0f, 0f, 1f, 1f);

    private HudElement BuildSnapshotRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(SnapCrestTile(), () => _snapshot?.Identity ?? _loc.T("snapshot.title"),
            new TextElement(() => _snapshot?.SubIdentity ?? "", Color: () => MutedColor(), NoWrap: true) { Ellipsize = true }),
        new ScrollElement(new ColumnElement(new HudElement[]
        {
            // Two balanced columns, top-aligned: each ends in a vertical filler so the Row stretches both to the taller.
            new RowElement(new HudElement[]
            {
                new CellElement(new ColumnElement(new HudElement[] { BuildSnapStatsSection(), VerticalFill() }, Gap: 8f), Width: SnapColW),
                new CellElement(new ColumnElement(new HudElement[] { BuildSnapGearSection(), VerticalFill() }, Gap: 8f), Width: SnapColW),
            }, Gap: 8f),
            BuildSnapSkillsSection(),
            BuildSnapFashionSection(),
        }, Gap: 8f), SnapScrollHeight) { FixedHeight = true },
    }, Gap: 8f);

    // 40 px class crest (the game's profession icon, rounded); the meter mark when the class is unknown. Fixed cell: a
    // bare Conditional in a Row takes flexible width.
    private HudElement SnapCrestTile() => new CellElement(new ConditionalElement(() => (_snapshot?.ProfId ?? 0) > 0,
        new GameTextureElement(
            () => _snapshot is { ProfId: > 0 } s ? _services.GameAssets.LoadProfessionIcon(s.ProfId, out _snapCrestUv) : null,
            (int)ToolIconSize, (int)ToolIconSize, () => _snapCrestUv) { CornerRadius = 7 },
        MeterIconTile()), Width: ToolIconSize);

    private int SnapCount(Func<SnapshotState, int> count) => _snapshot is { } s ? count(s) : 0;

    // KEY STATS: muted label · bold value, 22 px rows.
    private HudElement BuildSnapStatsSection()
    {
        var slots = new HudElement[MaxSnapStatRows];
        for (var i = 0; i < MaxSnapStatRows; i++)
        {
            var idx = i;
            slots[i] = new RowElement(new HudElement[]
            {
                new SpacerElement(1f, 22f),
                new CellElement(new TextElement(
                    () => idx < SnapCount(s => s.Stats.Count) ? _snapshot!.Stats[idx].Label : "", Color: () => MutedColor(), NoWrap: true)
                    { Ellipsize = true }, Weight: 1f),
                new TextElement(() => idx < SnapCount(s => s.Stats.Count) ? _snapshot!.Stats[idx].Value : "",
                    Color: () => TextColor(), NoWrap: true, FontSize: 13) { Bold = true },
            }, Gap: 6f);
        }
        return SectionPanel(() => _loc.T("snapshot.keyStats"), null,
            new ConditionalElement(() => SnapCount(s => s.Stats.Count) == 0, MutedLine(() => _loc.T("snapshot.noAttributes"))),
            new ListElement(() => SnapCount(s => s.Stats.Count), slots) { Gap = 0.01f });
    }

    // GEAR: icon · muted slot · bold item name, 24 px rows.
    private HudElement BuildSnapGearSection()
    {
        var slots = new HudElement[MaxSnapGearRows];
        for (var i = 0; i < MaxSnapGearRows; i++)
        {
            var idx = i;
            slots[i] = new RowElement(new HudElement[]
            {
                new SpacerElement(1f, 24f),
                SnapIcon(() => SnapGearIcon(idx), () => _snapGearUv[idx]),
                new CellElement(new TextElement(
                    () => idx < SnapCount(s => s.Gear.Count) ? _snapshot!.Gear[idx].Slot : "", Color: () => MutedColor(), NoWrap: true)
                    { Ellipsize = true }, Width: 52f),
                new CellElement(new TextElement(
                    () => idx < SnapCount(s => s.Gear.Count) ? _snapshot!.Gear[idx].Name : "", Color: () => TextColor(), NoWrap: true,
                    FontSize: 13) { Bold = true, Ellipsize = true }, Weight: 1f),
            }, Gap: 6f);
        }
        return SectionPanel(() => _loc.T("snapshot.gear"), null,
            new ConditionalElement(() => SnapCount(s => s.Gear.Count) == 0, MutedLine(() => _loc.T("snapshot.noEquipment"))),
            new ListElement(() => SnapCount(s => s.Gear.Count), slots) { Gap = 0.01f });
    }

    // SKILLS: icon · bold skill name · muted "Lv 12 · Tier 3", 26 px rows, full width.
    private HudElement BuildSnapSkillsSection()
    {
        var slots = new HudElement[MaxSnapSkillRows];
        for (var i = 0; i < MaxSnapSkillRows; i++)
        {
            var idx = i;
            slots[i] = new RowElement(new HudElement[]
            {
                new SpacerElement(1f, 26f),
                SnapIcon(() => SnapSkillIcon(idx), () => _snapSkillUv[idx]),
                new CellElement(new TextElement(
                    () => idx < SnapCount(s => s.Skills.Count) ? _snapshot!.Skills[idx].Name : "", Color: () => TextColor(), NoWrap: true,
                    FontSize: 13) { Bold = true, Ellipsize = true }, Weight: 1f),
                new TextElement(() => idx < SnapCount(s => s.Skills.Count) ? _snapshot!.Skills[idx].Sub : "",
                    Color: () => MutedColor(), NoWrap: true),
            }, Gap: 6f);
        }
        return SectionPanel(() => _loc.T("snapshot.skills"), null,
            new ConditionalElement(() => SnapCount(s => s.Skills.Count) == 0, MutedLine(() => _loc.T("snapshot.noSkills"))),
            new ListElement(() => SnapCount(s => s.Skills.Count), slots) { Gap = 0.01f });
    }

    // FASHION: one muted line, only when the snapshot carries any (the joined line is baked on rebuild).
    private HudElement BuildSnapFashionSection() => new ConditionalElement(
        () => SnapCount(s => s.Fashion.Count) > 0,
        SectionPanel(() => _loc.T("snapshot.fashion"), null, MutedLine(() => _snapshot?.FashionLine ?? "")));

    // A 20 px rounded game icon in a fixed, centring cell (a bare picture in a Cell is stretched sideways).
    private static HudElement SnapIcon(Func<object?> tex, Func<UvRect> uv) => new CellElement(new RowElement(new HudElement[]
        {
            new GameTextureElement(tex, (int)SnapIconSize, (int)SnapIconSize, uv) { CornerRadius = 4 },
        }, Gap: 0.01f, Justify: RowJustify.Center), Width: SnapIconSize);
}
