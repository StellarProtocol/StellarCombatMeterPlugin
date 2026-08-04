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
    /// Gated to IN-RUN only (CurrentRunId != 0): the in-dungeon pre-pull reads still establish the
    /// unbuffed floor, while town samples — which <see cref="AttrRangeTracker.ResetForRun"/> clears at
    /// the next run start anyway — are skipped so we never pay the ~130-entry GetAttributes copy idling
    /// in a hub. No-op out of world / profession unknown. Self-only.</summary>
    private void TickAttrRangeSample()
    {
        _attrRangeSampleToggle ^= 1;
        if (_attrRangeSampleToggle == 0) return;   // every other tick → ~5 Hz
        if (_services.Dungeon.CurrentRunId == 0) return;   // in-run only (spec perf constraint)
        var prof = _services.PlayerState.Profession;
        if (prof == 0) return;
        var self = _services.CombatSnapshot.LocalEntityId;
        if (!self.IsPlayer) return;
        _attrRange.Observe(prof, _services.EntityDetail.GetAttributes(self));
    }

    /// <summary>Pure: writes BASE ([attrId, min]) into <paramref name="snap"/>'s AttrIds/AttrValues and
    /// sparse PEAKS ([attrId, max]) into AttrPeakIds/AttrPeakValues. Testable without services.</summary>
    internal static void WriteRangeToSnapshot(EntitySnapshot snap,
        IReadOnlyList<long[]> baseAttrs, IReadOnlyList<long[]> peaks)
    {
        snap.AttrIds    = new int[baseAttrs.Count];
        snap.AttrValues = new long[baseAttrs.Count];
        for (var i = 0; i < baseAttrs.Count; i++)
        {
            snap.AttrIds[i]    = (int)baseAttrs[i][0];
            snap.AttrValues[i] = baseAttrs[i][1];
        }
        snap.AttrPeakIds    = new int[peaks.Count];
        snap.AttrPeakValues = new long[peaks.Count];
        for (var i = 0; i < peaks.Count; i++)
        {
            snap.AttrPeakIds[i]    = (int)peaks[i][0];
            snap.AttrPeakValues[i] = peaks[i][1];
        }
    }

    /// <summary>At archive: replace the SELF actor snapshot's attrs with the run's BASE (min) + sparse
    /// PEAKS (max), and rebuild each captured loadout with its class's base/peak. The tracker is fully
    /// accumulated by archive time. No-op per profession the tracker never saw (keeps the existing
    /// single-read fallback). Self-only — non-self snapshots are untouched.
    /// NOTE: <see cref="WriteRangeToSnapshot"/> mutates the (possibly sticky) EntitySnapshot in place —
    /// safe only because SnapshotEntities transfers ownership at archive and ManualArchive Clear()s
    /// _entitySnaps immediately after (EntitySnapshot.cs). A future SnapshotEntities returning a
    /// shared/cached snap must copy-on-write here.</summary>
    private void ApplyAttrRanges(EncounterHistoryEntry entry)
    {
        var self = _services.CombatSnapshot.LocalEntityId;
        var prof = _services.PlayerState.Profession;
        if (prof != 0 && _attrRange.Has(prof) && entry.Entities.TryGetValue(self, out var snap))
            WriteRangeToSnapshot(snap, _attrRange.Base(prof), _attrRange.Peaks(prof));

        if (entry.Loadouts.Count == 0) return;
        var resolved = new List<CapturedLoadout>(entry.Loadouts.Count);
        foreach (var l in entry.Loadouts)
        {
            var withAttrs = _attrRange.Has(l.ProfessionId)
                ? l with { Attributes = _attrRange.Base(l.ProfessionId), AttrPeaks = _attrRange.Peaks(l.ProfessionId) }
                : l;
            // Fill each played class's gear/modules from its LoadoutSlot (saved-loadout base + live overlay
            // for the current class) — the actual per-class gear/modules.
            resolved.Add(ApplyPerClassGear(withAttrs));
        }
        entry.Loadouts = resolved;
    }
}
