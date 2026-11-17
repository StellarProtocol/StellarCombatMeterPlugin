using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Party buffs & debuffs pick-list MODEL (owner request 2026-09-24): the catalog, the kind / mode / show-hidden
// switches, the search filter and the per-row group state behind Settings › Effects (the UI is
// Plugin.SettingsEffects.cs; it replaced the old standalone "Configure buffs & debuffs" window). A port of
// CooldownBar's Debuffs Track tab, sourced from the FULL game BuffTable via IGameDataCombat.AllBuffs() (every iconed
// buff/debuff), split by kind. Drives the DebuffSelection model (mode + show-hidden + per-id checked set).
public sealed partial class Plugin
{
    private const int DebuffConfigPoolSize = 14;   // ceil(10-row viewport / 28) + 3 (KB VirtualList pool rule)

    // One config row: a group of same-NAMED effects (like CooldownBar's "Cuisine ×136"). Ids is every base id
    // sharing Name; toggling the row toggles them all. Game-hidden rows (Hidden) are single-id groups (Count = 1) whose
    // Name is the hidden-effect label (Plugin.EffectNames.cs: "Design name (id)" / "#id").
    internal readonly record struct EffectGroup(string Name, int Count, int RepId, int[] Ids, bool Hidden = false);

    // Raw/filtered state — mirrors CooldownBar's _dt* fields, minus the description column this panel
    // doesn't need. A group's Name is what its row shows (DcSlotRow falls back to "#<id>" for an empty one).
    private string   _dcFilter = "";
    private int      _dcOffset;
    private int      _dcFiltCount;
    private EffectGroup[] _dcGroups = Array.Empty<EffectGroup>();
    private bool     _dcScrollReset;
    private int      _dcTab;   // 0 = Debuffs tab, 1 = Buffs tab (which selection + kind the panel edits)
    private readonly UvRect[] _dcUv = new UvRect[DebuffConfigPoolSize];
    // Bumped on every selection / mode / filter / kind change: invalidates the per-slot tick caches and the
    // ticked count (Plugin.SettingsEffects.cs), so nothing is recomputed per refresh.
    private int      _dcSelVer;

    // Full pick-list catalog built ONCE from IGameDataCombat.AllBuffs() (every iconed buff/debuff in the game's
    // BuffTable, like CooldownBar lists every skill/buff) — NOT limited to what's been seen live. Keyed by id →
    // (name, hasIcon, isBuff, hidden): name is the SHOWN label — the game name, or for a game-hidden effect (hidden =
    // its game name is empty/placeholder) its EffectLabel. The two tabs filter this by kind. Built lazily (the table
    // may be empty at init); dropped and rebuilt once on IGameText.Ready (RefreshEffectLabels).
    private readonly Dictionary<int, (string name, bool hasIcon, bool isBuff, bool hidden)> _effectCatalog = new();
    private int _dcDebuffTotal, _dcBuffTotal;

    // The active tab's selection + its persistence prefix (each tab owns an independent mode/checked/show-hidden).
    private DebuffSelection ActiveSel => _dcTab == 1 ? _buffSelection : _debuffSelection;
    private string ActiveSelPrefix => _dcTab == 1 ? DefaultTrackedEffects.BuffPrefix : DefaultTrackedEffects.DebuffPrefix;

    // Materialise the full buff/debuff catalog from the game table once it is loaded (rebuilds only while empty).
    private void EnsureEffectCatalog()
    {
        if (_effectCatalog.Count > 0) return;
        _dcDebuffTotal = _dcBuffTotal = 0;
        foreach (var b in _services.GameData.Combat.AllBuffs())
        {
            if (string.IsNullOrEmpty(b.IconPath)) continue;   // iconed effects only (an icon-less row can never render)
            var isBuff = !b.IsDebuff;
            var hidden = _services.GameText.IsPlaceholderName(b.Name);
            _effectCatalog[b.Id] = (hidden ? EffectLabel(b.Id, b.Name) : b.Name!, true, isBuff, hidden);
            if (isBuff) _dcBuffTotal++; else _dcDebuffTotal++;
        }
    }

    private void SetDebuffConfigTab(int tab)
    {
        if (_dcTab == tab) return;
        _dcTab = tab;
        _dcScrollReset = true;
        ApplyDebuffCfgFilter(_dcFilter);
    }

    private void SetDebuffMode(DebuffTrackMode mode)
    {
        ActiveSel.Mode = mode;
        ActiveSel.Save(_prefs, ActiveSelPrefix);
        _dcScrollReset = true;
        _dcSelVer++;
    }

    // Re-applies the filter immediately: toggling ShowHidden changes which game-hidden rows are eligible, so
    // the list must refresh in place rather than waiting for the next search keystroke.
    private void SetDebuffShowHidden(bool v)
    {
        ActiveSel.ShowHidden = v;
        ActiveSel.Save(_prefs, ActiveSelPrefix);
        ApplyDebuffCfgFilter(_dcFilter);
    }

    private void ApplyDebuffCfgFilter(string text)
    {
        _dcFilter = text;
        _dcOffset = 0;
        EnsureEffectCatalog();
        FilterSeenEffects(_effectCatalog, text, ActiveSel.ShowHidden, wantBuff: _dcTab == 1,
                          out _dcGroups, out _dcFiltCount);
        _dcSelVer++;
    }

    /// <summary>
    /// Pure/static: snapshots the seen-effect catalog into the filtered id/name arrays the panel's virtual
    /// list reads, restricted to the active tab's KIND (<paramref name="wantBuff"/>). A row is eligible to list
    /// at all when it matches the kind AND has an icon AND (<paramref name="showHidden"/> or it is not game-hidden)
    /// — the same gates <see cref="DebuffSelection.ShouldShow"/> applies to the live strip, so the panel
    /// never offers a toggle for something the strip could never show anyway. Eligible rows are then filtered by
    /// a case-insensitive label-or-id substring match against <paramref name="text"/> (the label is the shown,
    /// translated text), and sorted game-hidden rows first (so they cluster together), then by name, then id.
    /// </summary>
    internal static void FilterSeenEffects(
        IReadOnlyDictionary<int, (string name, bool hasIcon, bool isBuff, bool hidden)> seen, string text, bool showHidden,
        bool wantBuff, out EffectGroup[] groups, out int count)
    {
        var q = text?.Trim() ?? "";
        // Group NAMED, eligible entries by name (CooldownBar's "Name ×N"); game-hidden rows stay per-id (each is its
        // own internal effect, and a design-name label already carries its id).
        var byName = new Dictionary<string, List<int>>();
        var singles = new List<(int id, string name)>();
        foreach (var (id, entry) in seen)
        {
            if (entry.isBuff != wantBuff) continue;   // active tab: Debuffs (false) or Buffs (true)
            if (!entry.hasIcon) continue;
            if (!showHidden && entry.hidden) continue;
            if (!entry.hidden)
            {
                if (!byName.TryGetValue(entry.name, out var list)) byName[entry.name] = list = new List<int>();
                list.Add(id);
            }
            else singles.Add((id, entry.name));   // game-hidden → its own single-id group, with its label
        }

        var built = new List<EffectGroup>(byName.Count + singles.Count);
        foreach (var (name, list) in byName)
        {
            list.Sort();
            // A group matches the query if its NAME matches OR any of its ids does.
            if (q.Length > 0 && !GroupMatchesQuery(name, list, q)) continue;
            built.Add(new EffectGroup(name, list.Count, list[0], list.ToArray()));
        }
        foreach (var (id, label) in singles)
        {
            if (q.Length > 0 && !MatchesDebuffQuery(id, label, q)) continue;
            built.Add(new EffectGroup(label, 1, id, new[] { id }, Hidden: true));
        }
        built.Sort((a, b) =>
        {
            if (a.Hidden != b.Hidden) return a.Hidden ? -1 : 1;   // game-hidden rows first, clustering together
            var c = string.CompareOrdinal(a.Name, b.Name);
            return c != 0 ? c : a.RepId.CompareTo(b.RepId);
        });
        groups = built.ToArray();
        count = built.Count;
    }

    private static bool GroupMatchesQuery(string name, List<int> ids, string query)
    {
        if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        foreach (var id in ids)
            if (id.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool MatchesDebuffQuery(int id, string name, string query)
        => name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
           || id.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase);

    // Toggles the WHOLE name group in pool slot `idx` (a group is "checked" only when every member id is selected, so a
    // partial group reads off and one click selects all of it); selection stays per-id.
    private void SetDcTracked(int idx, bool on)
    {
        int i = _dcOffset + idx;
        if (i >= _dcFiltCount) return;
        foreach (var id in _dcGroups[i].Ids) ActiveSel.SetSelected(id, on);
        ActiveSel.Save(_prefs, ActiveSelPrefix);
        _dcSelVer++;
    }
}
