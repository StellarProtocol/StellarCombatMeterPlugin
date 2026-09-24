using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Party-focus debuff config panel (owner request 2026-09-24). A faithful port of CooldownBar's Debuffs
// Track tab (StellarCooldownBarPlugin Plugin.Settings.cs BuildDebuffsTab — VirtualListElement + pooled
// row + search-filter + per-row-toggle), but sourced from the "seen debuffs" catalog Plugin.Debuffs.cs
// sourced from the FULL game BuffTable via IGameDataCombat.AllBuffs() (every iconed buff/debuff), split by kind.
// Drives the already-built DebuffSelection model (mode + show-hidden + per-id checked set); opened from
// the Appearance panel's "Configure debuffs…" button (Plugin.Settings.cs, party tabs only).
public sealed partial class Plugin
{
    private const int DebuffConfigPoolSize = 24;

    // One config row: a group of same-NAMED effects (like CooldownBar's "Cuisine ×136"). Ids is every base id
    // sharing Name; toggling the row toggles them all. Unnamed rows are single-id groups (Name = "", Count = 1).
    internal readonly record struct EffectGroup(string Name, int Count, int RepId, int[] Ids);

    // Raw/filtered state — mirrors CooldownBar's _dt* fields, minus the description column this panel
    // doesn't need. _dcNames is the filtered set's display names (empty string = unnamed, icon-only row;
    // DcLabel falls back to "#<id>" for those).
    private string   _dcFilter = "";
    private int      _dcOffset;
    private int      _dcFiltCount;
    private EffectGroup[] _dcGroups = Array.Empty<EffectGroup>();
    private bool     _dcScrollReset;
    private int      _dcTab;   // 0 = Debuffs tab, 1 = Buffs tab (which selection + kind the panel edits)
    private readonly UvRect[] _dcUv = new UvRect[DebuffConfigPoolSize];

    // Full pick-list catalog built ONCE from IGameDataCombat.AllBuffs() (every iconed buff/debuff in the game's
    // BuffTable, like CooldownBar lists every skill/buff) — NOT limited to what's been seen live. Keyed by id →
    // (name, hasIcon, isBuff). The two tabs filter this by kind. Built lazily (the table may be empty at init).
    private readonly Dictionary<int, (string name, bool hasIcon, bool isBuff)> _effectCatalog = new();
    private int _dcDebuffTotal, _dcBuffTotal;

    // The active tab's selection + its persistence prefix (each tab owns an independent mode/checked/show-hidden).
    private DebuffSelection ActiveSel => _dcTab == 1 ? _buffSelection : _debuffSelection;
    private string ActiveSelPrefix => _dcTab == 1 ? "status.buff" : "status.debuff";

    // Materialise the full buff/debuff catalog from the game table once it is loaded (rebuilds only while empty).
    private void EnsureEffectCatalog()
    {
        if (_effectCatalog.Count > 0) return;
        _dcDebuffTotal = _dcBuffTotal = 0;
        foreach (var b in _services.GameData.Combat.AllBuffs())
        {
            if (string.IsNullOrEmpty(b.IconPath)) continue;   // iconed effects only (an icon-less row can never render)
            var isBuff = !b.IsDebuff;
            _effectCatalog[b.Id] = (b.Name ?? "", true, isBuff);
            if (isBuff) _dcBuffTotal++; else _dcDebuffTotal++;
        }
    }

    private IWindowControl BuildAndRegisterDebuffConfig()
        => _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "combatmeter.debuffConfig",
                Title:       _loc.T("debuffcfg.title"),   // baked; rebuilt on LanguageChanged
                DefaultRect: new WindowRect(900f, 120f, 380f, 540f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = false, Closable = true, Draggable = true, Resizable = true,
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            BuildDebuffConfigRoot(),
            OnClose: () => _debuffConfigWindow.SetVisible(false)));

    // Builds the catalog + re-applies the filter on open (the game table may not have been loaded when the panel
    // was first built, so a fresh EnsureEffectCatalog on open picks it up).
    private void ToggleDebuffConfig()
    {
        if (!_debuffConfigWindow.IsShown) { EnsureEffectCatalog(); ApplyDebuffCfgFilter(_dcFilter); }
        _debuffConfigWindow.SetVisible(!_debuffConfigWindow.IsShown);
    }

    private HudElement BuildDebuffConfigRoot()
    {
        var pool = new HudElement[DebuffConfigPoolSize];
        for (int i = 0; i < DebuffConfigPoolSize; i++)
        {
            int idx = i;
            pool[i] = new ConditionalElement(
                () => _dcOffset + idx < _dcFiltCount,
                new RowElement(new HudElement[]
                {
                    new CellElement(new GameTextureElement(() => DcIcon(idx), 22, 22, () => _dcUv[idx]), Width: 26f),
                    new TextElement(() => DcLabel(idx)),
                    new SpacerElement(Width: 0f),
                    new ToggleElement(() => "", () => DcTracked(idx), v => SetDcTracked(idx, v)),
                }, Gap: 4f));
        }
        return new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.T("debuffcfg.title"), Emphasis: true),
            DebuffConfigTabRow(),
            DebuffConfigModeRow(),
            new TextElement(() => _loc.T("debuffcfg.mode.help"), MutedCol),
            DebuffConfigShowHiddenRow(),
            new SeparatorElement(),
            new InputElement(Get: () => _dcFilter, Submit: ApplyDebuffCfgFilter, Width: 340f, OnChange: ApplyDebuffCfgFilter),
            new TextElement(() => _loc.TFormat("debuffcfg.count", _dcFiltCount, _dcTab == 1 ? _dcBuffTotal : _dcDebuffTotal)),
            new VirtualListElement(
                Count:    () => _dcFiltCount,
                RowHeight: 32f, Pool: pool,
                OnWindow: i => _dcOffset = i, Height: 360f)
            { ResetScroll = () => { if (!_dcScrollReset) return false; _dcScrollReset = false; return true; } },
        }, Gap: 4f);
    }

    // Debuffs | Buffs tab — the top-level switch. Each tab edits its OWN selection (mode + checked set +
    // show-hidden below apply to the active tab only), so "show only checked" on Debuffs is independent of Buffs
    // (owner 2026-09-24).
    private HudElement DebuffConfigTabRow()
        => new RowElement(new HudElement[]
        {
            new ButtonElement(() => _loc.T("debuffcfg.tab.debuffs"),
                () => SetDebuffConfigTab(0), Active: () => _dcTab == 0, Width: 168f),
            new ButtonElement(() => _loc.T("debuffcfg.tab.buffs"),
                () => SetDebuffConfigTab(1), Active: () => _dcTab == 1, Width: 168f),
        }, Gap: 6f);

    private void SetDebuffConfigTab(int tab)
    {
        if (_dcTab == tab) return;
        _dcTab = tab;
        _dcScrollReset = true;
        ApplyDebuffCfgFilter(_dcFilter);
    }

    // Show all except checked | Show only checked — the active tab's own mode.
    private HudElement DebuffConfigModeRow()
        => new RowElement(new HudElement[]
        {
            new ButtonElement(() => _loc.T("debuffcfg.mode.all"),
                () => SetDebuffMode(DebuffTrackMode.ShowAllExceptSelected),
                Active: () => ActiveSel.Mode == DebuffTrackMode.ShowAllExceptSelected, Width: 168f),
            new ButtonElement(() => _loc.T("debuffcfg.mode.selected"),
                () => SetDebuffMode(DebuffTrackMode.ShowOnlySelected),
                Active: () => ActiveSel.Mode == DebuffTrackMode.ShowOnlySelected, Width: 168f),
        }, Gap: 6f);

    private void SetDebuffMode(DebuffTrackMode mode)
    {
        ActiveSel.Mode = mode;
        ActiveSel.Save(_prefs, ActiveSelPrefix);
        _dcScrollReset = true;
    }

    private HudElement DebuffConfigShowHiddenRow()
        => new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", () => ActiveSel.ShowHidden, SetDebuffShowHidden),
            new TextElement(() => _loc.T("debuffcfg.showHidden")),
        }, Gap: 8f);

    // Re-applies the filter immediately: toggling ShowHidden changes which unnamed rows are eligible, so
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
    }

    /// <summary>
    /// Pure/static: snapshots the seen-effect catalog into the filtered id/name arrays the panel's virtual
    /// list reads, restricted to the active tab's KIND (<paramref name="wantBuff"/>). A row is eligible to list
    /// at all when it matches the kind AND has an icon AND (<paramref name="showHidden"/> or it has a non-empty
    /// name) — the same gates <see cref="DebuffSelection.ShouldShow"/> applies to the live strip, so the panel
    /// never offers a toggle for something the strip could never show anyway. Eligible rows are then filtered by
    /// a case-insensitive name-or-id substring match against <paramref name="text"/>, and sorted by name (then
    /// id) so unnamed rows — which sort first under an empty name — cluster together.
    /// </summary>
    internal static void FilterSeenEffects(
        IReadOnlyDictionary<int, (string name, bool hasIcon, bool isBuff)> seen, string text, bool showHidden,
        bool wantBuff, out EffectGroup[] groups, out int count)
    {
        var q = text?.Trim() ?? "";
        // Group NAMED, eligible entries by name (CooldownBar's "Name ×N"); unnamed rows stay per-id.
        var byName = new Dictionary<string, List<int>>();
        var singles = new List<(int id, string name)>();
        foreach (var (id, entry) in seen)
        {
            if (entry.isBuff != wantBuff) continue;   // active tab: Debuffs (false) or Buffs (true)
            if (!entry.hasIcon) continue;
            var named = !string.IsNullOrEmpty(entry.name);
            if (!showHidden && !named) continue;
            if (named)
            {
                if (!byName.TryGetValue(entry.name, out var list)) byName[entry.name] = list = new List<int>();
                list.Add(id);
            }
            else singles.Add((id, ""));   // unnamed → its own single-id group
        }

        var built = new List<EffectGroup>(byName.Count + singles.Count);
        foreach (var (name, list) in byName)
        {
            list.Sort();
            // A group matches the query if its NAME matches OR any of its ids does.
            if (q.Length > 0 && !GroupMatchesQuery(name, list, q)) continue;
            built.Add(new EffectGroup(name, list.Count, list[0], list.ToArray()));
        }
        foreach (var (id, _) in singles)
        {
            if (q.Length > 0 && !MatchesDebuffQuery(id, "", q)) continue;
            built.Add(new EffectGroup("", 1, id, new[] { id }));
        }
        built.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.Name, b.Name);   // "" (unnamed) sorts first, clustering together
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

    // ── Row helpers ──────────────────────────────────────────────────────────

    private object? DcIcon(int idx)
    {
        int i = _dcOffset + idx;
        if (i >= _dcFiltCount) { _dcUv[idx] = default; return null; }
        return _services.GameAssets.LoadBuffIcon(_dcGroups[i].RepId, out _dcUv[idx]);
    }

    private string DcLabel(int idx)
    {
        int i = _dcOffset + idx;
        if (i >= _dcFiltCount) return "";
        var g = _dcGroups[i];
        var name = string.IsNullOrEmpty(g.Name) ? $"#{g.RepId}" : g.Name;
        return g.Count > 1 ? $"{name} ×{g.Count}" : name;   // "Cuisine ×136"
    }

    // A group is "checked" when EVERY id in it is selected; toggling flips them all together.
    private bool DcTracked(int idx)
    {
        int i = _dcOffset + idx;
        if (i >= _dcFiltCount) return false;
        foreach (var id in _dcGroups[i].Ids) if (!ActiveSel.IsSelected(id)) return false;
        return true;
    }

    private void SetDcTracked(int idx, bool on)
    {
        int i = _dcOffset + idx;
        if (i >= _dcFiltCount) return;
        foreach (var id in _dcGroups[i].Ids) ActiveSel.SetSelected(id, on);
        ActiveSel.Save(_prefs, ActiveSelPrefix);
    }
}
