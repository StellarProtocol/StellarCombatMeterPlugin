using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.CombatMeter.Replay;

namespace Stellar.CombatMeter;

// Delta-window mechanics (owner design 2026-07-19), split out of Plugin.Replay.cs to keep it under
// the file-size guardrail: the slicing that turns the never-reset per-run capture into the window
// (watermark, now] PrepareReplayDoc uploads, plus the watermark advance that frees the samples it
// consumed. The pure slice math lives in Replay/ReplayWindow.cs (unit-tested); these are the thin
// Plugin-side adapters over the live capture buffers.
public sealed partial class Plugin
{
    // Sentinel for "no upper-bound cap on the replay window" — passed for every non-boss archive
    // (Task 7). The inline boss-phase trash cut (Plugin.AutoArchive.cs MaybeCutForBossPhase) passes a
    // real server-clock cap so its window ends at (firstBossHit − keepBefore) and the run-up movement
    // flows into the following boss window instead of the trash one (boundary moves earlier; windows
    // stay contiguous → concatenation unbroken). Consumed in PrepareReplayDoc via ReplayWindow.CapUpper.
    internal const long ReplayUpperCapUnset = long.MaxValue;

    // Slices the position buffer to the window (lowerExclusive, upperInclusive]; keeps only entities
    // with at least one sample in the window (they define the window's meta set). Pure slicing via
    // ReplayWindow; the source buffers are not mutated here (freeing happens in AdvanceReplayWatermark).
    private Dictionary<EntityId, PositionSample[]> SliceWindowPositions(long lowerExclusive, long upperInclusive)
    {
        var result = new Dictionary<EntityId, PositionSample[]>(_replay!.Tracks.Count);
        foreach (var id in _replay.Tracks.Keys)
        {
            var slice = ReplayWindow.SlicePositions(_replay.Tracks[id].Snapshot(), lowerExclusive, upperInclusive);
            if (slice.Length > 0) result[id] = slice;
        }
        return result;
    }

    // Slices one HP track to (watermark, upperMs] at the shared 500 ms cadence (see ReplayWindow.SliceHp).
    private HpTrack? SliceHpWindow(HpTrack? track, long upperMs)
        => track is null ? null : ReplayWindow.SliceHp(track, _replayWatermarkMs, upperMs, ReplaySampleIntervalMs);

    // Slices every player HP track to the window; drops players with no sample in it (SliceHp → null).
    private IReadOnlyDictionary<string, HpTrack>? SlicePlayerHpWindow(long upperMs)
    {
        if (_hpSampler is null || _replay is null) return null;
        Dictionary<string, HpTrack>? result = null;
        foreach (var id in _replay.Tracks.Keys)
        {
            if (!id.IsPlayer) continue;
            var slice = SliceHpWindow(_hpSampler.GetTrack(id.Value), upperMs);
            if (slice is null) continue;
            result ??= new Dictionary<string, HpTrack>(8);
            result[id.Value.ToString(CultureInfo.InvariantCulture)] = slice;
            if (result.Count == MaxPlayerHpTracks) break;
        }
        return result;
    }

    // Multi-boss (2026-08-12 review, Task 4): the scalar BossEntityId/BossHp representative that old
    // readers still consume. First-admitted stage-set member when the set is non-empty (deterministic —
    // amendment 4, no plugin-side raid-roster mirror), else today's single-latch behavior
    // (_bossEntityId/_bossMonsterInfo, resolved by ResolveBossEntity's highest-MaxHp pick in
    // Plugin.Replay.cs) for a config where boss-phase detection — which is what populates the set — is
    // off but the standalone boss-HP feature still resolved one. A stage-set member's MonsterInfo comes
    // from _replayMonsterInfo (Plugin.Replay.cs): SnapshotReplayMonster runs off the SAME OnCombatEvent
    // src/tgt that admits the member into the set, so it is already populated by archive time.
    private (EntityId id, string idStr, MonsterInfo? info) ResolveBossRepresentative()
    {
        if (_stageBosses.Count > 0)
        {
            var (id, _, _) = _stageBosses.MemberAt(0);
            _replayMonsterInfo.TryGetValue(id, out var info);
            return (id, id.Value.ToString(CultureInfo.InvariantCulture), info);
        }
        var (idStr, legacyInfo) = ResolveBossUploadFields();
        return (_bossEntityId, idStr, legacyInfo);
    }

    // Resolves this window's boss upload fields. The boss is "in the window" when it has EITHER
    // position samples OR non-empty sliced HP — critically the latter ALONE: the boss entity vanishes
    // on death, so the FINAL window can carry the MarkDead death-0 HP sample with no boss position
    // sample (the archive fires in the ~500 ms between the last probeable boss position and death
    // detection). Gating BossHp on position presence re-clipped the replay short of 0% — the exact
    // bug this release fixes. Returns blanks + null HP only when the boss is absent from the window
    // entirely (no HP AND no positions).
    private (EntityId id, string idStr, MonsterInfo? info, HpTrack? hp, bool inWindow) ResolveWindowBossFields(
        Dictionary<EntityId, PositionSample[]> windowTracks, long upperMs, int msOffset)
    {
        var (repId, idStr, info) = ResolveBossRepresentative();
        if (repId.Value == 0) return (default, "", null, null, false);
        var hp = RebaseHpTrack(SliceHpWindow(_hpSampler?.GetTrack(repId.Value), upperMs), msOffset);
        return hp is not null || windowTracks.ContainsKey(repId)
            ? (repId, idStr, info, hp, true)
            : (default, "", null, null, false);
    }

    // Multi-boss (Task 4): every stage-set boss's id/configId/HP, sliced+rebased to THIS window — the
    // source for both the additive Bosses[] array and the meta-id union below. Reuses BuildBossHpTracks()
    // (Task 3, Plugin.Replay.cs) as the per-member source and mirrors ResolveWindowBossFields's own
    // per-member inWindow rule (sliced HP present OR a position track this window) — a boss that
    // vanished on death still rides the array on its death-0 HP sample alone, same as the scalar. Returns
    // null when the stage set is empty (today's non-boss-phase configs) or every member is absent from
    // this window.
    private List<(EntityId id, int configId, HpTrack? hp)>? BuildWindowBossMembers(
        Dictionary<EntityId, PositionSample[]> windowTracks, long upperMs, int msOffset)
    {
        var members = BuildBossHpTracks();
        if (members.Count == 0) return null;
        List<(EntityId, int, HpTrack?)>? list = null;
        foreach (var (id, configId, track) in members)
        {
            var hp = RebaseHpTrack(SliceHpWindow(track, upperMs), msOffset);
            if (hp is null && !windowTracks.ContainsKey(id)) continue;   // absent from this window entirely
            (list ??= new List<(EntityId, int, HpTrack?)>(members.Count)).Add((id, configId, hp));
        }
        return list;
    }

    // Converts BuildWindowBossMembers's window-filtered tuples to the Replay.BossTrackDto records carried
    // on PositionUploadDoc.Bosses. Archive-time-only allocation (amendment 1) — never called per-tick.
    private static List<BossTrackDto>? ToBossTrackDtos(List<(EntityId id, int configId, HpTrack? hp)>? members)
    {
        if (members is null) return null;
        var list = new List<BossTrackDto>(members.Count);
        foreach (var (id, configId, hp) in members)
            list.Add(new BossTrackDto(id.Value.ToString(CultureInfo.InvariantCulture), configId, hp));
        return list;
    }

    // The window's meta entity set: entities with position samples, PLUS every boss (representative AND
    // every other stage member) that is in the window via HP ALONE (no position track this window) — so
    // the site's boss name/star join still resolves for a boss that vanished on death, for EVERY boss,
    // not just the representative (multi-boss plan Task 4). When windowBosses is non-null/non-empty it
    // already covers the representative too (same per-member inWindow test as ResolveWindowBossFields),
    // so the legacy repBossId/repInWindow branch only fires for the single-boss fallback (stage set
    // empty) — byte-identical to the pre-Task-4 behavior in that case.
    private ICollection<EntityId> WindowMetaIds(
        Dictionary<EntityId, PositionSample[]> windowTracks, EntityId repBossId, bool repInWindow,
        List<(EntityId id, int configId, HpTrack? hp)>? windowBosses)
    {
        if (windowBosses is { Count: > 0 })
        {
            List<EntityId>? extra = null;
            foreach (var (id, _, _) in windowBosses)
            {
                if (windowTracks.ContainsKey(id)) continue;
                (extra ??= new List<EntityId>(windowBosses.Count)).Add(id);
            }
            if (extra is null) return windowTracks.Keys;
            var result = new List<EntityId>(windowTracks.Keys);
            result.AddRange(extra);
            return result;
        }

        if (!repInWindow || windowTracks.ContainsKey(repBossId)) return windowTracks.Keys;
        return new List<EntityId>(windowTracks.Keys) { repBossId };
    }

    // Advances the watermark to the window PrepareReplayDoc just serialized and FREES the consumed
    // samples (positions + HP ≤ watermark). Called ONLY after the upload was handed off to the queue
    // (owner default 2) — a failed/skipped hand-off leaves the watermark put, so the samples re-window
    // next time. Bounds retained memory to the un-uploaded tail; MaxSamplesPerEntity stays the cap.
    private void AdvanceReplayWatermark()
    {
        _replayWatermarkMs = _replayWindowUpperMs;
        _replay?.TrimBelow(_replayWatermarkMs);
        _hpSampler?.TrimBelow(_replayWatermarkMs, ReplaySampleIntervalMs);
    }
}
