// VENDORED from services/stellar-logs/dotnet/Stellar.LogFormat/ — DO NOT edit upstream here.
// Namespace adjusted to Stellar.CombatMeter.LogUpload for plugin-local use.
// Hand-rolled, reflection-free (IL2CPP-safe) JSON writer.
// UNVERIFIED — this code has never been executed in-game.

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
        w.EndObject();
        return w.ToString();
    }

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
        w.EndObject();
    }

    private static void WriteEncounter(JsonWriter w, Encounter e)
    {
        w.BeginObject();
        w.Name("kind").Str(e.Kind);
        w.Name("levelUuid").Number(e.LevelUuid);
        if (e.DungeonGuid != null) w.Name("dungeonGuid").Str(e.DungeonGuid);
        w.Name("mapId").Number(e.MapId);
        w.Name("lineId").Number(e.LineId);
        if (e.Name != null) w.Name("name").Str(e.Name);
        w.Name("bossId").Number(e.BossId);
        if (e.BossName != null) w.Name("bossName").Str(e.BossName);
        if (e.Difficulty != null) w.Name("difficulty").Str(e.Difficulty);
        w.Name("masterModeScore").Number(e.MasterModeScore);
        w.Name("result").Str(e.Result);
        w.Name("startMs").Number(e.StartMs);
        w.Name("endMs").Number(e.EndMs);
        w.Name("durationMs").Number(e.DurationMs);
        w.Name("passTime").Number(e.PassTime);
        w.EndObject();
    }

    private static void WriteUploader(JsonWriter w, Uploader u)
    {
        w.BeginObject();
        w.Name("localUid").Number(u.LocalUid);
        w.Name("sig").Str(u.Sig);
        w.Name("nonce").Str(u.Nonce);
        w.EndObject();
    }

    private static void WriteActors(JsonWriter w, IReadOnlyDictionary<string, Actor> actors)
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
            w.Name("gear"); WriteIntArrays(w, a.Gear);
            w.Name("skills"); WriteIntArrays(w, a.Skills);
            w.Name("fashion"); WriteFashion(w, a.Fashion);
        }
        w.EndObject();
    }

    private static void WriteLongPairs(JsonWriter w, IReadOnlyList<long[]> rows)
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
