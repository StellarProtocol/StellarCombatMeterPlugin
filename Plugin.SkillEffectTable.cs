using System;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// SkillEffectTable sub-hit id → parent SkillId, for Skill Breakdown names (SourceNameResolver step 5). Some damage rows
// carry a SkillEffectTable id instead of a skill/buff id — e.g. 230501 "艾露娜AIRATK" → SkillId 2305, 230401 → 2304 — and
// read "#230501" without this. The framework's IGameDataCombat has no SkillEffect lookup, so this reads the game table
// directly, the DbmTable way (Boss-DBM-Skill-Timers.md): static Bokura.SkillEffectTableBase.GetTable(bool) →
// ZTable<int,SkillEffectTableBase>, 3-arg TryGetValue(id, out row, false), row.SkillId (int). The game's own damage panel
// (dmg_skill_loop_item.lua) uses the same table. Runs only for ids no other step named, once per id (the caller caches),
// so the reflection Invoke's arg-array allocation is off every hot path.
public sealed partial class Plugin
{
    private bool _seReflResolved;        // type + GetTable resolved (once)
    private MethodInfo? _miSeGetTable;
    private object? _seTable;            // ZTable instance — retried until non-null (config tables load lazily)
    private MethodInfo? _miSeTryGetValue;
    private PropertyInfo? _piSeSkillId;  // IL2CPP fields surface as properties
    private Func<int, int>? _effectParentSkillFn;
    private Func<int, string?>? _gameSkillNameFn;

    // Hoisted delegates for SourceNameResolver (bound once, no per-call closure).
    private Func<int, int> EffectParentSkillFn => _effectParentSkillFn ??= SkillEffectParentSkill;
    private Func<int, string?> GameSkillNameFn => _gameSkillNameFn ??= id => _services.GameData.Combat.GetSkill(id)?.Name;

    /// <summary>False while the game's SkillEffectTable is not reachable yet — an "#id" resolved then must not be cached.</summary>
    private bool SkillEffectTableReady => _seTable != null && _miSeTryGetValue != null;

    /// <summary>The parent SkillId of SkillEffectTable row <paramref name="effectId"/>; 0 when not an effect row or the
    /// table is not loaded.</summary>
    private int SkillEffectParentSkill(int effectId)
    {
        // One-entry memo: SourceName asks for the icon right after SourceNameResolver asked for the name.
        if (effectId == _seLastId && _seLastParent > 0) return _seLastParent;
        int parent = LookupSkillEffectParent(effectId);
        _seLastId = effectId;
        _seLastParent = parent;
        return parent;
    }

    private int _seLastId, _seLastParent;

    private int LookupSkillEffectParent(int effectId)
    {
        if (effectId <= 0 || !EnsureSkillEffectTable()) return 0;
        try
        {
            // errorWhenNotFound = false — most ids reaching here are not effect rows; a miss must not log a game error.
            var args = new object?[] { effectId, null, false };
            var row = _miSeTryGetValue!.Invoke(_seTable, args) is true ? args[1] : null;
            if (row == null) return 0;
            _piSeSkillId ??= row.GetType().GetProperty("SkillId", BindingFlags.Public | BindingFlags.Instance);
            return _piSeSkillId?.GetValue(row) is int skillId ? skillId : 0;
        }
        catch { return 0; }
    }

    private bool EnsureSkillEffectTable()
    {
        if (!_seReflResolved)
        {
            _seReflResolved = true;
            var t = StellarInterop.FindType("Bokura.SkillEffectTableBase");
            if (t != null)
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "GetTable" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(bool))
                    { _miSeGetTable = m; break; }
            if (_miSeGetTable == null) _services.Log.Warning("[CombatMeter][name] SkillEffectTableBase.GetTable not found — sub-hit ids stay #id");
        }
        if (_miSeGetTable == null) return false;
        // AutoLoad = false: never force a synchronous table load from UI code; the game loads it for combat anyway.
        if (_seTable == null)
        {
            try { _seTable = _miSeGetTable.Invoke(null, new object[] { false }); } catch { }
            if (_seTable == null) return false;
            foreach (var m in _seTable.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == "TryGetValue" && m.GetParameters().Length == 3) { _miSeTryGetValue = m; break; }
        }
        return _miSeTryGetValue != null;
    }
}
