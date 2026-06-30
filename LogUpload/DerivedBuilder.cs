// UNVERIFIED — this code has never been executed in-game.
// SP1 Part B: builds the unsigned `derived` aggregate block from the plugin's UNCAPPED
// per-actor stats + per-second series + complete death list. These numbers are authoritative
// (the event ring is only a forensic detail track), so the web run page renders from here.

using System.Collections.Generic;
using System.Globalization;

namespace Stellar.CombatMeter.LogUpload;

internal static class DerivedBuilder
{
    internal static Derived Build(Plugin.EncounterHistoryEntry entry, bool truncatedEvents)
    {
        var perActor = new Dictionary<string, ActorAgg>(entry.Stats.Count);
        var dmgSkills = new Dictionary<string, IReadOnlyList<SkillAgg>>();
        var healSkills = new Dictionary<string, IReadOnlyList<SkillAgg>>();
        var takenSkills = new Dictionary<string, IReadOnlyList<TakenAgg>>();
        foreach (var (id, s) in entry.Stats)
        {
            var key = id.Value.ToString(CultureInfo.InvariantCulture);
            perActor[key] = new ActorAgg(s.TotalDamage, s.TotalHealing, s.TotalTaken,
                s.Hits, s.Crits, s.Luckys, s.Deaths, s.TopHit, s.FirstHitMs, s.LastHitMs);

            var dl = new List<SkillAgg>(); var hl = new List<SkillAgg>();
            foreach (var (sid, sk) in s.BySkill)
            {
                if (sk.Total > 0) dl.Add(new SkillAgg(sid, sk.Total, sk.Hits, sk.Crits));
                if (sk.HealTotal > 0) hl.Add(new SkillAgg(sid, sk.HealTotal, sk.Hits, sk.Crits));
            }
            var tl = new List<TakenAgg>();
            foreach (var (sid, inc) in s.IncomingBySkill) tl.Add(new TakenAgg(sid, inc.Total, inc.Hits));
            dmgSkills[key] = dl; healSkills[key] = hl; takenSkills[key] = tl;
        }

        var deaths = new List<DeathRec>(entry.DeathLog.Count);
        foreach (var de in entry.DeathLog)
            deaths.Add(new DeathRec(de.Ms, de.Victim.Value.ToString(CultureInfo.InvariantCulture), de.Skill));

        var series = BuildSeries(entry);
        return new Derived(entry.CombatDurationMs, truncatedEvents, perActor, dmgSkills, healSkills, takenSkills, deaths, series);
    }

    private static SeriesBlock BuildSeries(Plugin.EncounterHistoryEntry entry)
    {
        // SourceSeries.BucketMs can differ per actor if a timeline coalesced; normalize to the max bucket.
        int bucketMs = 1000;
        foreach (var ser in entry.Series.Values) if (ser.BucketMs > bucketMs) bucketMs = ser.BucketMs;
        var perActor = new Dictionary<string, ActorSeries>(entry.Series.Count);
        foreach (var (id, ser) in entry.Series)
        {
            var key = id.Value.ToString(CultureInfo.InvariantCulture);
            perActor[key] = new ActorSeries(
                Rebucket(ser.Dealt, ser.BucketMs, bucketMs),
                Rebucket(ser.Healing, ser.BucketMs, bucketMs),
                Rebucket(ser.Taken, ser.BucketMs, bucketMs));
        }
        return new SeriesBlock(bucketMs, perActor);
    }

    // Merge a per-actor array recorded at srcBucketMs into dstBucketMs slots (dst is a multiple of src).
    private static long[] Rebucket(long[] src, int srcBucketMs, int dstBucketMs)
    {
        if (src.Length == 0 || srcBucketMs == dstBucketMs || srcBucketMs <= 0) return src;
        int factor = dstBucketMs / srcBucketMs;
        if (factor <= 1) return src;
        int len = (src.Length + factor - 1) / factor;
        var dst = new long[len];
        for (int i = 0; i < src.Length; i++) dst[i / factor] += src[i];
        return dst;
    }
}
