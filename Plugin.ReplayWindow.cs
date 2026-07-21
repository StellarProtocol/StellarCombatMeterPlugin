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

    // Resolves this window's boss upload fields. The boss is "in the window" when it has EITHER
    // position samples OR non-empty sliced HP — critically the latter ALONE: the boss entity vanishes
    // on death, so the FINAL window can carry the MarkDead death-0 HP sample with no boss position
    // sample (the archive fires in the ~500 ms between the last probeable boss position and death
    // detection). Gating BossHp on position presence re-clipped the replay short of 0% — the exact
    // bug this release fixes. Returns blanks + null HP only when the boss is absent from the window
    // entirely (no HP AND no positions).
    private (string idStr, MonsterInfo? info, HpTrack? hp, bool inWindow) ResolveWindowBossFields(
        Dictionary<EntityId, PositionSample[]> windowTracks, long upperMs, int msOffset)
    {
        if (_bossEntityId.Value == 0) return ("", null, null, false);
        var (idStr, info) = ResolveBossUploadFields();
        var hp = RebaseHpTrack(SliceHpWindow(BuildBossHpTrack(), upperMs), msOffset);
        return hp is not null || windowTracks.ContainsKey(_bossEntityId)
            ? (idStr, info, hp, true)
            : ("", null, null, false);
    }

    // The window's meta entity set: entities with position samples, PLUS the boss when it is in the
    // window via HP ALONE (no position track this window) — so the site's boss name/star join still
    // resolves for a boss that vanished on death. BuildReplayMeta fills the boss row from the
    // capture-time _bossMonsterInfo snapshot (live caches are wiped by archive time).
    private ICollection<EntityId> WindowMetaIds(Dictionary<EntityId, PositionSample[]> windowTracks, bool bossInWindow)
    {
        if (!bossInWindow || windowTracks.ContainsKey(_bossEntityId)) return windowTracks.Keys;
        return new List<EntityId>(windowTracks.Keys) { _bossEntityId };
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
