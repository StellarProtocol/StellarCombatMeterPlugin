using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.AutoArchive;

/// <summary>
/// The SET of Elite-tier (<c>MonsterType == 1</c>) entities captured this run (ELITE CAPTURE channel,
/// owner ruling 2026-08-13, verbatim): "elites get HP + movement + identity capture SAME AS BOSSES,
/// but the auto-archive engine, cuts, verdict, bossId/bosses[], killed-boss tracker, and every config
/// toggle stay BOSS-TYPE-ONLY — the elite channel must feed NOTHING in AutoArchive/BossStatus/verdict
/// paths." This class is CAPTURE-ONLY:
/// <list type="bullet">
/// <item>NO <c>Aggregate()</c> / <c>present,gone,dead</c> tuple — nothing here ever drives
/// <c>AutoArchiveEngine</c> triggers or cuts.</item>
/// <item>NO <c>DrainIfAllGone()</c> — there is no "stage" concept for elites (unlike
/// <see cref="StageBossSet"/>, which closes/reopens per boss-phase segment). Membership simply
/// accumulates for the whole RUN and is cleared at the run/scene boundary alongside
/// <c>_stageBosses.Clear()</c> (<c>Plugin.RunBoundary.cs</c>'s <c>ResetRunScopedTrackers</c>).</item>
/// <item>NO killed-boss-tracker / re-adoption-loop guard — elites are never "cut on", so the corpse-
/// re-opens-a-segment failure mode <see cref="KilledBossTracker"/> exists to prevent cannot occur here.</item>
/// </list>
/// Mirrors <see cref="StageBossSet"/>'s shape for every OTHER consumer (Admit/SetLiveness/Count/
/// MemberAt/MembersSnapshot/Clear/Contains, MaxMembers 32, alloc-free, sticky <c>Killed</c>) so the
/// replay/HP-timeline/upload capture pipelines can treat an elite member exactly like a stage-boss
/// member without a parallel shape to learn. See <c>Plugin.EliteDetection.cs</c> for the IL2CPP glue
/// (admission via the shared <c>_bossCheck</c> cache, per-frame HP/liveness ticking) — deliberately kept
/// in its OWN sibling file, never <c>Plugin.BossDetection.cs</c>, so a future change near the protected
/// boss/engine code can never accidentally also touch elite capture (and vice versa).
/// </summary>
internal sealed class EliteSet
{
    // Mirrors StageBossSet.MaxMembers — same runaway brake + same upload-schema bound rationale
    // (a bad classification flag must never admit a whole mob pack; 32 matches the bosses[]/elites[]
    // upload schema bound).
    internal const int MaxMembers = 32;

    internal struct EliteLiveness
    {
        public bool Present;   // in AOI, alive
        public bool Dead;      // confirmed dead this tick
    }

    private sealed class Member
    {
        public EntityId Id;
        public int ConfigId;
        public bool Present;
        public bool Killed;    // sticky once observed dead
    }

    private readonly List<Member> _members = new();

    internal int Count => _members.Count;

    /// <summary>Indexed, allocation-free access for hot-path iteration. Valid for 0 &lt;= i &lt; Count.</summary>
    internal (EntityId id, int configId, bool killed) MemberAt(int i)
    {
        var m = _members[i];
        return (m.Id, m.ConfigId, m.Killed);
    }

    /// <summary>Allocating snapshot of the current membership. ARCHIVE-TIME-ONLY (upload/replay assembly)
    /// and tests — mirrors StageBossSet.MembersSnapshot's own contract.</summary>
    internal IReadOnlyList<(EntityId id, int configId, bool killed)> MembersSnapshot()
    {
        var list = new List<(EntityId id, int configId, bool killed)>(_members.Count);
        for (var i = 0; i < _members.Count; i++)
        {
            var m = _members[i];
            list.Add((m.Id, m.ConfigId, m.Killed));
        }
        return list;
    }

    /// <summary>True if <paramref name="id"/> is already a tracked member (alive or killed). Lets a
    /// hot-path caller skip re-resolving/re-admitting an already-admitted id (mirrors StageBossSet's
    /// own Contains, used by CheckEliteCandidate the same way CheckBossCandidate uses the boss one).</summary>
    internal bool Contains(EntityId id)
    {
        for (var i = 0; i < _members.Count; i++)
            if (_members[i].Id == id) return true;
        return false;
    }

    /// <summary>Member lookup returning the member's CONFIG id — the per-elite statistics bucket key
    /// (Spec B, docs/superpowers/specs/2026-08-14-per-boss-statistics-design.md §3.1). Mirrors
    /// <see cref="StageBossSet.TryGetConfigId"/> exactly, including the 0-on-miss contract
    /// (<c>TargetBucketStats.OtherKey</c>); the elite result feeds its OWN store, never a boss
    /// surface. Bounded alloc-free scan — this runs per COMBAT EVENT.</summary>
    internal bool TryGetConfigId(EntityId id, out int configId)
    {
        for (var i = 0; i < _members.Count; i++)
        {
            if (_members[i].Id != id) continue;
            configId = _members[i].ConfigId;
            return true;
        }
        configId = 0;
        return false;
    }

    /// <summary>Admits a new elite up to <see cref="MaxMembers"/>. UNLIKE <see cref="StageBossSet.Admit"/>
    /// there is no "stage open/closed" gate here — elite capture is RUN-scoped, not stage-scoped (no
    /// drain concept), so membership simply grows (de-duplicated) until the run/scene boundary clears
    /// it.</summary>
    internal bool Admit(EntityId id, int configId)
    {
        if (id.Value == 0) return false;
        if (_members.Count >= MaxMembers) return false;
        for (var i = 0; i < _members.Count; i++)
            if (_members[i].Id == id) return false;   // already tracked

        _members.Add(new Member { Id = id, ConfigId = configId, Present = true, Killed = false });
        return true;
    }

    internal void SetLiveness(EntityId id, EliteLiveness live)
    {
        for (var i = 0; i < _members.Count; i++)
        {
            var m = _members[i];
            if (m.Id != id) continue;
            m.Present = live.Present;
            if (live.Dead) m.Killed = true;
            return;
        }
    }

    internal void Clear() => _members.Clear();
}
