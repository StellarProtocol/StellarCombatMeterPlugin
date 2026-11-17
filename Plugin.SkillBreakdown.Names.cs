using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// Skill Breakdown — row names + icon kind for damage-attributed ids (SourceNameResolver has the order). Resolved once per
// id and cached: rows rebuild only when the drilled source or language changes, but the same ids recur across every
// encounter, and the lookup touches three game-data tables. The framework's English game text (IGameText, fw 2.33.0)
// loads in the background on first use — until then unnamed buffs fall back to "#id" — so its Ready event drops the
// cache and the built-for marker once, and the open breakdown re-resolves on its next tick.
public sealed partial class Plugin
{
    // Curated display-name overrides for damage-attributed ids whose game-table name is unhelpful — e.g. DoT/proc
    // buffs whose English Name column is empty in this client (only a source-language NameDesign exists). Mirrors
    // ZDPS's SkillOverrides; wins over the buff-table name so the lance lucky-counter proc reads "Lucky Strike
    // (Lance)" instead of "骑枪-幸运" or a raw id. Extend as ids surface — the [name] diagnostic (gated on
    // STELLAR_DIAGNOSTICS) logs each unresolved id once so they can be added here.
    private static readonly Dictionary<int, string> SkillNameOverrides = new()
    {
        [2031104] = "Lucky Strike (Lance)",   // BuffTable "Lance - Luck" / NameDesign 骑枪-幸运 — Wind Knight lance proc
    };

    private readonly Dictionary<int, (string Name, bool BuffIcon)> _sourceNames = new();
    private bool _gameTextHooked;

    private string ResolveSkillName(int skillId) => SourceName(skillId).Name;

    private (string Name, bool BuffIcon) SourceName(int id)
    {
        if (_sourceNames.TryGetValue(id, out var cached)) return cached;
        var text = _services.GameText;
        EnsureGameTextHooked();   // also starts the load

        var skill = _services.GameData.Combat.GetSkill(id);
        var buff = _services.GameData.Combat.GetBuff(id);
        var name = SourceNameResolver.Resolve(id, skill?.Name, buff?.Name, SkillNameOverrides, text);
        if (name == null)
        {
            if (text.IsReady) LogUnresolvedSkillName(id);   // before Ready every unnamed buff would log
            name = "#" + id;
        }
        var entry = (name, skill == null && buff != null);
        _sourceNames[id] = entry;
        return entry;
    }

    private void EnsureGameTextHooked()
    {
        if (_gameTextHooked) return;
        _gameTextHooked = true;
        _services.GameText.Ready += OnGameTextReady;
    }

    // Main thread (framework-marshalled), once: names resolved during the load window may have been "#id" — the
    // breakdown's and the hidden-effect labels (Plugin.EffectNames.cs).
    private void OnGameTextReady()
    {
        _sourceNames.Clear();
        _sbBuiltFor = null;
        RefreshEffectLabels();
    }

    private void UnhookGameText()
    {
        if (_gameTextHooked) _services.GameText.Ready -= OnGameTextReady;
    }
}
