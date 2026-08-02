using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>
/// Pure per-profession attribute min/max accumulator (owner design 2026-08-02, base+peak snapshot
/// stats). The wire attribute sheet is buff-varying — a single end-of-fight read froze buffed values
/// (Crit 23.30% at rating 11,200 instead of the idle ~5% at ~2,400). This folds many sampled sheets
/// into a running <c>min</c> (unbuffed BASE, the headline) and <c>max</c> (combat PEAK) per attr,
/// keyed by professionId so a class switch never mixes Class A's floor with Class B's. Self-only;
/// plain data in / plain data out, so it is unit-testable without an IL2CPP/IPluginServices fake
/// (see <c>AttrRangeTrackerTests</c>). Reset at RUN start only (see <c>Plugin.AttrRange.cs</c>
/// wiring) — never per-archive.
/// </summary>
internal sealed class AttrRangeTracker
{
    // professionId -> (attrId -> [min, max]); inner value mutated in place to keep sampling low-alloc.
    private readonly Dictionary<int, Dictionary<int, long[]>> _byProfession = new();

    /// <summary>Fold one sampled sheet into <paramref name="professionId"/>'s running min/max. Skips
    /// profession 0 (unknown) and zero-valued attrs (absent ≠ 0; the sheet only carries non-zero attrs).</summary>
    public void Observe(int professionId, IReadOnlyDictionary<int, long> attrs)
    {
        if (professionId == 0 || attrs.Count == 0) return;
        if (!_byProfession.TryGetValue(professionId, out var map))
            _byProfession[professionId] = map = new Dictionary<int, long[]>();
        foreach (var kv in attrs)
        {
            if (kv.Value == 0) continue;
            if (map.TryGetValue(kv.Key, out var mm))
            {
                if (kv.Value < mm[0]) mm[0] = kv.Value;
                if (kv.Value > mm[1]) mm[1] = kv.Value;
            }
            else map[kv.Key] = new[] { kv.Value, kv.Value };
        }
    }

    public bool Has(int professionId) => _byProfession.ContainsKey(professionId);

    /// <summary>Base = running MIN of every tracked attr, as [attrId, min] pairs.</summary>
    public IReadOnlyList<long[]> Base(int professionId)
    {
        if (!_byProfession.TryGetValue(professionId, out var map)) return System.Array.Empty<long[]>();
        var list = new List<long[]>(map.Count);
        foreach (var kv in map) list.Add(new[] { (long)kv.Key, kv.Value[0] });
        return list;
    }

    /// <summary>Peak = running MAX, sparse — only attrs whose max exceeds their min (a buff moved it).</summary>
    public IReadOnlyList<long[]> Peaks(int professionId)
    {
        if (!_byProfession.TryGetValue(professionId, out var map)) return System.Array.Empty<long[]>();
        var list = new List<long[]>();
        foreach (var kv in map)
            if (kv.Value[1] > kv.Value[0]) list.Add(new[] { (long)kv.Key, kv.Value[1] });
        return list;
    }

    /// <summary>Clears every profession. Called at RUN START only.</summary>
    public void ResetForRun() => _byProfession.Clear();
}

public sealed partial class Plugin
{
    private readonly AttrRangeTracker _attrRange = new();

    // ~5 Hz throttle over the existing 10 Hz snapshot tick: flip each tick, sample on the "1" phase.
    private int _attrRangeSampleToggle;

    /// <summary>Samples the local player's live attribute sheet into the tracker for the currently
    /// active profession. Called from <see cref="TickLoadoutCapture"/> (10 Hz), throttled to ~5 Hz.
    /// Runs pre-combat + in-combat so idle reads establish the unbuffed floor. No-op out of world /
    /// profession unknown. Self-only.</summary>
    private void TickAttrRangeSample()
    {
        _attrRangeSampleToggle ^= 1;
        if (_attrRangeSampleToggle == 0) return;   // every other tick → ~5 Hz
        var prof = _services.PlayerState.Profession;
        if (prof == 0) return;
        var self = _services.CombatSnapshot.LocalEntityId;
        if (!self.IsPlayer) return;
        _attrRange.Observe(prof, _services.EntityDetail.GetAttributes(self));
    }
}
