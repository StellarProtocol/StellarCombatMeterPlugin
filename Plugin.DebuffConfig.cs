using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Party-focus debuff config panel (owner request 2026-09-24). A faithful port of CooldownBar's Debuffs
// Track tab (StellarCooldownBarPlugin Plugin.Settings.cs BuildDebuffsTab — VirtualListElement + pooled
// row + search-filter + per-row-toggle), but sourced from the "seen debuffs" catalog Plugin.Debuffs.cs
// builds live off the combat wire (Plugin.Debuffs.cs's _seenDebuffs) instead of a reflected game table.
// Drives the already-built DebuffSelection model (mode + show-hidden + per-id checked set); opened from
// the Appearance panel's "Configure debuffs…" button (Plugin.Settings.cs, party tabs only).
public sealed partial class Plugin
{
    private const int DebuffConfigPoolSize = 24;

    // Raw/filtered state — mirrors CooldownBar's _dt* fields, minus the description column this panel
    // doesn't need. _dcNames is the filtered set's display names (empty string = unnamed, icon-only row;
    // DcLabel falls back to "#<id>" for those).
    private string   _dcFilter = "";
    private int      _dcOffset;
    private int      _dcFiltCount;
    private int[]    _dcFiltIds = Array.Empty<int>();
    private string[] _dcNames = Array.Empty<string>();
    private bool     _dcScrollReset;
    private readonly UvRect[] _dcUv = new UvRect[DebuffConfigPoolSize];

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

    // Re-applies the filter on open so a debuff seen since the panel was last shown is listed immediately.
    private void ToggleDebuffConfig()
    {
        if (!_debuffConfigWindow.IsShown) ApplyDebuffCfgFilter(_dcFilter);
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
            DebuffConfigModeRow(),
            new TextElement(() => _loc.T("debuffcfg.mode.help"), MutedCol),
            DebuffConfigShowHiddenRow(),
            new SeparatorElement(),
            new InputElement(Get: () => _dcFilter, Submit: ApplyDebuffCfgFilter, Width: 340f, OnChange: ApplyDebuffCfgFilter),
            new TextElement(() => _loc.TFormat("debuffcfg.count", _dcFiltCount, _seenDebuffs.Count)),
            new VirtualListElement(
                Count:    () => _dcFiltCount,
                RowHeight: 32f, Pool: pool,
                OnWindow: i => _dcOffset = i, Height: 360f)
            { ResetScroll = () => { if (!_dcScrollReset) return false; _dcScrollReset = false; return true; } },
        }, Gap: 4f);
    }

    private HudElement DebuffConfigModeRow()
        => new RowElement(new HudElement[]
        {
            new ButtonElement(() => _loc.T("debuffcfg.mode.all"),
                () => SetDebuffMode(DebuffTrackMode.ShowAllExceptSelected),
                Active: () => _debuffSelection.Mode == DebuffTrackMode.ShowAllExceptSelected, Width: 168f),
            new ButtonElement(() => _loc.T("debuffcfg.mode.selected"),
                () => SetDebuffMode(DebuffTrackMode.ShowOnlySelected),
                Active: () => _debuffSelection.Mode == DebuffTrackMode.ShowOnlySelected, Width: 168f),
        }, Gap: 6f);

    private void SetDebuffMode(DebuffTrackMode mode)
    {
        _debuffSelection.Mode = mode;
        _debuffSelection.Save(_prefs);
        _dcScrollReset = true;
    }

    private HudElement DebuffConfigShowHiddenRow()
        => new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", () => _debuffSelection.ShowHidden, SetDebuffShowHidden),
            new TextElement(() => _loc.T("debuffcfg.showHidden")),
        }, Gap: 8f);

    // Re-applies the filter immediately: toggling ShowHidden changes which unnamed rows are eligible, so
    // the list must refresh in place rather than waiting for the next search keystroke.
    private void SetDebuffShowHidden(bool v)
    {
        _debuffSelection.ShowHidden = v;
        _debuffSelection.Save(_prefs);
        ApplyDebuffCfgFilter(_dcFilter);
    }

    private void ApplyDebuffCfgFilter(string text)
    {
        _dcFilter = text;
        _dcOffset = 0;
        FilterSeenDebuffs(_seenDebuffs, text, _debuffSelection.ShowHidden, out _dcFiltIds, out _dcNames, out _dcFiltCount);
    }

    /// <summary>
    /// Pure/static: snapshots the seen-debuff catalog into the filtered id/name arrays the panel's virtual
    /// list reads. A row is eligible to list at all when it has an icon AND (<paramref name="showHidden"/>
    /// or it has a non-empty name) — the same two gates <see cref="DebuffSelection.ShouldShow"/> applies to
    /// the live strip, so the panel never offers a toggle for something the strip could never show anyway.
    /// Eligible rows are then filtered by a case-insensitive name-or-id substring match against
    /// <paramref name="text"/>, and sorted by name (then id) so unnamed rows — which sort first under an
    /// empty name — cluster together.
    /// </summary>
    internal static void FilterSeenDebuffs(
        IReadOnlyDictionary<int, (string name, bool hasIcon)> seen, string text, bool showHidden,
        out int[] ids, out string[] names, out int count)
    {
        var q = text?.Trim() ?? "";
        var kept = new List<(int id, string name)>(seen.Count);
        foreach (var (id, entry) in seen)
        {
            if (!entry.hasIcon) continue;
            if (!showHidden && string.IsNullOrEmpty(entry.name)) continue;
            if (q.Length > 0 && !MatchesDebuffQuery(id, entry.name, q)) continue;
            kept.Add((id, entry.name));
        }
        kept.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.name, b.name);
            return c != 0 ? c : a.id.CompareTo(b.id);
        });
        ids = new int[kept.Count];
        names = new string[kept.Count];
        for (int i = 0; i < kept.Count; i++) { ids[i] = kept[i].id; names[i] = kept[i].name; }
        count = kept.Count;
    }

    private static bool MatchesDebuffQuery(int id, string name, string query)
        => name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
           || id.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase);

    // ── Row helpers ──────────────────────────────────────────────────────────

    private object? DcIcon(int idx)
    {
        int i = _dcOffset + idx;
        if (i >= _dcFiltCount) { _dcUv[idx] = default; return null; }
        return _services.GameAssets.LoadBuffIcon(_dcFiltIds[i], out _dcUv[idx]);
    }

    private string DcLabel(int idx)
    {
        int i = _dcOffset + idx;
        if (i >= _dcFiltCount) return "";
        var name = _dcNames[i];
        return string.IsNullOrEmpty(name) ? $"#{_dcFiltIds[i]}" : name;
    }

    private bool DcTracked(int idx)
    {
        int i = _dcOffset + idx;
        return i < _dcFiltCount && _debuffSelection.IsSelected(_dcFiltIds[i]);
    }

    private void SetDcTracked(int idx, bool on)
    {
        int i = _dcOffset + idx;
        if (i >= _dcFiltCount) return;
        _debuffSelection.SetSelected(_dcFiltIds[i], on);
        _debuffSelection.Save(_prefs);
    }
}
