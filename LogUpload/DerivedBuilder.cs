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
                s.Hits, s.Crits, s.Luckys, s.Deaths, s.TopHit, s.FirstHitMs, s.LastHitMs,
                s.CritLuckys,
                s.CritDamage, s.LuckyDamage, s.CritLuckyDamage, s.ShieldBreak,
                s.HealHits, s.HealCrits, s.HealLuckys, s.HealCritLuckys,
                s.CritHealing, s.LuckyHealing, s.CritLuckyHealing,
                s.TopHeal, s.EffectiveHealing);

            var dl = new List<SkillAgg>(); var hl = new List<SkillAgg>();
            foreach (var (sid, sk) in s.BySkill)
            {
                if (sk.Total > 0)
                    dl.Add(new SkillAgg(sid, sk.Total, sk.Hits, sk.Crits, sk.Luckys, sk.CritLuckys, sk.TopHit, sk.MinHit));
                // Heal rows use the SEPARATE heal counters (damage Hits/Crits were wrong for hybrids).
                if (sk.HealTotal > 0)
                    hl.Add(new SkillAgg(sid, sk.HealTotal, sk.HealHits, sk.HealCrits, sk.HealLuckys, 0, sk.HealTop, 0));
            }
            var tl = new List<TakenAgg>();
            foreach (var (sid, inc) in s.IncomingBySkill) tl.Add(new TakenAgg(sid, inc.Total, inc.Hits, inc.TopHit));
            dmgSkills[key] = dl; healSkills[key] = hl; takenSkills[key] = tl;
        }

        var deaths = new List<DeathRec>(entry.DeathLog.Count);
        foreach (var de in entry.DeathLog)
            deaths.Add(new DeathRec(de.Ms, de.Victim.Value.ToString(CultureInfo.InvariantCulture), de.Skill));

        var casts = new List<ImagineCastRec>(entry.ImagineCasts.Count);
        foreach (var ic in entry.ImagineCasts)
            casts.Add(new ImagineCastRec(ic.Ms, ic.Source.Value.ToString(CultureInfo.InvariantCulture), ic.Skill));

        // ONE normalized bucket width for the whole block — the max over the per-actor timelines AND
        // the Spec B bucket cells (which coalesce independently; see DerivedBucketBuilder).
        var bucketMs = DerivedBucketBuilder.ResolveBucketMs(entry);
        var series = BuildSeries(entry, bucketMs);
        var boss  = DerivedBucketBuilder.Build(entry.BossBuckets, bucketMs);
        var elite = DerivedBucketBuilder.Build(entry.EliteBuckets, bucketMs);
        return new Derived(entry.CombatDurationMs, truncatedEvents, perActor, dmgSkills, healSkills, takenSkills, deaths, series,
            casts.Count > 0 ? casts : null,
            boss.Dealt, boss.Taken, boss.Series,
            elite.Dealt, elite.Taken, elite.Series);
    }

    private static SeriesBlock BuildSeries(Plugin.EncounterHistoryEntry entry, int bucketMs)
    {
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

    // Merge a series recorded at srcBucketMs into dstBucketMs slots (dst is a multiple of src — every
    // timeline starts at 1000 ms and only ever DOUBLES). Loss-free: the merged array's sum is unchanged.
    // internal so DerivedBucketBuilder normalizes the Spec B per-bucket series through this same path.
    internal static long[] Rebucket(long[] src, int srcBucketMs, int dstBucketMs)
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
