using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.Replay;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    // -----------------------------------------------------------------------
    // Player HP timeline helpers (split out of Plugin.Replay.cs to keep it
    // under the file-size guardrail; boss identification/upload logic stays there).
    // -----------------------------------------------------------------------

    // Worker positions schema caps `playerHp` at maxProperties: 32 (see
    // services/stellar-logs worker positions route) — an upload with more
    // player tracks is rejected WHOLE, losing the entire replay. Stop adding
    // entries once this many are collected.
    private const int MaxPlayerHpTracks = 32;

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

    // (Per-player HP collection now happens window-scoped in Plugin.Replay.cs's SlicePlayerHpWindow,
    // which slices each track to (watermark, now] before upload — see the delta-window design.)

    // Shift a single HP track's Ms0 by the same capture->combat-start offset applied to the
    // position tracks (see PrepareReplayDoc), so boss HP stays synced with the replay timeline.
    private static HpTrack? RebaseHpTrack(HpTrack? track, int msOffset)
        => track is null ? null : track with { Ms0 = track.Ms0 + msOffset };

    // Shift every player HP track's Ms0 by the same offset (see RebaseHpTrack).
    private static IReadOnlyDictionary<string, HpTrack>? RebasePlayerHpTracks(
        IReadOnlyDictionary<string, HpTrack>? tracks, int msOffset)
    {
        if (tracks is null || msOffset == 0) return tracks;
        var result = new Dictionary<string, HpTrack>(tracks.Count);
        foreach (var (id, track) in tracks) result[id] = track with { Ms0 = track.Ms0 + msOffset };
        return result;
    }
}
