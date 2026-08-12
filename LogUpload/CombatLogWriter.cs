// VENDORED from services/stellar-logs/dotnet/Stellar.LogFormat/ — DO NOT edit upstream here.
// Namespace adjusted to Stellar.CombatMeter.LogUpload for plugin-local use.
// Hand-rolled, reflection-free (IL2CPP-safe) JSON writer.

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>Hand-rolled, reflection-free (IL2CPP-safe) JSON writer for a <see cref="CombatLog"/>.</summary>
internal static class CombatLogWriter
{
    internal static string Write(CombatLog log)
    {
        var w = new JsonWriter();
        w.BeginObject();
        w.Name("v").Number(log.V);
        w.Name("header"); WriteHeader(w, log.Header);
        w.Name("actors"); WriteActors(w, log.Actors);
        w.Name("events"); WriteEvents(w, log.Events);
        if (log.Derived != null) { w.Name("derived"); WriteDerived(w, log.Derived); }
        w.EndObject();
        return w.ToString();
    }

    private static void WriteDerived(JsonWriter w, Derived d)
    {
        w.BeginObject();
        w.Name("combatDurationMs").Number(d.CombatDurationMs);
        w.Name("truncatedEvents").Bool(d.TruncatedEvents);
        w.Name("perActor"); WriteActorAggs(w, d.PerActor);
        w.Name("perActorSkills"); WriteSkillMap(w, d.PerActorSkills);
        w.Name("perActorHealSkills"); WriteSkillMap(w, d.PerActorHealSkills);
        w.Name("perActorTakenSkills"); WriteTakenMap(w, d.PerActorTakenSkills);
        w.Name("deaths"); WriteDeaths(w, d.Deaths);
        if (d.ImagineCasts is { Count: > 0 } casts)
        {
            w.Name("imagineCasts"); w.BeginArray();
            foreach (var c in casts) { w.BeginObject(); w.Name("ms").Number(c.Ms); w.Name("src").Str(c.Src); w.Name("skill").Number(c.Skill); w.EndObject(); }
            w.EndArray();
        }
        w.Name("series"); WriteSeries(w, d.Series);
        w.EndObject();
    }

    private static void WriteActorAggs(JsonWriter w, IReadOnlyDictionary<string, ActorAgg> m)
    {
        w.BeginObject();
        foreach (var kv in m)
        {
            w.Name(kv.Key); var a = kv.Value; w.BeginObject();
            w.Name("damage").Number(a.Damage); w.Name("healing").Number(a.Healing); w.Name("damageTaken").Number(a.DamageTaken);
            w.Name("hits").Number(a.Hits); w.Name("crits").Number(a.Crits); w.Name("luckys").Number(a.Luckys); w.Name("deaths").Number(a.Deaths);
            w.Name("topHit").Number(a.TopHit); w.Name("firstHitMs").Number(a.FirstHitMs); w.Name("lastHitMs").Number(a.LastHitMs);
            w.Name("critLuckys").Number(a.CritLuckys);
            w.Name("critDmg").Number(a.CritDamage); w.Name("luckyDmg").Number(a.LuckyDamage); w.Name("critLuckyDmg").Number(a.CritLuckyDamage);
            w.Name("shieldBreak").Number(a.ShieldBreak);
            w.Name("healHits").Number(a.HealHits); w.Name("healCrits").Number(a.HealCrits); w.Name("healLuckys").Number(a.HealLuckys); w.Name("healCritLuckys").Number(a.HealCritLuckys);
            w.Name("critHeal").Number(a.CritHealing); w.Name("luckyHeal").Number(a.LuckyHealing); w.Name("critLuckyHeal").Number(a.CritLuckyHealing);
            w.Name("topHeal").Number(a.TopHeal); w.Name("effHeal").Number(a.EffectiveHealing);
            w.EndObject();
        }
        w.EndObject();
    }

    private static void WriteSkillMap(JsonWriter w, IReadOnlyDictionary<string, IReadOnlyList<SkillAgg>> m)
    {
        w.BeginObject();
        foreach (var kv in m)
        {
            w.Name(kv.Key); w.BeginArray();
            foreach (var s in kv.Value)
            {
                w.BeginObject();
                w.Name("skillId").Number(s.SkillId); w.Name("total").Number(s.Total);
                w.Name("hits").Number(s.Hits); w.Name("crits").Number(s.Crits);
                w.Name("luckys").Number(s.Luckys); w.Name("critLuckys").Number(s.CritLuckys);
                w.Name("top").Number(s.Top); w.Name("min").Number(s.Min);
                w.EndObject();
            }
            w.EndArray();
        }
        w.EndObject();
    }

    private static void WriteTakenMap(JsonWriter w, IReadOnlyDictionary<string, IReadOnlyList<TakenAgg>> m)
    {
        w.BeginObject();
        foreach (var kv in m)
        {
            w.Name(kv.Key); w.BeginArray();
            foreach (var s in kv.Value) { w.BeginObject(); w.Name("skillId").Number(s.SkillId); w.Name("total").Number(s.Total); w.Name("hits").Number(s.Hits); w.Name("top").Number(s.Top); w.EndObject(); }
            w.EndArray();
        }
        w.EndObject();
    }

    private static void WriteDeaths(JsonWriter w, IReadOnlyList<DeathRec> deaths)
    {
        w.BeginArray();
        foreach (var x in deaths) { w.BeginObject(); w.Name("ms").Number(x.Ms); w.Name("victim").Str(x.Victim); w.Name("skill").Number(x.Skill); w.EndObject(); }
        w.EndArray();
    }

    private static void WriteSeries(JsonWriter w, SeriesBlock s)
    {
        w.BeginObject();
        w.Name("bucketMs").Number(s.BucketMs);
        w.Name("perActor"); w.BeginObject();
        foreach (var kv in s.PerActor)
        {
            w.Name(kv.Key); var a = kv.Value; w.BeginObject();
            w.Name("dealt"); WriteLongArr(w, a.Dealt); w.Name("healing"); WriteLongArr(w, a.Healing); w.Name("taken"); WriteLongArr(w, a.Taken);
            w.EndObject();
        }
        w.EndObject(); w.EndObject();
    }

    private static void WriteLongArr(JsonWriter w, IReadOnlyList<long> arr) { w.BeginArray(); foreach (var n in arr) w.Number(n); w.EndArray(); }

    private static void WriteHeader(JsonWriter w, LogHeader h)
    {
        w.BeginObject();
        w.Name("logId").Str(h.LogId);
        w.Name("capturedAtMs").Number(h.CapturedAtMs);
        w.Name("gameVersion").Str(h.GameVersion);
        w.Name("region").Str(h.Region);
        if (h.FrameworkVer != null) w.Name("frameworkVer").Str(h.FrameworkVer);
        if (h.PluginVer != null) w.Name("pluginVer").Str(h.PluginVer);
        w.Name("privacy").Str(h.Privacy);
        w.Name("encounter"); WriteEncounter(w, h.Encounter);
        w.Name("uploader"); WriteUploader(w, h.Uploader);
        w.Name("eventChunks").Number(h.EventChunks);
        w.EndObject();
    }

    private static void WriteEncounter(JsonWriter w, Encounter e)
    {
        w.BeginObject();
        w.Name("kind").Str(e.Kind);
        w.Name("levelUuid").Str(e.LevelUuid.ToString(CultureInfo.InvariantCulture));
        if (e.DungeonGuid != null) w.Name("dungeonGuid").Str(e.DungeonGuid);
        w.Name("mapId").Number(e.MapId);
        w.Name("lineId").Number(e.LineId);
        if (e.Name != null) w.Name("name").Str(e.Name);
        w.Name("bossId").Number(e.BossId);
        if (e.BossKilled) w.Name("bossKilled").Bool(true);
        if (e.Bosses is { Count: > 0 } bosses)
        {
            w.Name("bosses"); w.BeginArray();
            foreach (var b in bosses)
            {
                w.BeginObject();
                w.Name("configId").Number(b.ConfigId);
                if (b.Killed) w.Name("killed").Bool(true);
                w.EndObject();
            }
            w.EndArray();
        }
        if (e.BossName != null) w.Name("bossName").Str(e.BossName);
        if (e.Difficulty != null) w.Name("difficulty").Str(e.Difficulty);
        w.Name("masterModeScore").Number(e.MasterModeScore);
        if (e.TotalScore != 0) w.Name("totalScore").Number(e.TotalScore);
        w.Name("result").Str(e.Result);
        w.Name("startMs").Number(e.StartMs);
        w.Name("endMs").Number(e.EndMs);
        w.Name("durationMs").Number(e.DurationMs);
        w.Name("passTime").Number(e.PassTime);
        if (e.DifficultyLevel != 0) w.Name("difficultyLevel").Number(e.DifficultyLevel);
        if (e.DungeonStartMs != 0) w.Name("dungeonStartMs").Number(e.DungeonStartMs);
        if (e.DefeatedCount != 0) w.Name("defeated").Number(e.DefeatedCount);
        if (e.PartyId != 0) w.Name("partyId").Str(e.PartyId.ToString(CultureInfo.InvariantCulture));
        w.EndObject();
    }

    private static void WriteUploader(JsonWriter w, Uploader u)
    {
        w.BeginObject();
        w.Name("localUid").Number(u.LocalUid);
        w.Name("sig").Str(u.Sig);
        w.Name("nonce").Str(u.Nonce);
        // Additive — omitted when unknown (0), matching the server's optional `masterScore`.
        if (u.MasterScore > 0) w.Name("masterScore").Number(u.MasterScore);
        // Per-install identity + second signature (claim hardening). Omitted when no install key.
        if (u.PubKey.Length > 0)
        {
            w.Name("pubkey").Str(u.PubKey);
            w.Name("installSig").Str(u.InstallSig);
        }
        w.EndObject();
    }

    // internal: Task 10's SupplementWriter reuses this to emit the local-only actor map
    // for the supplement POST body (same wire shape as the summary's `actors` object).
    internal static void WriteActors(JsonWriter w, IReadOnlyDictionary<string, Actor> actors)
    {
        w.BeginObject();
        foreach (var kv in actors)
        {
            w.Name(kv.Key);
            WriteActor(w, kv.Value);
        }
        w.EndObject();
    }

    private static void WriteActor(JsonWriter w, Actor a)
    {
        w.BeginObject();
        w.Name("name").Str(a.Name);
        w.Name("kind").Str(a.Kind);
        w.Name("teamId").Number(a.TeamId);
        w.Name("isLocal").Bool(a.IsLocal);
        w.Name("uid"); if (a.Uid.HasValue) w.Number(a.Uid.Value); else w.Null();
        if (a.Kind == "player")
        {
            w.Name("professionId").Number(a.ProfessionId);
            w.Name("level").Number(a.Level);
            w.Name("abilityScore").Number(a.AbilityScore);
            w.Name("maxHp").Number(a.MaxHp);
            w.Name("attributes"); WriteLongPairs(w, a.Attributes);
            // Base+peak stats (2026-08-02): sparse self-only combat peaks — emitted only when present
            // (absent on old runs / non-self / no buff moved a stat), so old-run payloads are unchanged.
            if (a.AttrPeaks is { Count: > 0 } apk) { w.Name("attrPeaks"); WriteLongPairs(w, apk); }
            // Per-entity class detection (2026-08-03): self AND party — unlike modules/loadouts/
            // talents below, NOT gated to isLocal (see Actor.ClassSpans doc).
            if (a.ClassSpans is { Count: > 0 } cs) { w.Name("classSpans"); WriteLongTriples(w, cs); }
            w.Name("gear"); WriteIntArrays(w, a.Gear);
            w.Name("skills"); WriteIntArrays(w, a.Skills);
            w.Name("fashion"); WriteFashion(w, a.Fashion);
            if (a.GearDetail is { Count: > 0 } gd) { w.Name("gearDetail"); WriteGearDetail(w, gd); }
            // Per-class-loadout plan (Task 3), all self-only — the assembler leaves these null/0
            // for every non-local actor (ResolveLoadoutFields), so this simply mirrors GearDetail's
            // own presence-guard convention.
            if (a.Modules is { Count: > 0 } m) { w.Name("modules"); WriteModules(w, m); }
            if (a.TalentStageId > 0) w.Name("talentStageId").Number(a.TalentStageId);
            if (a.TalentNodes is { Count: > 0 } tn) { w.Name("talentNodes"); WriteIntList(w, tn); }
            if (a.Loadouts is { Count: > 0 } l) { w.Name("loadouts"); WriteLoadouts(w, l); }
        }
        w.EndObject();
    }

    private static void WriteModules(JsonWriter w, IReadOnlyList<ModuleEntry> rows)
    {
        w.BeginArray();
        foreach (var m in rows)
        {
            w.BeginObject();
            w.Name("slot").Number(m.Slot);
            w.Name("configId").Number(m.ConfigId);
            w.Name("quality").Number(m.Quality);
            w.Name("parts"); WriteIntArrays(w, m.Parts);
            w.EndObject();
        }
        w.EndArray();
    }

    private static void WriteLoadouts(JsonWriter w, IReadOnlyList<LoadoutEntry> rows)
    {
        w.BeginArray();
        foreach (var l in rows)
        {
            w.BeginObject();
            w.Name("professionId").Number(l.ProfessionId);
            if (l.ProjectName != null) w.Name("projectName").Str(l.ProjectName);
            if (l.AbilityScore > 0) w.Name("abilityScore").Number(l.AbilityScore);
            w.Name("gear"); WriteIntArrays(w, l.Gear);
            if (l.GearDetail is { Count: > 0 } gd) { w.Name("gearDetail"); WriteGearDetail(w, gd); }
            w.Name("skills"); WriteIntArrays(w, l.Skills);
            w.Name("fashion"); WriteFashion(w, l.Fashion);
            if (l.Modules is { Count: > 0 } m) { w.Name("modules"); WriteModules(w, m); }
            if (l.TalentStageId > 0) w.Name("talentStageId").Number(l.TalentStageId);
            if (l.TalentNodes is { Count: > 0 } tn) { w.Name("talentNodes"); WriteIntList(w, tn); }
            if (l.Attributes is { Count: > 0 } at) { w.Name("attributes"); WriteLongPairs(w, at); }
            if (l.AttrPeaks is { Count: > 0 } apk) { w.Name("attrPeaks"); WriteLongPairs(w, apk); }
            w.EndObject();
        }
        w.EndArray();
    }

    // Flat array of numbers (e.g. talent-tree node ids).
    private static void WriteIntList(JsonWriter w, IReadOnlyList<int> xs)
    {
        w.BeginArray();
        foreach (var x in xs) w.Number(x);
        w.EndArray();
    }

    private static void WriteGearDetail(JsonWriter w, IReadOnlyList<GearDetail> rows)
    {
        w.BeginArray();
        foreach (var g in rows)
        {
            w.BeginObject();
            w.Name("slot").Number(g.Slot);
            w.Name("quality").Number(g.Quality);
            w.Name("refine").Number(g.RefineLevel);
            w.Name("lvl").Number(g.ItemLevel);
            w.Name("bt").Number(g.BreakThrough);
            w.Name("perfVal").Number(g.PerfectionValue);
            w.Name("perfMax").Number(g.PerfectionMax);
            w.Name("enchantId").Number(g.EnchantId);
            w.Name("enchantLv").Number(g.EnchantLevel);
            w.Name("rolls"); WriteIntArrays(w, g.Rolls);
            w.EndObject();
        }
        w.EndArray();
    }

    private static void WriteLongPairs(JsonWriter w, IReadOnlyList<long[]> rows)
    {
        w.BeginArray();
        foreach (var r in rows) { w.BeginArray(); foreach (var n in r) w.Number(n); w.EndArray(); }
        w.EndArray();
    }

    // [professionId,startMs,endMs] triples (Actor.ClassSpans). Mechanically identical to
    // WriteLongPairs (each row writes however many numbers it holds) — named separately so the
    // call site documents the 3-element contract rather than reusing a "pairs" name for triples.
    private static void WriteLongTriples(JsonWriter w, IReadOnlyList<long[]> rows)
    {
        w.BeginArray();
        foreach (var r in rows) { w.BeginArray(); foreach (var n in r) w.Number(n); w.EndArray(); }
        w.EndArray();
    }

    private static void WriteIntArrays(JsonWriter w, IReadOnlyList<int[]> rows)
    {
        w.BeginArray();
        foreach (var r in rows) { w.BeginArray(); foreach (var n in r) w.Number(n); w.EndArray(); }
        w.EndArray();
    }

    private static void WriteFashion(JsonWriter w, IReadOnlyList<Fashion> rows)
    {
        w.BeginArray();
        foreach (var f in rows)
        {
            w.BeginArray();
            w.Number(f.Slot); w.Number(f.FashionId);
            w.BeginArray(); foreach (var d in f.Dyes) w.Number(d); w.EndArray();
            w.EndArray();
        }
        w.EndArray();
    }

    private static void WriteEvents(JsonWriter w, IReadOnlyList<CombatLogEvent> events)
    {
        w.BeginArray();
        foreach (var ev in events)
        {
            switch (ev)
            {
                case SkillEvent s:
                    w.BeginObject();
                    w.Name("t").Str("skill"); w.Name("ms").Number(s.Ms);
                    w.Name("src").Str(s.Src); w.Name("skill").Number(s.Skill); w.Name("phase").Number(s.Phase);
                    w.EndObject();
                    break;
                case DamageEvent d:
                    w.BeginObject();
                    w.Name("t").Str("dmg"); w.Name("ms").Number(d.Ms);
                    w.Name("src").Str(d.Src); w.Name("tgt").Str(d.Tgt); w.Name("skill").Number(d.Skill);
                    w.Name("amt").Number(d.Amt); w.Name("act").Number(d.Act); w.Name("shield").Number(d.Shield);
                    w.Name("crit").Bool(d.Crit); w.Name("lucky").Bool(d.Lucky);
                    w.Name("heal").Bool(d.Heal); w.Name("dead").Bool(d.Dead);
                    w.Name("elem").Number(d.Elem); w.Name("kind").Number(d.Kind); w.Name("source").Number(d.Source);
                    w.EndObject();
                    break;
                case BuffEvent b:
                    w.BeginObject();
                    w.Name("t").Str("buff"); w.Name("ms").Number(b.Ms);
                    w.Name("tgt").Str(b.Tgt); w.Name("uuid").Number(b.Uuid); w.Name("base").Number(b.Base);
                    w.Name("kind").Str(b.Kind); w.Name("stacks").Number(b.Stacks);
                    w.Name("layer").Number(b.Layer); w.Name("durMs").Number(b.DurMs);
                    w.EndObject();
                    break;
            }
        }
        w.EndArray();
    }
}

/// <summary>Minimal comma-aware JSON emitter (mirrors the plugin's HistoryJsonWriter style).</summary>
internal sealed class JsonWriter
{
    private readonly StringBuilder _sb = new();
    private bool _needComma;

    internal JsonWriter BeginObject() { Pre(); _sb.Append('{'); _needComma = false; return this; }
    internal JsonWriter EndObject() { _sb.Append('}'); _needComma = true; return this; }
    internal JsonWriter BeginArray() { Pre(); _sb.Append('['); _needComma = false; return this; }
    internal JsonWriter EndArray() { _sb.Append(']'); _needComma = true; return this; }

    internal JsonWriter Name(string key) { Pre(); WriteString(key); _sb.Append(':'); _needComma = false; return this; }
    internal JsonWriter Number(long v) { Pre(); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); _needComma = true; return this; }
    internal JsonWriter Number(float v)
    {
        if (!float.IsFinite(v))
            throw new System.ArgumentException(
                "Cannot serialize a non-finite float (NaN/Infinity) — JSON has no representation for it.", nameof(v));
        Pre();
        string s = v.ToString("R", CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E') && !s.Contains('e'))
            s += ".0";
        _sb.Append(s);
        _needComma = true;
        return this;
    }
    internal JsonWriter Bool(bool v) { Pre(); _sb.Append(v ? "true" : "false"); _needComma = true; return this; }
    internal JsonWriter Null() { Pre(); _sb.Append("null"); _needComma = true; return this; }
    internal JsonWriter Str(string v) { Pre(); WriteString(v); _needComma = true; return this; }
    // Appends an already-serialized JSON fragment verbatim (e.g. reusing EventsJsonWriter's
    // output for a chunk envelope's `events` array) — caller guarantees it is valid JSON.
    internal JsonWriter Raw(string json) { Pre(); _sb.Append(json); _needComma = true; return this; }

    public override string ToString() => _sb.ToString();

    private void Pre() { if (_needComma) _sb.Append(','); _needComma = false; }

    private void WriteString(string s)
    {
        _sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':  _sb.Append("\\\""); break;
                case '\\': _sb.Append("\\\\"); break;
                case '\b': _sb.Append("\\b");  break;
                case '\f': _sb.Append("\\f");  break;
                case '\n': _sb.Append("\\n");  break;
                case '\r': _sb.Append("\\r");  break;
                case '\t': _sb.Append("\\t");  break;
                default:
                    if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else _sb.Append(c);
                    break;
            }
        }
        _sb.Append('"');
    }
}
