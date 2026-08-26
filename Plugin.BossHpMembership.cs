using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.Replay;

namespace Stellar.CombatMeter;

// Boss HP-track membership for the replay upload (2026-08-26 raid-bosshp-capture-design § decision
// 3/4, recon L3/item 4) — split out of Plugin.BossDetection.cs (which sits at the file-size
// guardrail) because this is a separately-testable unit: the union of the resolved stage-boss set
// with whatever the HP sampler is STILL holding samples for, plus the native-tap death signal that
// feeds MarkDead alongside the sticky `killed` flag. Core boss-candidate/kill detection stays in
// Plugin.BossDetection.cs; this file only resolves WHICH bosses the upload doc carries and whether a
// native-zero read should terminate a track.
public sealed partial class Plugin
{
    /// <summary>Spec item 4: does the native boss-blood tap report this entity at literal 0% RIGHT
    /// NOW? A raid boss's WIRE vitals can starve (recon L1 — AOI eviction) while the native tap keeps
    /// reading, so this is an independent death signal alongside the sticky <c>killed</c> flag, not a
    /// replacement for it. <c>false</c> when the tap has no current observation for the id (never
    /// conflated with "observed and alive"). The IL2CPP-adjacent interop call is isolated here;
    /// <see cref="ShouldMarkBossDead"/> is the pure OR-decision both call sites actually branch on.</summary>
    private bool IsNativeBossZero(EntityId id)
        => _services.BossVitals.TryGetBlood(id, out var pct, out _) && pct == 0;

    /// <summary>Pure decision (spec item 4): should THIS tick stamp the sampler's terminal 0% sample?
    /// True when either the existing sticky-death signal (<paramref name="stickyKilled"/> — the wire's
    /// confirmed death or the scripted-vanish inference, both resolved upstream in
    /// <c>BossStatus</c>/the scalar <c>_bossDeathMarked</c> block) OR the native tap's literal 0%
    /// read fires. <c>HpTimelineSampler.MarkDead</c> is idempotent, so re-evaluating this every tick
    /// while either input stays true is safe — pins the OR headless without the IL2CPP-adjacent
    /// <see cref="IsNativeBossZero"/> call.</summary>
    internal static bool ShouldMarkBossDead(bool stickyKilled, bool nativeZero) => stickyKilled || nativeZero;

    /// <summary>
    /// Per-member HP-track builder (multi-boss plan Task 3, extended by spec item 3): every boss the
    /// UPLOAD should carry — <see cref="ResolveCurrentStageBosses"/>'s membership UNION any
    /// sampler-tracked, monster-info-confirmed boss not already in it. The union half closes recon L3
    /// (certain): a boss admitted into <c>_stageBosses</c> (Plugin.BossDetection.cs) earlier in the
    /// run keeps accumulating samples in the sampler's <c>_entries</c> (Tick has no notion of stage
    /// membership) even after BOTH the live set drains AND <c>Clear()</c> wipes
    /// <c>_segmentStageBosses</c> (e.g. the run continues into a new stage) — without the union those
    /// already-captured samples reach no <c>bosses[]</c> entry and are silently trimmed at the next
    /// archive. Pure merge logic lives in <see cref="MergeBossMembership"/> so it pins headless; this
    /// instance wrapper supplies the three IL2CPP-adjacent inputs (sampler + <c>_replayMonsterInfo</c>,
    /// Plugin.Replay.cs) exactly like <see cref="ResolveCurrentStageBosses"/> supplies its own pure
    /// resolver's inputs. Archive/window-assembly time only (called once per <c>PrepareReplayDoc</c>
    /// via <c>BuildWindowBossMembers</c>, Plugin.ReplayWindow.cs — never per-tick).
    /// </summary>
    private IReadOnlyList<(EntityId id, int configId, HpTrack? track)> BuildBossHpTracks()
    {
        var members = MergeBossMembership(
            ResolveCurrentStageBosses(),
            _hpSampler?.TrackedIds ?? Array.Empty<long>(),
            LookupReplayMonster);
        var list = new List<(EntityId, int, HpTrack?)>(members.Count);
        foreach (var (id, configId) in members)
            list.Add((id, configId, _hpSampler?.GetTrack(id.Value)));
        return list;
    }

    /// <summary>Pure merge (spec item 3 / recon L3): the resolved stage set, UNIONED with every id in
    /// <paramref name="sampledEntityIds"/> that (a) isn't already covered, (b) isn't a player, and (c)
    /// <paramref name="lookupMonster"/> confirms is a KNOWN boss. Condition (c) is what keeps elites
    /// out — <c>lookupMonster</c> returns <c>isBoss: false</c> for an elite (MonsterType==1), and
    /// elites must never reach the boss surfaces (owner ruling 2026-08-13, CAPTURE-ONLY channel).
    /// Deterministic order: stage-set members first (unchanged from the pre-fix behavior), then the
    /// extra tracked-only members in sampler-enumeration order. Pure/static so it pins headless
    /// without a live Plugin instance — mirrors <c>PreferLiveStageBosses</c>'s extraction
    /// (Plugin.BossDetection.cs). <paramref name="lookupMonster"/> is a plain dictionary read
    /// (<c>_replayMonsterInfo</c>) — never an invented lookup; that snapshot is already populated at
    /// capture time for every non-player entity a combat event has touched (Plugin.Replay.cs's
    /// SnapshotReplayMonster).</summary>
    internal static IReadOnlyList<(EntityId id, int configId)> MergeBossMembership(
        IReadOnlyList<(EntityId Id, int ConfigId, bool Killed)> stageBosses,
        IEnumerable<long> sampledEntityIds,
        Func<EntityId, (bool isBoss, int configId)?> lookupMonster)
    {
        var seen = new HashSet<long>(stageBosses.Count);
        var list = new List<(EntityId, int)>(stageBosses.Count);
        foreach (var (id, configId, _) in stageBosses)
        {
            list.Add((id, configId));
            seen.Add(id.Value);
        }
        foreach (var entityIdValue in sampledEntityIds)
        {
            if (seen.Contains(entityIdValue)) continue;
            var id = new EntityId(entityIdValue);
            if (id.IsPlayer) continue;
            if (lookupMonster(id) is not { isBoss: true } info) continue;
            list.Add((id, info.configId));
            seen.Add(entityIdValue);
        }
        return list;
    }

    /// <summary>Capture-time monster snapshot lookup for <see cref="MergeBossMembership"/> — a plain
    /// read of <c>_replayMonsterInfo</c> (Plugin.Replay.cs), never a fresh interop call (the live
    /// caches are already wiped by archive time, same reason the boss's own MonsterInfo is
    /// snapshotted early in <c>ResolveBossEntity</c>).</summary>
    private (bool isBoss, int configId)? LookupReplayMonster(EntityId id)
        => _replayMonsterInfo.TryGetValue(id, out var info) && info.HasValue
            ? (info.Value.IsBoss, info.Value.Id)
            : null;
}
