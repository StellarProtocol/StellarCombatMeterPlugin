using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
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

    // Generic attr map ids consulted by tiers ③/④. 11310/11320 were declared dead by recon's L4
    // (appear/delta ALWAYS route them straight into vitals, never the generic attr map) — that holds
    // for EVERY entity via those two paths. But the LOCAL PLAYER has a THIRD write path,
    // "enter-scene-self" (EnterSceneInfo), which has no dedicated Hp/MaxHp branches and therefore
    // DOES fall through to the generic attr map for exactly this one entity (I2, full-chain review
    // finding) — reviving 11310/11320 as a local-player-only walk-in fallback, tier ④ below.
    private const int AttrHp         = 11310;
    private const int AttrMaxHpBase  = 11320;
    private const int AttrMaxHpTotal = 11321;

    /// <summary>
    /// HP read for the sampler (players, bosses, elites alike — one shared reader; see
    /// <see cref="Stellar.CombatMeter.Replay.HpTimelineSampler"/>'s doc). Four-tier source order
    /// (2026-08-26 raid-bosshp-capture-design § decision 3 + full-chain review I2), pure decision
    /// logic lives in <see cref="ResolveHpPair"/> so it pins headless without a live Plugin instance:
    /// <list type="number">
    /// <item>Native boss-blood tap (<see cref="IBossVitals.TryGetBlood"/>) — reads the SAME merged
    /// entity store the game's own boss bar renders from, immune by construction to the wire
    /// mirror's AOI-eviction starvation (recon L1).</item>
    /// <item>Wire-derived vitals, gated on <see cref="EntityVitals.HasHpObservation"/> — a MaxHp-only
    /// "alive, HP unknown" observation must never be read as 0% (recon's false-0% defect).</item>
    /// <item>The already-known wire Hp paired with the generic attr map's <see cref="AttrMaxHpTotal"/>
    /// when the vitals row has Hp but MaxHp never arrived on it (recon L4 fix).</item>
    /// <item>The generic attr map's own <see cref="AttrHp"/>, paired with
    /// <see cref="AttrMaxHpTotal"/> preferred over <see cref="AttrMaxHpBase"/> — ONLY reachable when
    /// <see cref="EntityVitals.HasHpObservation"/> is false (no vitals-row Hp at all): restores the
    /// LOCAL PLAYER's walk-in HP fallback (I2) from the enter-scene-self write path, which — unlike
    /// appear/delta — has no dedicated Hp/MaxHp branches and so DOES store them in the generic attr
    /// map for that one entity, before the first vitals delta arrives.</item>
    /// </list>
    /// Returns (0, 0) — "unusable" — when none of the four produce a full pair; the sampler records
    /// a sentinel for that tick, never a false 0%.
    /// </summary>
    private (long Hp, long MaxHp) ReadHpPair(long entityId)
    {
        var id        = new EntityId(entityId);
        var hasNative = _services.BossVitals.TryGetBlood(id, out var nativePct, out _);
        var vitals    = _services.CombatLookup.GetVitals(id);
        var attrs     = _services.EntityDetail.GetAttributes(id);
        var attrPair  = (
            Hp:         attrs.TryGetValue(AttrHp, out var h) ? h : -1L,
            MaxHpBase:  attrs.TryGetValue(AttrMaxHpBase, out var mb) ? mb : 0L,
            MaxHpTotal: attrs.TryGetValue(AttrMaxHpTotal, out var mt) ? mt : 0L);
        var (hp, maxHp, src) = ResolveHpPair(hasNative, nativePct, vitals, attrPair);
        LogBossHpTick(entityId, hp, maxHp, vitals.HasHpObservation, src);
        return (hp, maxHp);
    }

    /// <summary>Pure order-of-sources decision behind <see cref="ReadHpPair"/> — see its doc for the
    /// four tiers. <paramref name="hasNativeBlood"/>/<paramref name="nativePct"/> are the native
    /// tap's out-params pre-resolved by the caller (an interop call, not testable headless);
    /// <paramref name="attrs"/> bundles the three generic-attr-map reads (<see cref="AttrHp"/>=-1
    /// when absent, the two MaxHp ids default 0 when absent) to keep this at 4 parameters. The
    /// returned <c>Src</c> tag (<c>native</c>/<c>vitals</c>/<c>attr11321</c>/<c>attr11310</c>/
    /// <c>none</c>) feeds the recon §6 line-2 diagnostic only — callers that don't need it can
    /// discard it.</summary>
    internal static (long Hp, long MaxHp, string Src) ResolveHpPair(
        bool hasNativeBlood, int nativePct, EntityVitals vitals,
        (long Hp, long MaxHpBase, long MaxHpTotal) attrs)
    {
        // Native tap: represented as hp=pct/maxHp=100 so the sampler's percent math
        // (Math.Round(100.0 * hp / maxHp)) reproduces the native percent exactly.
        if (hasNativeBlood) return (nativePct, 100, "native");

        if (vitals.HasHpObservation)
        {
            if (vitals.MaxHp > 0) return (vitals.Hp, vitals.MaxHp, "vitals");
            if (attrs.MaxHpTotal > 0) return (vitals.Hp, attrs.MaxHpTotal, "attr11321");
            return (0, 0, "none");
        }

        // I2: local-player enter-scene-self walk-in fallback — ONLY reachable when the vitals row has
        // no Hp observation at all. 11321 preferred over 11320 when both are present.
        var attrMaxHp = attrs.MaxHpTotal > 0 ? attrs.MaxHpTotal : attrs.MaxHpBase;
        return attrs.Hp >= 0 && attrMaxHp > 0 ? (attrs.Hp, attrMaxHp, "attr11310") : (0, 0, "none");
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
