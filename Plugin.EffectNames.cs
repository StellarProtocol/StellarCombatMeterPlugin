using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Labels for buffs/debuffs the game leaves unnamed (owner 2026-11-17, "Hidden effects"). A buff whose game name is
// empty or a placeholder (IGameText.IsPlaceholderName: blank, the "气刃突刺计数" / "Spirit Blade Thrust count" filler,
// the framework's "Buff <id>") is a GAME-HIDDEN effect — internal sub-buffs, procs, markers. Those are listed / shown
// only under Settings › Effects "Hidden effects", and are labelled the Skill Breakdown way (SourceNameResolver, no
// skill side): the framework's English name, else its English design name + " (<id>)", else "#<id>". A real game name
// is shown as-is and never gets the id. "Hidden" is decided by the GAME name alone — a design name existing does not
// make an effect "named", so the switch keeps meaning "show what the game hides".
// Cost: labels are resolved once per id and cached; IGameText.Ready (Plugin.SkillBreakdown.Names.cs) drops the cache
// and the Effects catalog once, since anything resolved during the load window fell back to "#id".
public sealed partial class Plugin
{
    private readonly Dictionary<int, string> _effectLabels = new();
    private Func<string?, bool>? _isUnnamedFn;

    // Hoisted delegate for DebuffStrip's name gate (allocation-free; IsPlaceholderName needs no loaded table).
    private Func<string?, bool> IsUnnamedFn => _isUnnamedFn ??= _services.GameText.IsPlaceholderName;

    /// <summary>The label shown for effect <paramref name="id"/> whose game name is <paramref name="gameName"/>:
    /// the game name when it is real, else the cached hidden-effect label (see the file comment).</summary>
    private string EffectLabel(int id, string? gameName)
    {
        var text = _services.GameText;
        if (!text.IsPlaceholderName(gameName)) return gameName!;
        if (_effectLabels.TryGetValue(id, out var cached)) return cached;
        EnsureGameTextHooked();   // also starts the background load
        var label = SourceNameResolver.Resolve(id, null, gameName ?? "", SkillNameOverrides, text) ?? "#" + id;
        _effectLabels[id] = label;
        return label;
    }

    // Main thread, once on IGameText.Ready: hidden labels resolved before the table loaded read "#id".
    private void RefreshEffectLabels()
    {
        _effectLabels.Clear();
        RestartEffectCatalog();   // Plugin.EffectCatalog.cs — sliced rebuild with the real labels; no-op if never opened
    }
}
