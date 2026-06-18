using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>Frozen per-source time-series (one archived encounter), one array per channel.</summary>
internal struct SourceSeries
{
    public int    BucketMs;
    public long[] Dealt;
    public long[] Healing;
    public long[] Taken;
}

public sealed partial class Plugin
{
    private const int HistoryCapacity = 50;
    private readonly List<EncounterHistoryEntry> _history = new();
    private string? _lastSceneName;

    internal sealed class EncounterHistoryEntry
    {
        public string?  SceneName;
        public long     EnteredAtMs;
        public long     ArchivedAtMs;
        public long     CombatDurationMs;
        public Dictionary<EntityId, SourceStats> Stats = new();
        public Dictionary<EntityId, SourceSeries> Series = new();   // NEW
        public Dictionary<EntityId, EntitySnapshot> Entities = new();   // per-player frozen entity snapshot (issue #5)
        public PartyType PartyType;
        public int       MemberCount;
    }

    private void OnSceneChanged(string? newScene)
    {
        if (_lastSceneName is null)
        {
            _lastSceneName = newScene;
            return;
        }

        // Auto-archive on scene change. ManualArchive() is the single source of
        // truth for the snapshot-and-clear flow; the Archive button calls it too.
        ManualArchive();

        _lastSceneName = newScene;
    }

    // Snapshot the active _stats into history and reset the live meter. No-op
    // when there's nothing to archive so the button-press path doesn't push
    // empty entries. Used by:
    //   - OnSceneChanged (automatic, on scene transition)
    //   - DrawHeaderBar's Archive button (manual, user-driven)
    internal void ManualArchive()
    {
        if (_stats.Count == 0) return;

        var entry = new EncounterHistoryEntry
        {
            SceneName        = _lastSceneName,
            EnteredAtMs      = _combatStartMs,
            ArchivedAtMs     = _services.CombatSnapshot.ServerNowMs,
            CombatDurationMs = ComputeDurationMs(),
            Stats            = DeepCopyStats(),
            Series           = FreezeTimelines(),
            Entities         = SnapshotEntities(),
            PartyType        = _services.PartySnapshot.PartyType,
            // Combatant count — every entity that participated, not just party.
            // Guarded by _stats.Count == 0 early-return above, so >= 1 here.
            MemberCount      = _stats.Count,
        };
        _history.Add(entry);
        TrimToCapacity(_history);
        SaveHistory();   // persist on every archive + eviction (a user/scene event, not a hot-path frame)

        Clear();
    }

    private long ComputeDurationMs()
    {
        long earliest = long.MaxValue, latest = 0;
        foreach (var s in _stats.Values)
        {
            if (s.FirstHitMs > 0 && s.FirstHitMs < earliest) earliest = s.FirstHitMs;
            if (s.LastHitMs  > latest)                       latest   = s.LastHitMs;
        }
        return earliest == long.MaxValue ? 0 : latest - earliest;
    }

    private Dictionary<EntityId, SourceStats> DeepCopyStats()
    {
        var copy = new Dictionary<EntityId, SourceStats>(_stats.Count);
        foreach (var (id, src) in _stats)
        {
            var s2 = new SourceStats
            {
                TotalDamage  = src.TotalDamage,
                TotalHealing = src.TotalHealing,
                TotalTaken   = src.TotalTaken,
                TopHit       = src.TopHit,
                Hits         = src.Hits,
                Crits        = src.Crits,
                Luckys       = src.Luckys,
                Kills        = src.Kills,
                FirstHitMs   = src.FirstHitMs,
                LastHitMs    = src.LastHitMs,
                BySkill      = new Dictionary<int, SkillStats>(src.BySkill.Count),
                IncomingBySkill = new Dictionary<int, IncomingSkillStats>(src.IncomingBySkill.Count),
            };
            foreach (var (sid, sk) in src.BySkill)
            {
                s2.BySkill[sid] = new SkillStats
                {
                    Total     = sk.Total,
                    HealTotal = sk.HealTotal,
                    Hits      = sk.Hits,
                    Crits     = sk.Crits,
                    Luckys    = sk.Luckys,
                    TopHit    = sk.TopHit,
                };
            }
            foreach (var (sid, inc) in src.IncomingBySkill)
            {
                s2.IncomingBySkill[sid] = new IncomingSkillStats
                {
                    Total  = inc.Total,
                    Hits   = inc.Hits,
                    TopHit = inc.TopHit,
                };
            }
            copy[id] = s2;
        }
        return copy;
    }

    private Dictionary<EntityId, SourceSeries> FreezeTimelines()
    {
        var copy = new Dictionary<EntityId, SourceSeries>(_timelines.Count);
        foreach (var (id, t) in _timelines)
            copy[id] = new SourceSeries
            {
                BucketMs = t.BucketMs,
                Dealt    = t.Freeze(TimelineChannel.Dealt),
                Healing  = t.Freeze(TimelineChannel.Healing),
                Taken    = t.Freeze(TimelineChannel.Taken),
            };
        return copy;
    }

    /// <summary>
    /// Active-uptime fraction for a source: how much of the encounter the source was
    /// dealing damage (FirstHit→LastHit span over the encounter duration, clamped 0..1).
    /// </summary>
    internal static float ComputeUptime(long firstHitMs, long lastHitMs, long durationMs)
    {
        if (durationMs <= 0 || lastHitMs <= firstHitMs) return 0f;
        var span = (float)(lastHitMs - firstHitMs);
        var frac = span / durationMs;
        return frac < 0f ? 0f : frac > 1f ? 1f : frac;
    }
}
