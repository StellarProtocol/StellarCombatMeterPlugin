using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>The exact upload bodies of a first send, retained for a byte-for-byte re-upload.</summary>
internal sealed record ReUploadPayload(
    int V, string Region, long LevelUuid, string LogId,
    string Summary, IReadOnlyList<string> Chunks, string? Positions);

/// <summary>
/// Gzipped JSON container for a run's retained upload payloads. Reflection-free (IL2CPP-safe),
/// reusing the HistoryJson primitives (<see cref="HistoryJsonWriter"/> / <see cref="HistoryJsonReader"/>)
/// and the <see cref="HistoryStore"/> reader helpers. The <c>summary</c>/<c>chunks[]</c>/<c>positions</c>
/// values are the already-serialized upload bodies, stored (and read back) verbatim — this container never
/// re-encodes or reinterprets them, so a re-upload can reproduce the first send byte-for-byte.
/// </summary>
internal static class ReUploadContainer
{
    internal const int Version = 1;

    internal static string ContainerName(long levelUuid, long archivedAtMs)
        => $"replay/{levelUuid}-{archivedAtMs}.replaydoc";

    internal static byte[] Serialize(ReUploadPayload p)
    {
        var w = new HistoryJsonWriter();
        w.BeginObject();
        w.Name("v").Value(p.V);
        w.Name("region").Value(p.Region);
        w.Name("luid").Value(p.LevelUuid);
        w.Name("logId").Value(p.LogId);
        w.Name("summary").Value(p.Summary);
        w.Name("chunks").BeginArray();
        foreach (var c in p.Chunks) w.Value(c);
        w.EndArray();
        w.Name("positions").Value(p.Positions);   // null -> "" ; distinguished on read via "hasPos"
        w.Name("hasPos").Value(p.Positions is null ? 0 : 1);
        w.EndObject();
        return Gzip(w.ToString());
    }

    /// <summary>
    /// Never throws — any malformed/corrupt/foreign-format input yields <c>false</c> so a bad container
    /// (or a rolled-back-format reader) simply skips the re-upload rather than crashing.
    /// </summary>
    internal static bool TryDeserialize(byte[] gz, out ReUploadPayload payload)
    {
        payload = default!;
        string json;
        try { json = Gunzip(gz); }
        catch { return false; }

        var r = new HistoryJsonReader(json);
        if (r.Next() != JsonTokenKind.ObjectStart) return false;

        int v = 0, hasPos = 1;
        long luid = 0;
        string? region = null, logId = null, summary = null, positions = null;
        string[] chunks = System.Array.Empty<string>();

        var ok = HistoryStore.ReadObject(r, key =>
        {
            switch (key)
            {
                case "v":         return HistoryStore.ReadInt(r, out v);
                case "region":    return HistoryStore.ReadString(r, out region);
                case "luid":      return HistoryStore.ReadLong(r, out luid);
                case "logId":     return HistoryStore.ReadString(r, out logId);
                case "summary":   return HistoryStore.ReadString(r, out summary);
                case "positions": return HistoryStore.ReadString(r, out positions);
                case "hasPos":    return HistoryStore.ReadInt(r, out hasPos);
                // "chunks" is a flat array of raw JSON-body STRINGS, not objects — ReadStringArray (a
                // sibling of ReadLongArray/ReadIntArray/ReadFloatArray), not ReadArray (which is only for
                // arrays-of-objects: stats/series/entities each start with an ObjectStart it eats itself).
                case "chunks":    return HistoryStore.ReadStringArray(r, out chunks);
                default:          return HistoryStore.SkipValue(r);
            }
        });
        if (!ok) return false;

        payload = new ReUploadPayload(
            v, region ?? "", luid, logId ?? "", summary ?? "", chunks,
            hasPos == 0 ? null : positions);
        return true;
    }

    private static byte[] Gzip(string s)
    {
        var raw = Encoding.UTF8.GetBytes(s);
        using var ms = new MemoryStream(raw.Length);
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static string Gunzip(byte[] gz)
    {
        using var ms = new MemoryStream(gz);
        using var gzs = new GZipStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        gzs.CopyTo(outMs);
        return Encoding.UTF8.GetString(outMs.ToArray());
    }
}
