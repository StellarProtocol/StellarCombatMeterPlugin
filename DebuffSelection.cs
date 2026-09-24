using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

/// <summary>How the checked debuff set is interpreted by <see cref="DebuffSelection.ShouldShow"/>.</summary>
public enum DebuffTrackMode
{
    /// <summary>Show every displayable debuff EXCEPT the ones that are checked (default).</summary>
    ShowAllExceptSelected = 0,
    /// <summary>Show only debuffs that are checked.</summary>
    ShowOnlySelected = 1,
}

/// <summary>
/// Persisted user selection for the party-focus debuff strip: which debuff base ids the player has
/// checked, the mode that decides what "checked" means, and whether unnamed (icon-only) debuffs are
/// also shown. Mirrors <c>CooldownBarSelection</c>'s persisted-set + mode + Load/Save shape, debuff-only.
/// Unity-free — unit-testable. Stored as an int[] + two scalars in the plugin's config section.
/// </summary>
public sealed class DebuffSelection
{
    private readonly HashSet<int> _selected = new();

    /// <summary>Out-of-the-box checked set for a NEW install (owner 2026-09-24): the three imagine-lockout
    /// debuffs whose cells show the source Battle-Imagine card — Mechanical Failure, Element Stasis, Time Stasis.
    /// Applied by <see cref="Load"/> (not the bare model) together with <see cref="DebuffTrackMode.ShowOnlySelected"/>
    /// so a fresh install shows exactly these and none of the ~8000 internal buff/debuff markers.</summary>
    public static readonly int[] DefaultChecked = { 2110049, 2110050, 2110056 };

    /// <summary>How <see cref="_selected"/> is interpreted. A bare model defaults to
    /// <see cref="DebuffTrackMode.ShowAllExceptSelected"/>; <see cref="Load"/> supplies the product default
    /// (<see cref="DebuffTrackMode.ShowOnlySelected"/>) for a fresh install.</summary>
    public DebuffTrackMode Mode { get; set; } = DebuffTrackMode.ShowAllExceptSelected;

    /// <summary>When true, effects with no display name (icon-only rows) are also eligible. Bare-model default
    /// false; <see cref="Load"/> supplies the product default (true).</summary>
    public bool ShowHidden { get; set; }

    /// <summary>The checked debuff base ids. Read-only view over the persisted set.</summary>
    public IReadOnlyCollection<int> Selected => _selected;

    /// <summary>Returns true when <paramref name="baseId"/> is in the checked set.</summary>
    public bool IsSelected(int baseId) => _selected.Contains(baseId);

    /// <summary>Adds or removes <paramref name="baseId"/> from the checked set.</summary>
    public void SetSelected(int baseId, bool on)
    {
        if (on) _selected.Add(baseId); else _selected.Remove(baseId);
    }

    /// <summary>
    /// The core display predicate shared by <c>DebuffStrip</c>: whether a debuff with the given base id
    /// should appear in the strip, given whether it has a display name and a display icon.
    /// 1) Must be renderable in the icon grid — no icon, never shown.
    /// 2) Unless <see cref="ShowHidden"/>, must have a name — unnamed rows are hidden by default.
    /// 3) Selection: <see cref="DebuffTrackMode.ShowOnlySelected"/> shows only checked ids;
    ///    <see cref="DebuffTrackMode.ShowAllExceptSelected"/> shows every eligible id except checked ones
    ///    (so an empty selection under the default mode shows everything eligible).
    /// </summary>
    public bool ShouldShow(int baseId, bool hasName, bool hasIcon)
    {
        if (!hasIcon) return false;
        if (!ShowHidden && !hasName) return false;
        return Mode == DebuffTrackMode.ShowOnlySelected ? IsSelected(baseId) : !IsSelected(baseId);
    }

    /// <summary>Populates a new <see cref="DebuffSelection"/> from a persisted config section under a per-tab key
    /// <paramref name="prefix"/> ("status.debuff" or "status.buff"), applying the supplied new-install defaults
    /// when a key is absent. The Debuffs and Buffs tabs each own an independent selection (owner 2026-09-24):
    /// their own mode + checked set + show-hidden, so "show only checked" on one tab does not affect the other.</summary>
    public static DebuffSelection Load(
        IConfigSection cfg, string prefix, int[] defaultChecked, DebuffTrackMode defaultMode, bool defaultShowHidden)
    {
        var sel = new DebuffSelection();
        foreach (var id in cfg.Get($"{prefix}.track", defaultChecked) ?? defaultChecked)
            sel._selected.Add(id);
        sel.Mode = (DebuffTrackMode)cfg.Get($"{prefix}.mode", (int)defaultMode);
        sel.ShowHidden = cfg.Get($"{prefix}.showHidden", defaultShowHidden);
        return sel;
    }

    /// <summary>Persists the checked set, mode and show-hidden flag under the per-tab key <paramref name="prefix"/>
    /// and calls <see cref="IConfigSection.Save"/>.</summary>
    public void Save(IConfigSection cfg, string prefix)
    {
        cfg.Set($"{prefix}.track", ToArray());
        cfg.Set($"{prefix}.mode", (int)Mode);
        cfg.Set($"{prefix}.showHidden", ShowHidden);
        cfg.Save();
    }

    private int[] ToArray()
    {
        var arr = new int[_selected.Count];
        _selected.CopyTo(arr);
        return arr;
    }
}
