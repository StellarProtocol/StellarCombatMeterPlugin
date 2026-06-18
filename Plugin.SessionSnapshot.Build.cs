using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>
/// Session Snapshot rebuild + window lifecycle. Bakes the frozen <see cref="EntitySnapshot"/> into the flat row
/// lists (live name/icon resolution) and registers / opens the read-only window. Unknown ids render as
/// <c>&lt;unknown {id}&gt;</c> — never crash (issue #5 degradation contract).
/// </summary>
public sealed partial class Plugin
{
    // Per-row icon UV caches (filled by the GameTextureElement Funcs each frame from the cached id arrays).
    private readonly UvRect[] _snapGearUv  = NewUvRects(MaxSnapGearRows);
    private readonly UvRect[] _snapSkillUv = NewUvRects(MaxSnapSkillRows);

    private static UvRect[] NewUvRects(int n)
    {
        var a = new UvRect[n];
        for (var i = 0; i < n; i++) a[i] = new UvRect(0f, 0f, 1f, 1f);
        return a;
    }

    private object? SnapGearIcon(int idx)
    {
        if (_snapshot is not { } s || idx >= s.Snap.GearItemIds.Length) return null;
        var itemId = s.Snap.GearItemIds[idx];
        return itemId > 0 ? _services.GameAssets.LoadItemIcon(itemId, out _snapGearUv[idx]) : null;
    }

    private object? SnapSkillIcon(int idx)
    {
        if (_snapshot is not { } s || idx >= s.Snap.SkillIds.Length) return null;
        var skillId = s.Snap.SkillIds[idx];
        return skillId > 0 ? _services.GameAssets.LoadSkillIcon(skillId, out _snapSkillUv[idx]) : null;
    }

    private void RebuildSnapshotRows()
    {
        if (_snapshot is not { } s) return;
        // Stale-session guard: the inspected session may have been evicted (cap) or deleted.
        if (!_history.Contains(s.Session)) { CloseSnapshot(); return; }

        BuildSnapIdentity(s);
        BuildSnapStats(s);
        BuildSnapGear(s);
        BuildSnapSkills(s);
        BuildSnapFashion(s);
    }

    // Identity header: live name (snapshot stores the frozen one as a fallback only) + profession · level · GS · HP.
    private void BuildSnapIdentity(SnapshotState s)
    {
        var snap = s.Snap;
        var liveName = _services.CombatLookup.GetEntityName(s.Source);
        var name = !string.IsNullOrEmpty(liveName) ? liveName! : snap.Name is { Length: > 0 } ? snap.Name : "Unknown";
        s.Identity = $"{name} — Session Snapshot";

        var prof = ResolveSnapProfession(snap);
        var level = SnapAttr(snap, AttrLevel);
        var ci = CultureInfo.InvariantCulture;
        var parts = new System.Collections.Generic.List<string>(4);
        if (prof.Length > 0) parts.Add(prof);
        if (level > 0) parts.Add($"Lv {level.ToString(ci)}");
        if (snap.FightPoint > 0) parts.Add($"Ability Score {snap.FightPoint.ToString("N0", ci)}");
        if (snap.MaxHp > 0) parts.Add($"HP {snap.Hp.ToString("N0", ci)} / {snap.MaxHp.ToString("N0", ci)}");
        s.SubIdentity = string.Join("   ·   ", parts);
    }

    private string ResolveSnapProfession(EntitySnapshot snap)
    {
        var profId = (int)SnapAttr(snap, AttrProfessionId);
        if (profId <= 0) return "";
        var prof = _services.GameData.Combat.GetProfession(profId);
        return prof is { Name: { Length: > 0 } n } ? n : $"Class {profId}";
    }

    private void BuildSnapStats(SnapshotState s)
    {
        s.Stats.Clear();
        var snap = s.Snap;
        foreach (var id in SnapKeyStatIds)
        {
            if (s.Stats.Count >= MaxSnapStatRows) break;
            if (!TrySnapAttr(snap, id, out var raw)) continue;   // non-zero attrs only were stored
            var info = _services.GameData.Combat.GetAttribute(id);
            var label = info is { Name.Length: > 0 } a ? a.Name : $"Attr{id}";
            s.Stats.Add((label, FormatSnapAttr(info?.NumType ?? -1, raw)));
        }
    }

    private void BuildSnapGear(SnapshotState s)
    {
        s.Gear.Clear();
        var snap = s.Snap;
        for (var i = 0; i < snap.GearItemIds.Length && s.Gear.Count < MaxSnapGearRows; i++)
        {
            var itemId = snap.GearItemIds[i];
            var slotName = ResolveSnapSlotName(itemId, snap.GearSlots[i]);
            var item = _services.GameData.Inventory.GetItem(itemId);
            var name = item is { Name.Length: > 0 } it ? it.Name : $"<unknown {itemId}>";
            s.Gear.Add((slotName, name));
        }
    }

    private string ResolveSnapSlotName(int itemId, int fallbackSlot)
    {
        var part = _services.GameData.Equip.GetEquipRow(itemId)?.EquipPart ?? fallbackSlot;
        return _services.GameData.Equip.GetSlotName(part) ?? $"Slot {fallbackSlot}";
    }

    private void BuildSnapSkills(SnapshotState s)
    {
        s.Skills.Clear();
        var snap = s.Snap;
        var ci = CultureInfo.InvariantCulture;
        for (var i = 0; i < snap.SkillIds.Length && s.Skills.Count < MaxSnapSkillRows; i++)
        {
            var skillId = snap.SkillIds[i];
            var info = _services.GameData.Combat.GetSkill(skillId);
            var name = info is { Name.Length: > 0 } sk ? sk.Name : $"<unknown {skillId}>";
            var sub = $"Lv {snap.SkillLevels[i].ToString(ci)}"
                    + (snap.SkillTiers[i] > 0 ? $" · Tier {snap.SkillTiers[i].ToString(ci)}" : "");
            s.Skills.Add((name, sub));
        }
    }

    private void BuildSnapFashion(SnapshotState s)
    {
        s.Fashion.Clear();
        var snap = s.Snap;
        for (var i = 0; i < snap.FashionIds.Length; i++)
        {
            var fashionId = snap.FashionIds[i];
            var item = _services.GameData.Inventory.GetItem(fashionId);
            s.Fashion.Add(item is { Name.Length: > 0 } it ? it.Name : $"<unknown {fashionId}>");
        }
    }

    // Index-aligned attr lookup over the snapshot's parallel arrays (clamped on load, so lengths are equal here).
    private static bool TrySnapAttr(EntitySnapshot snap, int id, out long value)
    {
        for (var i = 0; i < snap.AttrIds.Length; i++)
            if (snap.AttrIds[i] == id) { value = snap.AttrValues[i]; return true; }
        value = 0; return false;
    }

    private static long SnapAttr(EntitySnapshot snap, int id) => TrySnapAttr(snap, id, out var v) ? v : 0;

    private static string FormatSnapAttr(int numType, long raw) => numType switch
    {
        1 => (raw / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%",
        2 => (raw / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s",
        _ => raw.ToString("N0", CultureInfo.InvariantCulture),
    };

    // A read-only popup (opened from the History Inspect affordance): free-drag + ✕ close, GlassMenu chrome (same
    // as Skill Breakdown). Shared by the initial registration (Plugin.cs) and the rebuild-on-open below.
    private IWindowControl RegisterSnapshotWindow() => _services.Windows.Register(new WindowRegistration(
        new WindowSpec(
            Id:          "combatmeter.session-snapshot",
            Title:       "Session Snapshot",
            DefaultRect: new WindowRect(1040f, 120f, 460f, 520f),
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.GlassMenu)
        { StartVisible = false, HideUntilInWorld = true, Closable = true, Draggable = true,
          Resizable = true, MinWidth = 360f, MinHeight = 300f, MaxWidth = 900f, MaxHeight = 1000f },
        BuildSnapshotRoot(),
        OnClose: CloseSnapshot));

    // Open / toggle the frozen view for a player row. Re-clicking the same source closes it (mirrors the ►
    // drill-in toggle). The window subtree carries no baked colours, but we rebuild it for symmetry with the
    // Skill Breakdown pattern so the lifecycle stays uniform (cheap; fires on user action only).
    private void HandleInspectRequested(EntityId id, EncounterHistoryEntry session)
    {
        if (_snapshot is { } cur && cur.Source == id && ReferenceEquals(cur.Session, session))
        {
            CloseSnapshot();
            return;
        }
        if (!session.Entities.TryGetValue(id, out var snap)) return;   // no snapshot for this row (defensive)

        // Prefer the real Entity Inspector when a plugin provides IFrozenEntityViewer via the exchange; it renders
        // the frozen snapshot in its richer UI. Falls back to our own Session Snapshot when no viewer is installed.
        var viewer = _services.Exchange.Consume<Stellar.PluginContracts.IFrozenEntityViewer>();
        if (viewer is not null
            && viewer.ShowFrozen(BuildFrozenEntity(id, FormatSessionTimestampLong(session.ArchivedAtMs), snap)))
        {
            return;
        }

        _snapshot = new SnapshotState { Source = id, Session = session, Snap = snap };
        RebuildSnapshotRows();
        _snapshotWindow.SetVisible(true);
    }

    private void CloseSnapshot()
    {
        _snapshot = null;
        _snapshotWindow.SetVisible(false);
    }
}
