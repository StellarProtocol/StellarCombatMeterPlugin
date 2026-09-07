using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>D10 (xDPS spec § 6.5): the local player's cooldown-acceleration ratio as ONE scalar, read off the
/// framework's per-skill <c>LocalCooldowns</c> snapshot. The server sends cooldown rows as DELTAS, so only a row whose
/// tuple changed since the last observation ("fresh") states the CURRENT ratio; a stale row states the ratio at its
/// last send. The scalar is the value every fresh row agrees on (disagreement → the max, counted). Pure.</summary>
internal sealed class CdRatioTracker
{
    /// <summary>Synthetic sheet attribute id: 9_000_000 + 11960. Plugin-derived; never a game attribute id.</summary>
    internal const int AttrId = 9011960;

    private readonly Dictionary<int, (long Begin, int Dur, int Valid, int Accel)> _seen = new();
    internal int? Last { get; private set; }
    internal int Disagreements { get; private set; }

    internal int? Observe(IReadOnlyList<SkillCooldown> rows, List<SkillCooldown>? fresh = null)
    {
        fresh?.Clear();
        int? candidate = null; var disagree = false;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var tuple = (r.BeginTimeMs, r.DurationMs, r.ValidCdTimeMs, r.AccelerateCdRatio);
            if (_seen.TryGetValue(r.SkillId, out var prev) && prev == tuple) continue;
            _seen[r.SkillId] = tuple;
            fresh?.Add(r);
            if (candidate is null) candidate = r.AccelerateCdRatio;
            else if (candidate.Value != r.AccelerateCdRatio) { disagree = true; if (r.AccelerateCdRatio > candidate.Value) candidate = r.AccelerateCdRatio; }
        }
        if (disagree) Disagreements++;
        if (candidate is null || candidate == Last) return null;
        Last = candidate;
        return candidate;
    }

    /// <summary>Scene change: forget the emitted scalar ONLY. The per-skill cache is KEPT — the framework retains
    /// cooldown rows across a scene change (<c>CombatService</c> clears <c>_localCooldowns</c> only on logout; the
    /// getter evicts merely EXPIRED rows), so if this cleared <c>_seen</c> too, every retained row would read as
    /// fresh again on the very next 10 Hz tick and <see cref="Observe"/> would re-emit the PREVIOUS scene's peak
    /// ratio (e.g. a 120s imagine row latched under a Lower CD buff) into the new segment. A scene change forgets
    /// the emitted scalar, not what rows were seen — retained rows are stale, and stale rows never speak. Only a
    /// genuinely NEW or CHANGED cooldown row of the new scene re-states the truth.</summary>
    internal void Reset()
    {
        Last = null;
    }
}
