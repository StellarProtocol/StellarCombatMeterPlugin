using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// CombatMeter-local, read-only "Session Snapshot" window (Phase 1 / MVP of issue #5). Opened from the History
/// detail's per-player Inspect affordance, it renders a frozen <see cref="EntitySnapshot"/> captured at archive
/// time: an identity header + compact key-stats / gear / skills / fashion sections. IDs are frozen; every NAME /
/// ICON re-resolves LIVE from the static <c>IGameData</c> tables at render so a later language switch / table
/// update is honoured. This is NOT a full Entity-Inspector clone — the real cross-plugin relay is Phase 2.
///
/// Snapshot pattern (mirrors the rest of the plugin): row strings are baked into flat lists on open / rebuild, so
/// the element Funcs only index a cached list — no per-poll table scan.
/// </summary>
public sealed partial class Plugin
{
    private const int MaxSnapStatRows = 24;     // key-stats grid bound
    private const int MaxSnapGearRows = 12;     // gear list bound
    private const int MaxSnapSkillRows = 12;    // skills list bound
    private const float SnapScrollHeight = 280f;
    private const float SnapValueWidth = 120f;

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
        public string Identity = "";
        public string SubIdentity = "";
        public readonly List<(string Label, string Value)> Stats = new(MaxSnapStatRows);
        public readonly List<(string Slot, string Name)> Gear = new(MaxSnapGearRows);
        public readonly List<(string Name, string Sub)> Skills = new(MaxSnapSkillRows);
        public readonly List<string> Fashion = new();
    }

    private SnapshotState? _snapshot;

    private HudElement BuildSnapshotRoot() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => _snapshot?.Identity ?? "Session Snapshot", Emphasis: true),
        new TextElement(() => _snapshot?.SubIdentity ?? "", MutedCol),
        new SeparatorElement(),
        new ScrollElement(new ColumnElement(new HudElement[]
        {
            BuildSnapStatsSection(),
            BuildSnapGearSection(),
            BuildSnapSkillsSection(),
            BuildSnapFashionSection(),
        }, Gap: 8f), SnapScrollHeight),
    }, Gap: 3f);

    private HudElement BuildSnapStatsSection()
    {
        var slots = new HudElement[MaxSnapStatRows];
        for (var i = 0; i < MaxSnapStatRows; i++)
        {
            var idx = i;
            slots[i] = new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(
                    () => idx < (_snapshot?.Stats.Count ?? 0) ? _snapshot!.Stats[idx].Label : "", MutedCol), Weight: 1f),
                NumCell(() => idx < (_snapshot?.Stats.Count ?? 0) ? _snapshot!.Stats[idx].Value : "", SnapValueWidth),
            }, Gap: 6f);
        }
        return new ColumnElement(new HudElement[]
        {
            new TextElement(() => "Key Stats", () => AccentCol),
            new ConditionalElement(() => (_snapshot?.Stats.Count ?? 0) == 0,
                new TextElement(() => "No attributes recorded.", MutedCol)),
            new ListElement(() => _snapshot?.Stats.Count ?? 0, slots),
        }, Gap: 2f);
    }

    private HudElement BuildSnapGearSection()
    {
        var slots = new HudElement[MaxSnapGearRows];
        for (var i = 0; i < MaxSnapGearRows; i++)
        {
            var idx = i;
            slots[i] = new RowElement(new HudElement[]
            {
                new GameTextureElement(() => SnapGearIcon(idx), 20, 20, () => _snapGearUv[idx]),
                new CellElement(new TextElement(
                    () => idx < (_snapshot?.Gear.Count ?? 0) ? _snapshot!.Gear[idx].Slot : "", MutedCol), Width: 96f),
                new CellElement(new TextElement(
                    () => idx < (_snapshot?.Gear.Count ?? 0) ? _snapshot!.Gear[idx].Name : "", Emphasis: true), Weight: 1f),
            }, Gap: 6f);
        }
        return new ColumnElement(new HudElement[]
        {
            new TextElement(() => "Gear", () => AccentCol),
            new ConditionalElement(() => (_snapshot?.Gear.Count ?? 0) == 0,
                new TextElement(() => "No equipment recorded.", MutedCol)),
            new ListElement(() => _snapshot?.Gear.Count ?? 0, slots),
        }, Gap: 2f);
    }

    private HudElement BuildSnapSkillsSection()
    {
        var slots = new HudElement[MaxSnapSkillRows];
        for (var i = 0; i < MaxSnapSkillRows; i++)
        {
            var idx = i;
            slots[i] = new RowElement(new HudElement[]
            {
                new GameTextureElement(() => SnapSkillIcon(idx), 20, 20, () => _snapSkillUv[idx]),
                new CellElement(new ColumnElement(new HudElement[]
                {
                    new TextElement(() => idx < (_snapshot?.Skills.Count ?? 0) ? _snapshot!.Skills[idx].Name : "", Emphasis: true),
                    new TextElement(() => idx < (_snapshot?.Skills.Count ?? 0) ? _snapshot!.Skills[idx].Sub : "", MutedCol),
                }, Gap: 0f), Weight: 1f),
            }, Gap: 6f);
        }
        return new ColumnElement(new HudElement[]
        {
            new TextElement(() => "Skills", () => AccentCol),
            new ConditionalElement(() => (_snapshot?.Skills.Count ?? 0) == 0,
                new TextElement(() => "No skill loadout recorded.", MutedCol)),
            new ListElement(() => _snapshot?.Skills.Count ?? 0, slots),
        }, Gap: 2f);
    }

    private HudElement BuildSnapFashionSection() => new ConditionalElement(
        () => (_snapshot?.Fashion.Count ?? 0) > 0,
        new ColumnElement(new HudElement[]
        {
            new TextElement(() => "Fashion", () => AccentCol),
            new TextElement(() => _snapshot is { } s ? string.Join("   ", s.Fashion) : "", MutedCol),
        }, Gap: 2f));

    private ColorRgba AccentCol => new(0.79f, 0.66f, 0.36f, 1f);   // section header gold (matches EntityInspector)
}
