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

    /// <summary>
    /// P0 walk-in-anchor fix (2026-08-26, owner ground-truth run qCUzbYtTmI): the replay window's
    /// DECLARED <c>[StartMs, EndMs]</c> bounds — what the uploaded doc CLAIMS to cover — as ABSOLUTE
    /// server-clock timestamps, the SAME scale <c>entry.EnteredAtMs</c>/<c>ArchivedAtMs</c> (and
    /// therefore <c>encounter.StartMs</c>/<c>EndMs</c>, <c>CombatLogAssembler.BuildEncounter</c>)
    /// already use — <b>NOT</b> the small, per-window, combat-start-ZEROED relative numbers
    /// individual track/HP samples carry as their own <c>Ms0</c> (those intentionally reset to a
    /// DIFFERENT reference every window, via <c>msOffset</c>, so they cannot double as a cross-window
    /// stitching anchor — a site ordering/placing several windows on one real-world session timeline
    /// needs an ABSOLUTE, monotonically-comparable value, exactly like <c>EnteredAtMs</c>/
    /// <c>ArchivedAtMs</c> already provide for every OTHER archived entry).
    ///
    /// Before this fix, <c>PrepareReplayDoc</c> set these two fields from
    /// <c>encounter.StartMs</c>/<c>encounter.EndMs</c> — the DPS/damage-log's own COMBAT-ONLY span
    /// (<c>entry.EnteredAtMs</c>/<c>ArchivedAtMs</c>) — a field-level bug present in this file's
    /// ENTIRE git history back to the walk-in-capture arc's original commit (44ee9c8, 2026-07-01),
    /// verified via <c>git log -L</c>, so it predates every commit on this branch AND the released
    /// 2.5.0 build. A site that trusts <c>StartMs</c>/<c>EndMs</c> as the window's valid range (a
    /// completely reasonable reading of those field names) would clip/hide any track sample whose
    /// reconstructed absolute time falls outside them — and every pre-combat sample's true time does,
    /// since the window genuinely starts BEFORE combat (the dungeon-entry walk-in, or a post-teleport
    /// arrival). The owner's "the replay renders the wrong spawn" / "post-teleport arrival + heal-up
    /// movement unclaimed" symptoms are both explained by this: the doc always claimed only its own
    /// combat-only sub-span, never the true watermark-to-cut window.
    ///
    /// <para>Window 1 (<paramref name="watermarkMs"/> == <see cref="ReplayWatermarkUnset"/>, i.e.
    /// nothing has been banked yet this run) claims from <paramref name="captureStartMs"/> itself —
    /// the dungeon-entry walk-in's own absolute anchor. Window N (a real watermark from the PREVIOUS
    /// archive's own cut, in capture-relative ms) claims from THAT cut, converted back to absolute via
    /// <c>captureStartMs + watermarkMs</c>. <see cref="EndMs"/> is always <c>captureStartMs +
    /// upperMs</c> (THIS archive's own cut) — never <c>encounter.EndMs</c>, for the identical reason
    /// (though the two are numerically close in practice, since both represent "now" at archive time
    /// read a tick apart; this one is exact for what the tracks actually cover). An empty load segment
    /// (zero position samples during the actual loading screen) still gets CLAIMED by the surrounding
    /// window — this function never inspects the samples, only the window boundaries, so a sparse
    /// window's declared span is never narrowed down to "just where samples happen to exist".</para>
    /// </summary>
    internal static (long StartMs, long EndMs) ResolveWindowBounds(int captureStartMs, long watermarkMs, long upperMs)
    {
        var lowerCaptureRelativeMs = watermarkMs < 0 ? 0 : watermarkMs;
        return (captureStartMs + lowerCaptureRelativeMs, captureStartMs + upperMs);
    }

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
    //
    // M2 (2026-08-26 full-chain review): an ALL-SENTINEL slice (every sample a L2 gap, no real data)
    // is treated the SAME as no HP data — nulled before the "in window" test — so a boss whose only
    // HP entry this window is an unbroken run of gaps doesn't get admitted with a data-less HP chip;
    // it still rides the window on its position samples alone, same as any other no-HP-data boss.
    private (EntityId id, string idStr, MonsterInfo? info, HpTrack? hp, bool inWindow) ResolveWindowBossFields(
        Dictionary<EntityId, PositionSample[]> windowTracks, long upperMs, int msOffset)
    {
        var (repId, idStr, info) = ResolveBossRepresentative();
        if (repId.Value == 0) return (default, "", null, null, false);
        var slicedHp = RebaseHpTrack(SliceHpWindow(_hpSampler?.GetTrack(repId.Value), upperMs), msOffset);
        var hp = slicedHp is not null && ReplayWindow.IsAllSentinel(slicedHp) ? null : slicedHp;
        return hp is not null || windowTracks.ContainsKey(repId)
            ? (repId, idStr, info, hp, true)
            : (default, "", null, null, false);
    }

    // Multi-boss (Task 4): every boss member's id/configId/HP, sliced+rebased to THIS window — the
    // source for both the additive Bosses[] array and the meta-id union below. Reuses BuildBossHpTracks()
    // (Plugin.BossHpMembership.cs — moved there by spec item 3/recon L3, which extended its membership
    // to the sampler-tracked ∪ stage-set union) as the per-member source and mirrors ResolveWindowBossFields's
    // own per-member inWindow rule (sliced HP present OR a position track this window) — a boss that
    // vanished on death still rides the array on its death-0 HP sample alone, same as the scalar. Returns
    // null when the merged membership is empty or every member is absent from this window.
    private List<(EntityId id, int configId, HpTrack? hp)>? BuildWindowBossMembers(
        Dictionary<EntityId, PositionSample[]> windowTracks, long upperMs, int msOffset)
    {
        var members = BuildBossHpTracks();
        if (members.Count == 0) return null;
        List<(EntityId, int, HpTrack?)>? list = null;
        foreach (var (id, configId, track) in members)
        {
            var slicedHp = RebaseHpTrack(SliceHpWindow(track, upperMs), msOffset);
            // M2: an all-sentinel slice ("all gaps, no real data") is nulled before the doc-membership
            // test — see ResolveWindowBossFields's doc for the full rationale. The diagnostic below
            // still logs the RAW sliced result (pre-nulling) so a field read shows the true slice size
            // even when it was discarded for being data-less.
            var hp = slicedHp is not null && ReplayWindow.IsAllSentinel(slicedHp) ? null : slicedHp;
            var inDoc = hp is not null || windowTracks.ContainsKey(id);
            // recon §6 line 6 — per-boss sample accounting at archive time (settles L2 grid drift +
            // L3 captured-vs-uploaded in the field). Diagnostics-gated; see Plugin.Diagnostics.cs.
            LogBossHpArchive(id, track, slicedHp, upperMs, inDoc);
            if (!inDoc) continue;   // absent from this window entirely
            (list ??= new List<(EntityId, int, HpTrack?)>(members.Count)).Add((id, configId, hp));
        }
        return list;
    }

    // ELITE CAPTURE channel (owner ruling 2026-08-13): every elite (MonsterType==1) tracked this window,
    // sliced+rebased — mirrors BuildWindowBossMembers exactly, sourced from BuildEliteHpTracks/
    // ResolveCurrentElites (Plugin.EliteDetection.cs) instead of the stage-boss set. CAPTURE ONLY: feeds
    // PositionUploadDoc.Elites and nothing else — no meta-id union, no scalar representative (see
    // PositionUploadDoc.Elites' own doc for the full boundary).
    //
    // Minor 1 (2026-08-26 full-chain re-review): mirrors BuildWindowBossMembers's M2 fix — an
    // ALL-SENTINEL slice (every sample a L2 gap, no real data) is nulled before the "in window" test,
    // the same "no HP data" handling the boss sites already got, so an elite doesn't get admitted
    // with a data-less HP chip either.
    private List<(EntityId id, int configId, HpTrack? hp)>? BuildWindowEliteMembers(
        Dictionary<EntityId, PositionSample[]> windowTracks, long upperMs, int msOffset)
    {
        var members = BuildEliteHpTracks();
        if (members.Count == 0) return null;
        List<(EntityId, int, HpTrack?)>? list = null;
        foreach (var (id, configId, track) in members)
        {
            var slicedHp = RebaseHpTrack(SliceHpWindow(track, upperMs), msOffset);
            var hp = slicedHp is not null && ReplayWindow.IsAllSentinel(slicedHp) ? null : slicedHp;
            if (hp is null && !windowTracks.ContainsKey(id)) continue;   // absent from this window entirely
            (list ??= new List<(EntityId, int, HpTrack?)>(members.Count)).Add((id, configId, hp));
        }
        return list;
    }

    // Converts BuildWindowBossMembers's (or BuildWindowEliteMembers's — same tuple shape) window-filtered
    // tuples to the Replay.BossTrackDto records carried on PositionUploadDoc.Bosses/Elites. Archive-time-
    // only allocation (amendment 1) — never called per-tick.
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
