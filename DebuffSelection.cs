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

    /// <summary>How <see cref="_selected"/> is interpreted. Defaults to <see cref="DebuffTrackMode.ShowAllExceptSelected"/>
    /// so an empty selection shows every named, icon'd debuff — never an empty strip out of the box.</summary>
    public DebuffTrackMode Mode { get; set; } = DebuffTrackMode.ShowAllExceptSelected;

    /// <summary>When true, debuffs with no display name (icon-only rows) are also eligible to show.
    /// Defaults to false — unnamed rows are usually internal markers the player has no context for.</summary>
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

    /// <summary>Populates a new <see cref="DebuffSelection"/> from a persisted config section.</summary>
    public static DebuffSelection Load(IConfigSection cfg)
    {
        var sel = new DebuffSelection();
        foreach (var id in cfg.Get("debuffs.track", Array.Empty<int>()) ?? Array.Empty<int>())
            sel._selected.Add(id);
        sel.Mode = (DebuffTrackMode)cfg.Get("debuffs.mode", 0);
        sel.ShowHidden = cfg.Get("debuffs.showHidden", false);
        return sel;
    }

    /// <summary>Persists the checked set, mode and show-hidden flag to <paramref name="cfg"/> and calls
    /// <see cref="IConfigSection.Save"/>.</summary>
    public void Save(IConfigSection cfg)
    {
        cfg.Set("debuffs.track", ToArray());
        cfg.Set("debuffs.mode", (int)Mode);
        cfg.Set("debuffs.showHidden", ShowHidden);
        cfg.Save();
    }

    private int[] ToArray()
    {
        var arr = new int[_selected.Count];
        _selected.CopyTo(arr);
        return arr;
    }
}
