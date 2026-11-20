using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Party buffs & debuffs pick-list MODEL (owner request 2026-09-24): the catalog, the kind / mode / show-hidden
// switches, the search filter and the per-row group state behind Settings › Effects (the UI is
// Plugin.SettingsEffects.cs; it replaced the old standalone "Configure buffs & debuffs" window). A port of
// CooldownBar's Debuffs Track tab over the WHOLE game BuffTable (every iconed buff/debuff, built once per session by
// the sliced job in Plugin.EffectCatalog.cs), split by kind, ticked rows sorted first. Drives the DebuffSelection model
// (mode + show-hidden + per-id checked set).
public sealed partial class Plugin
{
    private const int DebuffConfigPoolSize = 14;   // ceil(10-row viewport / 28) + 3 (KB VirtualList pool rule)

    // One config row: a group of same-NAMED effects (like CooldownBar's "Cuisine ×136"). Ids is every base id
    // sharing Name; toggling the row toggles them all. Game-hidden rows (Hidden) are single-id groups (Count = 1) whose
    // Name is the hidden-effect label (Plugin.EffectNames.cs: "Design name (id)" / "#id").
    // Ticked is the SORT-TIME selection state (ticked rows sort first); the row's live tick box reads the selection itself.
    internal readonly record struct EffectGroup(string Name, int Count, int RepId, int[] Ids, bool Hidden = false, bool Ticked = false);

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

    // Full pick-list catalog of the WHOLE game BuffTable (every buff/debuff, like CooldownBar lists every
    // skill/buff) — NOT limited to what's been seen live. Keyed by id → (name, isBuff, hidden): name is the
    // SHOWN label — the game name, or for a game-hidden effect (hidden = its game name is empty/placeholder) its
    // EffectLabel. The two tabs filter this by kind. Built once per session by the sliced job in
    // Plugin.EffectCatalog.cs (first open of the tab); rebuilt once on IGameText.Ready (RefreshEffectLabels).
    private readonly Dictionary<int, (string name, bool isBuff, bool hidden)> _effectCatalog = new();
    // Eligible groups of the active tab for the current Hidden-effects switch, ignoring the search text — the "/ total"
    // of the "shown / total" hint. Recomputed with the filter, so the two numbers always count the same unit (rows).
    private int _dcTotal;

    // The active tab's selection + its persistence prefix (each tab owns an independent mode/checked/show-hidden).
    private DebuffSelection ActiveSel => _dcTab == 1 ? _buffSelection : _debuffSelection;
    private string ActiveSelPrefix => _dcTab == 1 ? DefaultTrackedEffects.BuffPrefix : DefaultTrackedEffects.DebuffPrefix;

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

    // The ONLY place the list re-sorts (ticked rows first). Runs on: search text change, tab (kind) switch, the
    // Hidden-effects switch, the Effects tab being opened / re-selected, Untick all, and catalog completion — never on
    // a tick click (SetDcTracked only bumps _dcSelVer), so a row the user just clicked stays exactly under the cursor
    // and the ticked set floats to the top the next time the list is reopened or filtered. Scroll returns to the top
    // with every re-sort, since the rows under the old scroll position are no longer the same rows.
    private void ApplyDebuffCfgFilter(string text)
    {
        _dcFilter = text;
        _dcOffset = 0;
        _dcScrollReset = true;
        if (_effCatReady)
            FilterSeenEffects(_effectCatalog, text, ActiveSel.ShowHidden, wantBuff: _dcTab == 1, _dcIsTrackedFn ??= DcIsTracked,
                              out _dcGroups, out _dcFiltCount, out _dcTotal);
        else { _dcGroups = Array.Empty<EffectGroup>(); _dcFiltCount = 0; _dcTotal = 0; }   // still loading
        _dcSelVer++;
    }

    /// <summary>
    /// Pure/static: snapshots the effect catalog into the filtered group array the panel's virtual list reads,
    /// restricted to the active tab's KIND (<paramref name="wantBuff"/>). A row is eligible to list at all when it
    /// matches the kind AND (<paramref name="showHidden"/> or it is not game-hidden) — the same gate
    /// <see cref="DebuffSelection.ShouldShow"/> applies to the live strip, so the panel never offers a toggle for
    /// something the strip could never show anyway. <paramref name="total"/> counts the eligible groups; they are then
    /// filtered by a case-insensitive label-or-id substring match against <paramref name="text"/> (the label is the
    /// shown, translated text) and sorted: TICKED groups first (every member id selected per
    /// <paramref name="isSelected"/>, null = none ticked; owner 2026-11-17 "what is ticked should order first"), then
    /// game-hidden rows (so they cluster together), then by name, then id. Each group's <see cref="EffectGroup.Ticked"/>
    /// records that sort-time state.
    /// </summary>
    internal static void FilterSeenEffects(
        IReadOnlyDictionary<int, (string name, bool isBuff, bool hidden)> seen, string text, bool showHidden,
        bool wantBuff, Func<int, bool>? isSelected, out EffectGroup[] groups, out int count, out int total)
    {
        var q = text?.Trim() ?? "";
        // Group NAMED, eligible entries by name (CooldownBar's "Name ×N"); game-hidden rows stay per-id (each is its
        // own internal effect, and a design-name label already carries its id).
        var byName = new Dictionary<string, List<int>>();
        var singles = new List<(int id, string name)>();
        foreach (var (id, entry) in seen)
        {
            if (entry.isBuff != wantBuff) continue;   // active tab: Debuffs (false) or Buffs (true)
            if (!showHidden && entry.hidden) continue;
            if (!entry.hidden)
            {
                if (!byName.TryGetValue(entry.name, out var list)) byName[entry.name] = list = new List<int>();
                list.Add(id);
            }
            else singles.Add((id, entry.name));   // game-hidden → its own single-id group, with its label
        }
        total = byName.Count + singles.Count;

        var built = new List<EffectGroup>(total);
        foreach (var (name, list) in byName)
        {
            list.Sort();
            // A group matches the query if its NAME matches OR any of its ids does.
            if (q.Length > 0 && !GroupMatchesQuery(name, list, q)) continue;
            built.Add(new EffectGroup(name, list.Count, list[0], list.ToArray(), Ticked: AllSelected(list, isSelected)));
        }
        foreach (var (id, label) in singles)
        {
            if (q.Length > 0 && !MatchesDebuffQuery(id, label, q)) continue;
            built.Add(new EffectGroup(label, 1, id, new[] { id }, Hidden: true, Ticked: isSelected != null && isSelected(id)));
        }
        built.Sort((a, b) =>
        {
            if (a.Ticked != b.Ticked) return a.Ticked ? -1 : 1;   // ticked rows first
            if (a.Hidden != b.Hidden) return a.Hidden ? -1 : 1;   // game-hidden rows next, clustering together
            var c = string.CompareOrdinal(a.Name, b.Name);
            return c != 0 ? c : a.RepId.CompareTo(b.RepId);
        });
        groups = built.ToArray();
        count = built.Count;
    }

    /// <summary>Selection-agnostic overload (nothing counts as ticked; no total) — tests and the ticked count.</summary>
    internal static void FilterSeenEffects(
        IReadOnlyDictionary<int, (string name, bool isBuff, bool hidden)> seen, string text, bool showHidden,
        bool wantBuff, out EffectGroup[] groups, out int count)
        => FilterSeenEffects(seen, text, showHidden, wantBuff, null, out groups, out count, out _);

    // A name group is "ticked" only when EVERY member id is selected (matches the row's tick box, DcSlotRow).
    private static bool AllSelected(List<int> ids, Func<int, bool>? isSelected)
    {
        if (isSelected == null) return false;
        foreach (var id in ids) if (!isSelected(id)) return false;
        return true;
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
    // Ticked state for the Settings list: by id or by name group (DebuffSelection.IsTracked). Name from the catalog row
    // (the same label the list groups by); cached delegate — the filter runs per keystroke.
    private Func<int, bool>? _dcIsTrackedFn;
    private bool DcIsTracked(int id)
        => ActiveSel.IsTracked(id, _effectCatalog.TryGetValue(id, out var e) && !e.hidden ? e.name : null,
                               _getBuffFn ??= _services.GameData.Combat.GetBuff);

    private void SetDcTracked(int idx, bool on)
    {
        int i = _dcOffset + idx;
        if (i >= _dcFiltCount) return;
        foreach (var id in _dcGroups[i].Ids) ActiveSel.SetSelected(id, on);
        // A NAMED group is also ticked as a whole, so members the game adds later (a new season's Potion/Cuisine)
        // follow it; a game-hidden single row stays a plain per-id tick.
        if (!_dcGroups[i].Hidden)
            ActiveSel.SetGroup(_dcGroups[i].RepId, _dcGroups[i].Name, on, _getBuffFn ??= _services.GameData.Combat.GetBuff);
        ActiveSel.Save(_prefs, ActiveSelPrefix);
        _dcSelVer++;
    }
}
