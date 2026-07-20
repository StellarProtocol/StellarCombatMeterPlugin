using System;
using System.Collections.Generic;

namespace Stellar.CombatMeter;

/// <summary>Low-level value/container readers shared by <see cref="HistoryStore"/>'s deserialize path. Each
/// returns false (never throws) on a token-shape mismatch so the surrounding entry can be skipped wholesale.</summary>
internal static partial class HistoryStore
{
    private static bool ReadLong(HistoryJsonReader r, out long v)
    {
        v = 0;
        if (r.Next() != JsonTokenKind.Number) return false;
        v = r.NumberValue;
        return true;
    }

    private static bool ReadInt(HistoryJsonReader r, out int v)
    {
        v = 0;
        if (!ReadLong(r, out var l)) return false;
        v = (int)l;
        return true;
    }

    private static bool ReadString(HistoryJsonReader r, out string? v)
    {
        v = null;
        if (r.Next() != JsonTokenKind.String) return false;
        v = r.StringValue;
        return true;
    }

    private static bool ReadLongArray(HistoryJsonReader r, out long[] arr)
    {
        arr = System.Array.Empty<long>();
        var list = new List<long>();
        if (r.Next() != JsonTokenKind.ArrayStart) return false;
        while (true)
        {
            var k = r.Next();
            if (k == JsonTokenKind.ArrayEnd) break;
            if (k == JsonTokenKind.Comma) continue;
            if (k != JsonTokenKind.Number) return false;
            list.Add(r.NumberValue);
        }
        arr = list.ToArray();
        return true;
    }

    private static bool ReadIntArray(HistoryJsonReader r, out int[] arr)
    {
        arr = System.Array.Empty<int>();
        var list = new List<int>();
        if (r.Next() != JsonTokenKind.ArrayStart) return false;
        while (true)
        {
            var k = r.Next();
            if (k == JsonTokenKind.ArrayEnd) break;
            if (k == JsonTokenKind.Comma) continue;
            if (k != JsonTokenKind.Number) return false;
            list.Add((int)r.NumberValue);
        }
        arr = list.ToArray();
        return true;
    }

    private static bool ReadFloatArray(HistoryJsonReader r, out float[] arr)
    {
        arr = System.Array.Empty<float>();
        var list = new List<float>();
        if (r.Next() != JsonTokenKind.ArrayStart) return false;
        while (true)
        {
            var k = r.Next();
            if (k == JsonTokenKind.ArrayEnd) break;
            if (k == JsonTokenKind.Comma) continue;
            if (k != JsonTokenKind.Number) return false;
            list.Add((float)r.DoubleValue);
        }
        arr = list.ToArray();
        return true;
    }

    // Iterate a JSON array, invoking <paramref name="readElement"/> with the reader positioned BEFORE each
    // element's first token. The callback must fully consume exactly one element.
    private static bool ReadArray(HistoryJsonReader r, Func<bool> readElement)
    {
        if (r.Next() != JsonTokenKind.ArrayStart) return false;
        while (true)
        {
            // An empty array ends immediately; otherwise each element is an object whose ObjectStart we consume
            // here and whose body the element reader walks. Commas separate elements.
            var k = r.Next();
            if (k == JsonTokenKind.ArrayEnd) return true;
            if (k == JsonTokenKind.Comma) continue;
            if (k != JsonTokenKind.ObjectStart) return false;
            if (!readElement()) return false;
        }
    }

    // Consume and DISCARD one complete JSON value (scalar, string, array, or object) positioned after a
    // colon. Lets an unknown key be skipped instead of failing the whole entry (forward-compat: a future
    // format's new keys load under this build as "read what you understand"). Returns false only on a
    // structurally broken value (EOF/Error/token in value position) so a genuinely malformed entry is still
    // rejected. Never throws.
    private static bool SkipValue(HistoryJsonReader r)
    {
        var k = r.Next();
        return k switch
        {
            JsonTokenKind.Number or JsonTokenKind.String => true,        // scalar already consumed
            JsonTokenKind.ArrayStart  => SkipContainer(r, JsonTokenKind.ArrayEnd),
            JsonTokenKind.ObjectStart => SkipContainer(r, JsonTokenKind.ObjectEnd),
            _ => false,                                                   // colon/comma/eof/error = malformed
        };
    }

    // Skip tokens until the matching container end, recursing into nested arrays/objects. A well-formed
    // (future-writer) value always closes; malformed input runs into Eof/Error and fails cleanly.
    private static bool SkipContainer(HistoryJsonReader r, JsonTokenKind end)
    {
        while (true)
        {
            var k = r.Next();
            if (k == end) return true;
            if (k == JsonTokenKind.Eof || k == JsonTokenKind.Error) return false;
            if (k == JsonTokenKind.ArrayStart  && !SkipContainer(r, JsonTokenKind.ArrayEnd))  return false;
            if (k == JsonTokenKind.ObjectStart && !SkipContainer(r, JsonTokenKind.ObjectEnd)) return false;
            // scalars, colons, commas inside the container are simply consumed and skipped
        }
    }

    // Read an object body, invoking <paramref name="onKey"/> for each key (reader positioned after the colon,
    // BEFORE the value). The ObjectStart has already been consumed by the caller (ReadArray).
    private static bool ReadObject(HistoryJsonReader r, Func<string, bool> onKey)
    {
        while (true)
        {
            var k = r.Next();
            if (k == JsonTokenKind.ObjectEnd) return true;
            if (k == JsonTokenKind.Comma) continue;
            if (k != JsonTokenKind.String) return false;
            var key = r.StringValue;
            if (r.Next() != JsonTokenKind.Colon) return false;
            if (!onKey(key)) return false;
        }
    }
}
