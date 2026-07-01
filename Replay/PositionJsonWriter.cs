// Hand-rolled, reflection-free (IL2CPP-safe) JSON writer for PositionUploadDoc.
// WriteBodyOnly output MUST byte-match the worker's positionsBody:
//   JSON.stringify({ hz, mapId, origin, scale, tracks, meta })
// Keys in that exact order; compact (no whitespace); arrays no spaces.

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Stellar.CombatMeter.Replay;

/// <summary>
/// Serializes a <see cref="PositionUploadDoc"/> to compact JSON.
/// <see cref="WriteBodyOnly"/> produces the exact bytes the replay worker hashes for signing.
/// </summary>
internal static class PositionJsonWriter
{
    /// <summary>
    /// Writes the full document including <c>sig</c> and <c>nonce</c> fields (when present).
    /// </summary>
    internal static string Write(PositionUploadDoc doc)
    {
        var w = new PosWriter();
        w.BeginObject();
        WriteBodyFields(w, doc);
        if (doc.Sig != null)   { w.Name("sig");   w.Str(doc.Sig); }
        if (doc.Nonce != null) { w.Name("nonce"); w.Str(doc.Nonce); }
        w.EndObject();
        return w.ToString();
    }

    /// <summary>
    /// Writes only the body fields (no sig/nonce).
    /// Output matches JS <c>JSON.stringify({hz,mapId,origin,scale,tracks,meta})</c> exactly.
    /// </summary>
    internal static string WriteBodyOnly(PositionUploadDoc doc)
    {
        var w = new PosWriter();
        w.BeginObject();
        WriteBodyFields(w, doc);
        w.EndObject();
        return w.ToString();
    }

    // ── body fields in the required JS key order ──────────────────────────────

    private static void WriteBodyFields(PosWriter w, PositionUploadDoc doc)
    {
        w.Name("hz");     w.Int(doc.Hz);
        w.Name("mapId");  w.Int(doc.MapId);
        w.Name("origin"); WriteOrigin(w, doc.Origin);
        w.Name("scale");  w.ScalarFloat(doc.Scale);
        w.Name("tracks"); WriteTracks(w, doc.Tracks);
        w.Name("meta");   WriteMeta(w, doc.Meta);
    }

    // origin: [X,Z] — emit as integers when whole numbers (matches JS integer literals).
    private static void WriteOrigin(PosWriter w, (float X, float Z) origin)
    {
        w.BeginArray();
        w.WholeFloat(origin.X);
        w.WholeFloat(origin.Z);
        w.EndArray();
    }

    private static void WriteTracks(PosWriter w, IReadOnlyDictionary<string, PositionTrackDto> tracks)
    {
        w.BeginObject();
        foreach (var kv in tracks)
        {
            w.Name(kv.Key);
            WriteTrackDto(w, kv.Value);
        }
        w.EndObject();
    }

    private static void WriteTrackDto(PosWriter w, PositionTrackDto dto)
    {
        w.BeginObject();
        w.Name("ms0"); w.Int(dto.Ms0);
        w.Name("dx");  WriteIntArray(w, dto.Dx);
        w.Name("dz");  WriteIntArray(w, dto.Dz);
        w.Name("y");   WriteIntArray(w, dto.Y);
        w.Name("yaw"); WriteIntArray(w, dto.Yaw);
        w.EndObject();
    }

    private static void WriteIntArray(PosWriter w, int[] arr)
    {
        w.BeginArray();
        foreach (var n in arr) w.Int(n);
        w.EndArray();
    }

    private static void WriteMeta(PosWriter w, IReadOnlyDictionary<string, PositionMetaDto> meta)
    {
        w.BeginObject();
        foreach (var kv in meta)
        {
            w.Name(kv.Key);
            WriteMetaDto(w, kv.Value);
        }
        w.EndObject();
    }

    private static void WriteMetaDto(PosWriter w, PositionMetaDto dto)
    {
        w.BeginObject();
        w.Name("kind");         w.Str(dto.Kind);
        w.Name("name");         w.Str(dto.Name);
        w.Name("professionId"); w.Int(dto.ProfessionId);
        w.EndObject();
    }

    // ── minimal JSON writer ───────────────────────────────────────────────────

    /// <summary>Minimal comma-aware JSON emitter (mirrors LogUpload.JsonWriter style).</summary>
    private sealed class PosWriter
    {
        private readonly StringBuilder _sb = new();
        private bool _needComma;

        internal PosWriter BeginObject() { Pre(); _sb.Append('{'); _needComma = false; return this; }
        internal PosWriter EndObject()   { _sb.Append('}'); _needComma = true; return this; }
        internal PosWriter BeginArray()  { Pre(); _sb.Append('['); _needComma = false; return this; }
        internal PosWriter EndArray()    { _sb.Append(']'); _needComma = true; return this; }

        internal PosWriter Name(string key) { Pre(); WriteString(key); _sb.Append(':'); _needComma = false; return this; }
        internal PosWriter Int(int v)       { Pre(); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); _needComma = true; return this; }
        internal PosWriter Str(string v)    { Pre(); WriteString(v); _needComma = true; return this; }

        /// <summary>
        /// Emits a float whose value is a whole number (no fractional part) as a plain integer.
        /// Used for origin coordinates: JS renders <c>0</c> not <c>0.0</c>.
        /// </summary>
        internal PosWriter WholeFloat(float v)
        {
            Pre();
            _sb.Append(((int)v).ToString(CultureInfo.InvariantCulture));
            _needComma = true;
            return this;
        }

        /// <summary>
        /// Emits a float using round-trip format, matching JS number serialization for values
        /// like 0.1 that have a clean decimal representation.
        /// </summary>
        internal PosWriter ScalarFloat(float v)
        {
            Pre();
            _sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
            _needComma = true;
            return this;
        }

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
}
