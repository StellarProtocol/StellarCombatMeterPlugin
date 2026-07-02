using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.Replay;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    // -----------------------------------------------------------------------
    // Player HP timeline helpers (split out of Plugin.Replay.cs to keep it
    // under the file-size guardrail; boss identification/upload logic stays there).
    // -----------------------------------------------------------------------

    /// <summary>
    /// HP read for the sampler: live vitals preferred, attr-cache fallback
    /// (MaxHp attr 11320, Hp attr 11310) when the vitals delta never arrived.
    /// </summary>
    private (long Hp, long MaxHp) ReadHpPair(long entityId)
    {
        var id     = new EntityId(entityId);
        var vitals = _services.CombatLookup.GetVitals(id);
        var attrs  = _services.EntityDetail.GetAttributes(id);
        var maxHp  = vitals.MaxHp > 0 ? vitals.MaxHp : (attrs.TryGetValue(11320, out var mh) ? mh : 0L);
        var hp     = vitals.IsKnown   ? vitals.Hp    : (attrs.TryGetValue(11310, out var h)  ? h  : 0L);
        return (hp, maxHp);
    }

    /// <summary>Collects per-player HP tracks (entity-id-string keyed); null when none sampled.</summary>
    private IReadOnlyDictionary<string, HpTrack>? BuildPlayerHpTracks()
    {
        if (_hpSampler is null || _replay is null) return null;
        Dictionary<string, HpTrack>? result = null;
        foreach (var id in _replay.Tracks.Keys)
        {
            if (!id.IsPlayer) continue;
            var track = _hpSampler.GetTrack(id.Value);
            if (track is null) continue;
            result ??= new Dictionary<string, HpTrack>(8);
            result[id.Value.ToString(CultureInfo.InvariantCulture)] = track;
        }
        return result;
    }
}
