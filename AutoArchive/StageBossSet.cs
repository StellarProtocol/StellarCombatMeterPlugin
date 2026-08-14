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
    // Runaway brake, NOT a fight-size assumption — this exists to stop a bad boss-detection flag
    // from admitting a whole mob pack, not to cap how many real bosses a stage can hold. 8 was wrong:
    // a live dungeon (Foggy Sea Shadows, 2026-08-13) spawned 5-10 simultaneous bosses and got clipped.
    // 32 matches the upload schema's bosses[] maxItems bound (services/stellar-logs
    // schema/combat-log.v1.schema.json) so the brake never fires before the server-side limit would.
    internal const int MaxMembers = 32;

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

    /// <summary>True if <paramref name="id"/> is already a tracked member (alive or killed). Alloc-free
    /// linear scan (MaxMembers-bounded) — mirrors Admit's own dupe-check loop below. Lets a hot-path
    /// caller skip re-resolving/re-admitting an already-admitted id without paying for the interop call
    /// behind it (final review, Important 3 — Plugin.BossDetection.cs's CheckBossCandidate).</summary>
    internal bool Contains(EntityId id)
    {
        for (var i = 0; i < _members.Count; i++)
            if (_members[i].Id == id) return true;
        return false;
    }

    /// <summary>Member lookup returning the member's CONFIG id — the per-boss statistics bucket key
    /// (Spec B, docs/superpowers/specs/2026-08-14-per-boss-statistics-design.md §3.1). Same bounded,
    /// alloc-free scan as <see cref="Contains"/> (this runs per COMBAT EVENT — no dictionary, no LINQ).
    /// <paramref name="configId"/> is 0 on a miss, which is <c>TargetBucketStats.OtherKey</c>, so an
    /// unrouted target lands in Other rather than being dropped (no-loss invariant §7.2). A KILLED
    /// member still resolves until the stage drains, keeping the post-kill DoT tail on its boss.</summary>
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

    /// <summary>Sticky-mark a member Killed WITHOUT touching its <c>Present</c> flag — the
    /// observation-pass counterpart to <see cref="SetLiveness"/> (which owns <c>Present</c>, the engine's
    /// aggregate input). Plugin.ObserveBossKillState calls this on a REAL <c>Hp&lt;=0</c> read seen through
    /// a settle window / pause, where the engine-facing <see cref="SetLiveness"/> path is suppressed: the
    /// kill fact must be captured the instant it is observed, but <c>Present</c> stays the engine's to set
    /// on the resume tick. No-op for an unknown id. Idempotent — <c>Killed</c> is sticky.</summary>
    internal void MarkKilled(EntityId id)
    {
        for (var i = 0; i < _members.Count; i++)
        {
            if (_members[i].Id != id) continue;
            _members[i].Killed = true;
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
