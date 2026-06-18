using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>
/// Reflection-free (IL2CPP-safe) serialize/deserialize of <see cref="Plugin.EncounterHistoryEntry"/> to/from a
/// compact JSON string, built on <see cref="HistoryJsonWriter"/> / <see cref="HistoryJsonReader"/>. Each history
/// entry persists as ONE JSON string; the plugin stores the list as a <c>string[]</c> under the config
/// <c>history.entries</c> key (the per-plugin <c>&lt;guid&gt;.config.json</c> in the game dir).
///
/// Robustness contract: <see cref="TryDeserializeEntry"/> NEVER throws. On any malformed / legacy / truncated
/// input it returns <c>false</c> so the caller skips that one entry and keeps the rest. The format carries a
/// <c>"v":1</c> version field for clean future migration.
///
/// EntityId is serialized as its raw <see cref="EntityId.Value"/> long. Dictionaries are serialized as JSON arrays
/// of <c>[key, value]</c>-style objects (key + nested object), keeping keys reflection-free.
/// </summary>
internal static partial class HistoryStore
{
    // v1 = stats + series. v2 = + "entities" (per-player frozen snapshot, issue #5). The reader accepts v1 || v2
    // (v1 entries load with an empty Entities map → backward compatible), so writing v2 never strands old files.
    internal const int FormatVersion = 2;
    internal const int MinSupportedVersion = 1;

    // ----- serialize -----

    internal static string SerializeEntry(Plugin.EncounterHistoryEntry e)
    {
        var w = new HistoryJsonWriter();
        w.BeginObject();
        w.Name("v").Value(FormatVersion);
        w.Name("scene").Value(e.SceneName);
        w.Name("enter").Value(e.EnteredAtMs);
        w.Name("arch").Value(e.ArchivedAtMs);
        w.Name("dur").Value(e.CombatDurationMs);
        w.Name("party").Value((int)e.PartyType);
        w.Name("members").Value(e.MemberCount);
        w.Name("stats"); WriteStats(w, e.Stats);
        w.Name("series"); WriteSeries(w, e.Series);
        w.Name("entities"); WriteEntities(w, e.Entities);
        w.EndObject();
        return w.ToString();
    }

    private static void WriteStats(HistoryJsonWriter w, Dictionary<EntityId, SourceStats> stats)
    {
        w.BeginArray();
        foreach (var (id, s) in stats)
        {
            w.BeginObject();
            w.Name("id").Value(id.Value);
            w.Name("td").Value(s.TotalDamage);
            w.Name("th").Value(s.TotalHealing);
            w.Name("tk").Value(s.TotalTaken);
            w.Name("top").Value(s.TopHit);
            w.Name("h").Value(s.Hits);
            w.Name("c").Value(s.Crits);
            w.Name("lk").Value(s.Luckys);
            w.Name("k").Value(s.Kills);
            w.Name("fh").Value(s.FirstHitMs);
            w.Name("lh").Value(s.LastHitMs);
            w.Name("sk"); WriteSkills(w, s.BySkill);
            w.Name("in"); WriteIncoming(w, s.IncomingBySkill);
            w.EndObject();
        }
        w.EndArray();
    }

    private static void WriteSkills(HistoryJsonWriter w, Dictionary<int, SkillStats> bySkill)
    {
        w.BeginArray();
        foreach (var (sid, sk) in bySkill)
        {
            w.BeginObject();
            w.Name("id").Value(sid);
            w.Name("t").Value(sk.Total);
            w.Name("ht").Value(sk.HealTotal);
            w.Name("h").Value(sk.Hits);
            w.Name("c").Value(sk.Crits);
            w.Name("lk").Value(sk.Luckys);
            w.Name("top").Value(sk.TopHit);
            w.EndObject();
        }
        w.EndArray();
    }

    private static void WriteIncoming(HistoryJsonWriter w, Dictionary<int, IncomingSkillStats> incoming)
    {
        w.BeginArray();
        foreach (var (sid, inc) in incoming)
        {
            w.BeginObject();
            w.Name("id").Value(sid);
            w.Name("t").Value(inc.Total);
            w.Name("h").Value(inc.Hits);
            w.Name("top").Value(inc.TopHit);
            w.EndObject();
        }
        w.EndArray();
    }

    private static void WriteSeries(HistoryJsonWriter w, Dictionary<EntityId, SourceSeries> series)
    {
        w.BeginArray();
        foreach (var (id, sr) in series)
        {
            w.BeginObject();
            w.Name("id").Value(id.Value);
            w.Name("b").Value(sr.BucketMs);
            w.Name("d").Value(sr.Dealt);
            w.Name("hl").Value(sr.Healing);
            w.Name("tk").Value(sr.Taken);
            w.EndObject();
        }
        w.EndArray();
    }

    // v2: per-player frozen entity snapshots — scalars + parallel primitive arrays (issue #5). Keys are terse to
    // keep the JSON compact at HistoryCapacity=50; the reader matches them by name.
    private static void WriteEntities(HistoryJsonWriter w, Dictionary<EntityId, EntitySnapshot> entities)
    {
        w.BeginArray();
        foreach (var (id, s) in entities)
        {
            w.BeginObject();
            w.Name("id").Value(id.Value);
            w.Name("nm").Value(s.Name);
            w.Name("fp").Value(s.FightPoint);
            w.Name("hp").Value(s.Hp);
            w.Name("mhp").Value(s.MaxHp);
            w.Name("tm").Value(s.TeamId);
            w.Name("ai").Value(s.AttrIds);
            w.Name("av").Value(s.AttrValues);
            w.Name("gs").Value(s.GearSlots);
            w.Name("gi").Value(s.GearItemIds);
            w.Name("si").Value(s.SkillIds);
            w.Name("sl").Value(s.SkillLevels);
            w.Name("st").Value(s.SkillTiers);
            w.Name("fs").Value(s.FashionSlots);
            w.Name("fi").Value(s.FashionIds);
            w.Name("fc").Value(s.FashionDyeCounts);
            w.Name("fd").Value(s.FashionDyes);
            w.EndObject();
        }
        w.EndArray();
    }
}
