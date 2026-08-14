// VENDORED-adjacent (see CombatLogWriter.cs' header): the Spec B per-target-bucket half of the
// `derived` writer, kept in its own partial so CombatLogWriter.cs stays under the 500-LoC guardrail.
// Emission is CONDITIONAL exactly like `imagineCasts` — a run with no buckets writes no keys at all,
// so its JSON is byte-identical to a pre-Spec-B upload (spec §7.5).

using System.Collections.Generic;

namespace Stellar.CombatMeter.LogUpload;

internal static partial class CombatLogWriter
{
    private static void WriteDerivedBuckets(JsonWriter w, Derived d)
    {
        if (d.PerActorBossDealt  is { Count: > 0 } bd) { w.Name("perActorBossDealt");  WriteDealtBuckets(w, bd); }
        if (d.PerActorBossTaken  is { Count: > 0 } bt) { w.Name("perActorBossTaken");  WriteTakenBuckets(w, bt); }
        if (d.PerActorBossSeries is { Count: > 0 } bs) { w.Name("perActorBossSeries"); WriteSeriesBuckets(w, bs); }
        if (d.PerActorEliteDealt  is { Count: > 0 } ed) { w.Name("perActorEliteDealt");  WriteDealtBuckets(w, ed); }
        if (d.PerActorEliteTaken  is { Count: > 0 } et) { w.Name("perActorEliteTaken");  WriteTakenBuckets(w, et); }
        if (d.PerActorEliteSeries is { Count: > 0 } es) { w.Name("perActorEliteSeries"); WriteSeriesBuckets(w, es); }
    }

    private static void WriteDealtBuckets(JsonWriter w, IReadOnlyDictionary<string, IReadOnlyDictionary<string, BucketDealt>> m)
    {
        BeginPerActorBuckets(w, m, (writer, cell) =>
        {
            writer.BeginObject();
            writer.Name("total").Number(cell.Total);
            writer.Name("skills"); WriteSkillRows(writer, cell.Skills);
            writer.EndObject();
        });
    }

    private static void WriteTakenBuckets(JsonWriter w, IReadOnlyDictionary<string, IReadOnlyDictionary<string, BucketTaken>> m)
        => BeginPerActorBuckets(w, m, (writer, cell) =>
        {
            writer.BeginObject();
            writer.Name("total").Number(cell.Total);
            writer.EndObject();
        });

    private static void WriteSeriesBuckets(JsonWriter w, IReadOnlyDictionary<string, IReadOnlyDictionary<string, BucketSeries>> m)
        => BeginPerActorBuckets(w, m, (writer, cell) =>
        {
            writer.BeginObject();
            writer.Name("dealt"); WriteLongArr(writer, cell.Dealt);
            writer.Name("taken"); WriteLongArr(writer, cell.Taken);
            writer.EndObject();
        });

    // { "<uid>": { "<configId|other>": <cell> } } — the shared two-level frame; the delegate writes
    // one cell. Called once per upload assemble (never on the hot path), so the closure alloc is fine.
    private static void BeginPerActorBuckets<T>(
        JsonWriter w, IReadOnlyDictionary<string, IReadOnlyDictionary<string, T>> m, System.Action<JsonWriter, T> writeCell)
    {
        w.BeginObject();
        foreach (var actor in m)
        {
            w.Name(actor.Key); w.BeginObject();
            foreach (var bucket in actor.Value) { w.Name(bucket.Key); writeCell(w, bucket.Value); }
            w.EndObject();
        }
        w.EndObject();
    }

    // One canonical skill-row emitter, shared with the whole-fight WriteSkillMap — per-bucket rows are
    // the SAME wire shape (the bucket store simply leaves luckys/critLuckys/top/min at 0).
    private static void WriteSkillRows(JsonWriter w, IReadOnlyList<SkillAgg> rows)
    {
        w.BeginArray();
        foreach (var s in rows)
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
}
