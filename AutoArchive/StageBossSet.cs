using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.AutoArchive;

/// <summary>
/// The SET of boss entities engaged in the current stage. Replaces the single _autoArchiveBossId latch
/// (multi-boss per battle, Spec A). Pure state, headless-testable — liveness is injected per member
/// (SetLiveness) so this class never touches the IL2CPP service surface, exactly like KilledBossTracker.
///
/// Lifecycle: Admit() opens/extends the stage's set; SetLiveness()+Aggregate() feed the engine's
/// (present,gone,dead) bools; DrainIfAllGone() closes the stage at the cut so the next stage opens a
/// fresh set; Clear() is the scene-change reset. gone = ALL members gone is the load-bearing rule that
/// keeps a two-boss stage as ONE entry (invariant 6) and makes staggered kills correct.
///
/// Alloc-free hot paths (2026-08-12 review, Amendment 1): BossStatus() runs EVERY FRAME and
/// EventInvolvesAnyStageBoss runs per COMBAT EVENT, both iterating this set. Admit/SetLiveness/Aggregate/
/// DrainIfAllGone therefore use plain `for` loops only — no LINQ, no per-call allocation. Indexed access
/// (Count + MemberAt) lets hot-path callers iterate without copying. MembersSnapshot() DOES allocate a
/// list and is for archive-time consumers (upload/replay assembly) and tests ONLY — never call it from a
/// per-frame or per-event path.
/// </summary>
internal sealed class StageBossSet
{
    internal const int MaxMembers = 8;   // runaway guard; a stage never has this many bosses

    internal struct BossLiveness
    {
        public bool Present;   // in AOI, alive
        public bool Dead;      // confirmed dead / scripted-killed this tick
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

    /// <summary>Indexed, allocation-free access for hot-path iteration (BossStatus,
    /// EventInvolvesAnyStageBoss). Valid for 0 &lt;= i &lt; Count.</summary>
    internal (EntityId id, int configId, bool killed) MemberAt(int i)
    {
        var m = _members[i];
        return (m.Id, m.ConfigId, m.Killed);
    }

    /// <summary>Allocating snapshot of the current membership. ARCHIVE-TIME-ONLY (upload/replay assembly)
    /// and tests — do not call from BossStatus/EventInvolvesAnyStageBoss or any other per-frame/per-event
    /// path. Use Count/MemberAt there instead.</summary>
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

    /// <summary>True if the stage is currently open to new members: the set is empty (new stage), or at
    /// least one existing member is present. A member already tracked (or a full set) is not re-added.</summary>
    internal bool Admit(EntityId id, int configId)
    {
        if (id.Value == 0) return false;

        var open = _members.Count == 0;
        for (var i = 0; i < _members.Count; i++)
        {
            var m = _members[i];
            if (m.Id == id) return false;   // already tracked
            if (m.Present) open = true;
        }
        if (!open || _members.Count >= MaxMembers) return false;

        _members.Add(new Member { Id = id, ConfigId = configId, Present = true, Killed = false });
        return true;
    }

    internal void SetLiveness(EntityId id, BossLiveness live)
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

    /// <summary>present = ANY member present; gone = set non-empty AND NO member present; dead = set
    /// non-empty AND ALL members killed.</summary>
    internal (bool present, bool gone, bool dead) Aggregate()
    {
        if (_members.Count == 0) return (false, false, false);

        var anyPresent = false;
        var allKilled = true;
        for (var i = 0; i < _members.Count; i++)
        {
            var m = _members[i];
            if (m.Present) anyPresent = true;
            if (!m.Killed) allKilled = false;
        }
        return (anyPresent, !anyPresent, allKilled);
    }

    internal void DrainIfAllGone()
    {
        if (_members.Count == 0) return;
        for (var i = 0; i < _members.Count; i++)
            if (_members[i].Present) return;   // at least one present → not gone
        _members.Clear();
    }

    internal void Clear() => _members.Clear();
}
