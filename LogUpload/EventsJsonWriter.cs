// UNVERIFIED — this code has never been executed in-game.
// SP1: Minimal JSON serializer for the events array only (used by CanonicalPayload).
// Mirrors exactly the event-array output of CombatLogWriter so SHA-256 hashes match.

using System.Collections.Generic;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Serializes the events array to the same JSON format as <see cref="CombatLogWriter"/>
/// so <see cref="CanonicalPayload"/> can hash it independently.
/// </summary>
internal static class EventsJsonWriter
{
    internal static string Write(IReadOnlyList<CombatLogEvent> events)
    {
        var w = new JsonWriter();
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
        return w.ToString();
    }
}
