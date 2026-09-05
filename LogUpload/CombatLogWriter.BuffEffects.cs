// Task 9 (rDPS capture, spec 2026-09-05 § 4.2/§6.1): the buff-effect half of the `derived` writer,
// kept in its own partial so CombatLogWriter.cs stays under the 500-LoC guardrail (it was already at
// cap). Emission is CONDITIONAL for `buffEffects` exactly like `imagineCasts` — a run where the
// sampler drained nothing writes no key at all; `truncatedBuffEvents` is unconditional (always a bool).

using System.Collections.Generic;

namespace Stellar.CombatMeter.LogUpload;

internal static partial class CombatLogWriter
{
    private static void WriteBuffEffects(JsonWriter w, Derived d)
    {
        w.Name("truncatedBuffEvents").Bool(d.TruncatedBuffEvents);
        if (d.BuffEffects is not { Count: > 0 } effects) return;

        w.Name("buffEffects"); w.BeginArray();
        foreach (var e in effects)
        {
            w.BeginObject();
            w.Name("base").Number(e.Base);
            w.Name("stacks").Number(e.Stacks);
            w.Name("srcKind").Number(e.SrcKind);
            w.Name("srcId").Number(e.SrcId);
            w.Name("n").Number(e.N);
            w.Name("deltas"); WriteBuffEffectDeltas(w, e.Deltas);
            w.EndObject();
        }
        w.EndArray();
    }

    private static void WriteBuffEffectDeltas(JsonWriter w, IReadOnlyList<(int AttrId, long MedianDelta)> deltas)
    {
        w.BeginArray();
        foreach (var (attrId, median) in deltas)
        {
            w.BeginArray();
            w.Number(attrId);
            w.Number(median);
            w.EndArray();
        }
        w.EndArray();
    }
}
