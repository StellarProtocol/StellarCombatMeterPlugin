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
    // v1 = stats + series. v2 = + "entities" (per-player frozen snapshot, issue #5). v3 = + run identity
    // (luid/pass/mms/res) so an archived run keeps the levelUuid a (deferred/manual) upload needs. v4 = richer
    // per-skill stats (crit-lucky/min/kills + separate heal counters) + self gear detail. v5 = ZDPS-parity
    // per-ACTOR splits (crit/lucky/crit-lucky damage+healing values, shield break, top/effective heal).
    // The reader accepts v1..v5 — older entries just lack the newer keys and load with defaults, so
    // writing v5 never strands old files. (Runs archived before v3 have no persisted levelUuid → upload as 0.)
    internal const int FormatVersion = 5;
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
        w.Name("luid").Value(e.LevelUuid);          // run identity — needed for (deferred) upload
        w.Name("pass").Value(e.PassTime);
        w.Name("mms").Value(e.MasterModeScore);
        w.Name("res").Value(e.Result);
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
            w.Name("cl").Value(s.CritLuckys);
            w.Name("k").Value(s.Kills);
            w.Name("fh").Value(s.FirstHitMs);
            w.Name("lh").Value(s.LastHitMs);
            w.Name("cd").Value(s.CritDamage);
            w.Name("ld").Value(s.LuckyDamage);
            w.Name("cld").Value(s.CritLuckyDamage);
            w.Name("sb").Value(s.ShieldBreak);
            w.Name("hh").Value(s.HealHits);
            w.Name("hc").Value(s.HealCrits);
            w.Name("hlk").Value(s.HealLuckys);
            w.Name("hcl").Value(s.HealCritLuckys);
            w.Name("ch").Value(s.CritHealing);
            w.Name("lch").Value(s.LuckyHealing);
            w.Name("clh").Value(s.CritLuckyHealing);
            w.Name("tph").Value(s.TopHeal);
            w.Name("eh").Value(s.EffectiveHealing);
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
            w.Name("cl").Value(sk.CritLuckys);
            w.Name("top").Value(sk.TopHit);
            w.Name("mn").Value(sk.MinHit);
            w.Name("k").Value(sk.Kills);
            w.Name("hh").Value(sk.HealHits);
            w.Name("hc").Value(sk.HealCrits);
            w.Name("hlk").Value(sk.HealLuckys);
            w.Name("htp").Value(sk.HealTop);
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
            // v4: self-only per-piece gear detail (empty arrays for non-self entities).
            w.Name("gds").Value(s.GdSlots);
            w.Name("gdq").Value(s.GdQuality);
            w.Name("gdr").Value(s.GdRefine);
            w.Name("gdlv").Value(s.GdItemLv);
            w.Name("gdbt").Value(s.GdBt);
            w.Name("gdpv").Value(s.GdPerfVal);
            w.Name("gdpm").Value(s.GdPerfMax);
            w.Name("gdei").Value(s.GdEnchantId);
            w.Name("gdel").Value(s.GdEnchantLv);
            w.Name("gdrc").Value(s.GdRollCounts);
            w.Name("gdrl").Value(s.GdRolls);
            w.EndObject();
        }
        w.EndArray();
    }
}
