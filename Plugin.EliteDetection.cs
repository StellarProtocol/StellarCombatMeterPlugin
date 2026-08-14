using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.Replay;

namespace Stellar.CombatMeter;

// ELITE CAPTURE channel (owner ruling 2026-08-13, verbatim): "elites (MonsterType==1) get HP + movement
// + identity capture SAME AS BOSSES, but the auto-archive engine, cuts, verdict, bossId/bosses[],
// killed-boss tracker, and every config toggle stay BOSS-TYPE-ONLY — the elite channel must feed NOTHING
// in AutoArchive/BossStatus/verdict paths." Kept in its OWN sibling file, deliberately separate from
// Plugin.BossDetection.cs, so a future change near the protected boss/engine code
// (docs/recon/combatmeter-archive-flow.md) can never accidentally also touch elite capture, and vice
// versa. See AutoArchive/EliteSet.cs for the pure-state shape this file wires into the IL2CPP service
// surface (mirrors Plugin.BossDetection.cs's own StageBossSet glue).
//
// Movement capture needs NO code here at all: NoteReplayEntity (Plugin.Replay.cs) already position-
// tracks EVERY entity that ever appears as a combat src/tgt, boss or elite or plain trash, unconditionally
// — position capture was never boss-specific. What elites were missing is (1) CLASSIFICATION — knowing
// WHICH tracked entity is an elite at all, and (2) an HP TIMELINE — only bosses/players fed the sampler
// before this channel existed. Both are what this file adds.
public sealed partial class Plugin
{
    // Elite identity set — CAPTURE ONLY (see EliteSet's own doc; NO Aggregate()/DrainIfAllGone()
    // consumer exists). RUN-scoped, not stage-scoped: unlike _stageBosses there is no "stage" to
    // open/close, so membership simply accumulates for the whole run. Reset alongside _stageBosses at
    // the run/scene boundary (ResetRunScopedTrackers, Plugin.RunBoundary.cs) — never by Clear().
    private readonly AutoArchive.EliteSet _eliteSet = new();

    // Sticky latch mirroring _segmentStageBosses (Plugin.BossDetection.cs) — needed for the EXACT same
    // ordering reason: OnSceneChanged's ResetRunScopedTrackers clears the LIVE _eliteSet BEFORE the
    // scene archive's own BuildHistoryEntry runs (via the later BankRunBoundary call), so without this
    // latch a scene-triggered archive would always see an already-emptied set. Latched immediately
    // before that clear (LatchElites(), called from ResetRunScopedTrackers) — there is no BossStatus-
    // equivalent per-frame drain for elites (no stage concept), so that single call site is the ONLY
    // latch point needed (unlike _segmentStageBosses, which also latches at the stage-drain moment).
    private IReadOnlyList<(EntityId Id, int ConfigId, bool Killed)> _segmentElites =
        Array.Empty<(EntityId Id, int ConfigId, bool Killed)>();

    private void LatchElites()
    {
        if (_eliteSet.Count > 0) _segmentElites = _eliteSet.MembersSnapshot();
    }

    /// <summary>Archive-time resolver: the LIVE elite set when it still has members, else
    /// <see cref="_segmentElites"/>. Consumed by BuildHistoryEntry (Plugin.History.cs) and
    /// BuildEliteHpTracks below. Reuses Plugin.BossDetection.cs's pure
    /// <c>PreferLiveStageBosses(live, latched)</c> helper — same tuple shape, same "prefer live, fall
    /// back to the sticky latch" rule, no need for a duplicate.</summary>
    private IReadOnlyList<(EntityId Id, int ConfigId, bool Killed)> ResolveCurrentElites()
        => PreferLiveStageBosses(_eliteSet.MembersSnapshot(), _segmentElites);

    // Called from ObserveAlwaysOnCapture (Plugin.CaptureAlwaysOn.cs) alongside NoteReplayEntity — the
    // always-on capture half of OnCombatEvent, so this is PAUSE-INDEPENDENT too since 2026-08-14 (it
    // used to sit past OnCombatEvent's `if (_paused) return;`). TOGGLE-INDEPENDENT as well:
    // no _autoArchive.BossEnabled gate. Elite admission is deliberately wired from its OWN, always-
    // reached call site rather than piggy-backing on the boss path — turning Boss phase off must never
    // blind elite capture, and keeping the call sites separate is what guarantees that independently of
    // however the boss path is gated. (Historical note: when this channel landed, 2026-08-13, the boss
    // path's ObserveAutoArchiveBoss WAS gated on BossEnabled, which made a separate call site strictly
    // necessary; the owner ruling 2026-08-14 has since made boss admission always-on too, so the two
    // channels now share the same gating — the separation stands on isolation grounds alone, per
    // agent-process-rules § 35.) Gated only on being in an instanced run (inRun gate — no open-world
    // elite capture), mirroring NoteReplayEntity's own gate.
    private void ObserveEliteCandidates(EntityId src, EntityId tgt)
    {
        if (!IsInstancedRun()) return;
        CheckEliteCandidate(src);
        CheckEliteCandidate(tgt);
    }

    private void CheckEliteCandidate(EntityId id)
    {
        if (id.IsPlayer || id.Value == 0) return;
        // Mirrors CheckBossCandidate's Contains fast-path: an already-admitted member skips the cache
        // lookup AND the interop call entirely (cheap Count/MemberAt-bounded scan, no allocation).
        if (_eliteSet.Contains(id)) return;
        var cached = ResolveMonsterCandidate(id);   // shared with CheckBossCandidate (Plugin.BossDetection.cs)
        if (cached is null || !cached.Value.IsElite) return;
        _eliteSet.Admit(id, cached.Value.ConfigId);
    }

    // Per-frame elite liveness + HP sampling — mirrors TickStageBossHpTracks/BossStatus's vitals-sampling
    // half MINUS the auto-archive-engine aggregate/killed-boss-tracker/drain machinery (none of that
    // applies to a capture-only channel with no stage concept, and no scripted-kill/raid inference either
    // — that heuristic exists for raid BOSSES specifically; an elite's HP reads plain 0 on death). Called
    // from TickHpTimelines (Plugin.Replay.cs) at the SAME per-frame cadence as TickStageBossHpTracks —
    // gated only on replay capture being active (never on _autoArchive.BossEnabled), so elite HP/kill
    // capture runs whenever boss HP capture does. Alloc-free: plain indexed `for` over Count/MemberAt.
    private void TickEliteHpTracks(HpTimelineSampler sampler, long combatStartMs, int nowMs)
    {
        for (var i = 0; i < _eliteSet.Count; i++)
        {
            var (id, _, killed) = _eliteSet.MemberAt(i);
            if (!killed)
            {
                var v = _services.CombatLookup.GetVitals(id);
                if (v.HasHpObservation && v.MaxHp > 0 && v.Hp <= 0)
                {
                    _eliteSet.SetLiveness(id, new AutoArchive.EliteSet.EliteLiveness { Present = false, Dead = true });
                    killed = true;
                }
            }
            sampler.Track(id.Value, nowMs - combatStartMs);
            if (killed) sampler.MarkDead(id.Value, nowMs - combatStartMs);
        }
    }

    /// <summary>Per-member HP-track builder — mirrors BuildBossHpTracks (Plugin.Replay.cs) exactly, but
    /// sourced from ResolveCurrentElites(). Consumed by BuildWindowEliteMembers (Plugin.ReplayWindow.cs).
    /// Archive/window-assembly time only (allocates), never per-tick.</summary>
    private IReadOnlyList<(EntityId id, int configId, HpTrack? track)> BuildEliteHpTracks()
    {
        var members = ResolveCurrentElites();
        var list = new List<(EntityId, int, HpTrack?)>(members.Count);
        for (var i = 0; i < members.Count; i++)
        {
            var (id, configId, _) = members[i];
            list.Add((id, configId, _hpSampler?.GetTrack(id.Value)));
        }
        return list;
    }
}
