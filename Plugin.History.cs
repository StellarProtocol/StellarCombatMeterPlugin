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

/// <summary>A single killing-blow record: when (epoch ms), who died, and the attacker skill id.</summary>
internal readonly record struct DeathEntry(long Ms, EntityId Victim, int Skill);

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
        public List<DeathEntry> DeathLog = new();   // NEW: complete killing-blow list (truncation-independent)
        public Dictionary<EntityId, EntitySnapshot> Entities = new();   // per-player frozen entity snapshot (issue #5)
        public PartyType PartyType;
        public int       MemberCount;
        public long      LevelUuid;        // snapshotted at archive (IDungeonState.CurrentRunId) for deferred upload
        public int       PassTime;         // settlement clear-time seconds at archive
        public int       MasterModeScore;  // settlement master-mode score at archive
        public string    Result = "partial"; // "kill" once settled, else "partial"
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

        // Snapshot run-identity from the live dungeon state AT ARCHIVE so a deferred
        // (manual) upload of this entry uses the right levelUuid / settlement — not
        // whatever run happens to be live when the user later clicks Upload.
        var settlement = _services.Dungeon.LastSettlement;
        var entry = new EncounterHistoryEntry
        {
            SceneName        = _lastSceneName,
            EnteredAtMs      = _combatStartMs,
            ArchivedAtMs     = _services.CombatSnapshot.ServerNowMs,
            CombatDurationMs = ComputeDurationMs(),
            Stats            = DeepCopyStats(),
            Series           = FreezeTimelines(),
            DeathLog         = new List<DeathEntry>(_deaths),
            Entities         = SnapshotEntities(),
            PartyType        = _services.PartySnapshot.PartyType,
            // Combatant count — every entity that participated, not just party.
            // Guarded by _stats.Count == 0 early-return above, so >= 1 here.
            MemberCount      = _stats.Count,
            LevelUuid        = _services.Dungeon.CurrentRunId != 0 ? _services.Dungeon.CurrentRunId : _lastRunId,
            PassTime         = settlement?.PassTimeSeconds ?? 0,
            MasterModeScore  = settlement?.MasterModeScore ?? 0,
            Result           = settlement is not null ? "kill" : "partial",
        };
        _history.Add(entry);
        foreach (var evicted in TrimToCapacity(_history)) _uploadStatus.Forget(evicted);   // unroot evicted runs
        SaveHistory();   // persist on every archive + eviction (a user/scene event, not a hot-path frame)

        // SP1: fire-and-forget upload of the full event log (opt-in; never blocks/crashes).
        MaybeUploadLog(entry);

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
                Deaths       = src.Deaths,
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
