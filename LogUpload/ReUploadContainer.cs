using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>The exact upload bodies of a first send, retained for a byte-for-byte re-upload.
/// <paramref name="Chunks"/> holds inlined event ENVELOPES (container V1 — builds before the rDPS spool);
/// <paramref name="ChunkRefs"/> points at the <c>spool/*</c> blobs the events already live in (V2, from
/// 2026-09-05). A container carries one or the other; a manual push re-sends whichever it has.</summary>
internal sealed record ReUploadPayload(
    int V, string Region, long LevelUuid, string LogId,
    string Summary, IReadOnlyList<string> Chunks, string? Positions,
    IReadOnlyList<SpoolChunkRef> ChunkRefs);

/// <summary>
/// Gzipped JSON container for a run's retained upload payloads. Reflection-free (IL2CPP-safe),
/// reusing the HistoryJson primitives (<see cref="HistoryJsonWriter"/> / <see cref="HistoryJsonReader"/>)
/// and the <see cref="HistoryStore"/> reader helpers. The <c>summary</c>/<c>chunks[]</c>/<c>positions</c>
/// values are the already-serialized upload bodies, stored (and read back) verbatim — this container never
/// re-encodes or reinterprets them, so a re-upload can reproduce the first send byte-for-byte.
/// </summary>
internal static class ReUploadContainer
{
    // Written on every Serialize; round-tripped verbatim by TryDeserialize. No read-side gate on this
    // value yet — an older reader simply skips any newer/unknown key additively (see SkipValue below),
    // so this is a marker for a future format check, not an enforced one today.
    // 2 (2026-09-05, rDPS spool): chunk REFS replace inlined envelopes. V1 containers still read.
    internal const int Version = 2;

    // How much of a container ReferencedBlobs decompresses before falling back to a full read. Comfortably
    // larger than a maximal ref list (2 tracks × 128 chunks × ~90 B of JSON).
    private const int RefsHeadBytes = 64 * 1024;

    internal static string ContainerName(long levelUuid, long archivedAtMs)
        => $"replay/{levelUuid}-{archivedAtMs}.replaydoc";

    /// <summary>Names in <paramref name="existing"/> under <c>replay/</c> that no live (levelUuid, archivedAtMs)
    /// key maps to — safe to delete. Non-<c>replay/</c> names are ignored.</summary>
    internal static IReadOnlyList<string> OrphanContainerNames(
        IReadOnlyList<string> existing, IEnumerable<(long LevelUuid, long ArchivedAtMs)> liveKeys)
    {
        var live = new HashSet<string>();
        foreach (var (l, a) in liveKeys) live.Add(ContainerName(l, a));
        var orphans = new List<string>();
        foreach (var name in existing)
            if (name.StartsWith("replay/", System.StringComparison.Ordinal) && !live.Contains(name)) orphans.Add(name);
        return orphans;
    }

    internal static byte[] Serialize(ReUploadPayload p)
    {
        var w = new HistoryJsonWriter();
        w.BeginObject();
        w.Name("v").Value(p.V);
        // chunkRefs is written FIRST (right after the version) on purpose: the startup blob sweep reads only
        // this key, so ReferencedBlobs can decompress a bounded HEAD of the container and stop, instead of
        // gunzipping + tokenizing every retained run's megabyte-scale summary/positions bodies.
        w.Name("chunkRefs").BeginArray();
        foreach (var r in p.ChunkRefs)
        {
            w.BeginObject();
            w.Name("track").Value(r.Track);
            w.Name("index").Value(r.Index);
            w.Name("startMs").Value(r.StartMs);
            w.Name("endMs").Value(r.EndMs);
            w.Name("count").Value(r.Count);
            w.Name("blob").Value(r.BlobName);
            w.EndObject();
        }
        w.EndArray();
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
        var chunkRefs = System.Array.Empty<SpoolChunkRef>();

        var ok = HistoryStore.ReadObject(r, key =>
        {
            switch (key)
            {
                case "v":         return HistoryStore.ReadInt(r, out v);
                case "chunkRefs": return ReadChunkRefs(r, out chunkRefs);   // absent (V1) → stays empty
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
            hasPos == 0 ? null : positions, chunkRefs);
        return true;
    }

    /// <summary>Reads the <c>chunkRefs</c> array (an array of OBJECTS — <see cref="HistoryStore.ReadArray"/>,
    /// which consumes each element's ObjectStart itself). Unknown per-ref keys are skipped additively.</summary>
    private static bool ReadChunkRefs(HistoryJsonReader r, out SpoolChunkRef[] refs)
    {
        refs = System.Array.Empty<SpoolChunkRef>();
        var list = new List<SpoolChunkRef>();
        var ok = HistoryStore.ReadArray(r, () =>
        {
            string? track = null, blob = null;
            int index = 0, count = 0;
            long startMs = 0, endMs = 0;
            if (!HistoryStore.ReadObject(r, key =>
            {
                switch (key)
                {
                    case "track":   return HistoryStore.ReadString(r, out track);
                    case "index":   return HistoryStore.ReadInt(r, out index);
                    case "startMs": return HistoryStore.ReadLong(r, out startMs);
                    case "endMs":   return HistoryStore.ReadLong(r, out endMs);
                    case "count":   return HistoryStore.ReadInt(r, out count);
                    case "blob":    return HistoryStore.ReadString(r, out blob);
                    default:        return HistoryStore.SkipValue(r);
                }
            })) return false;
            list.Add(new SpoolChunkRef(track ?? "", index, startMs, endMs, count, blob ?? ""));
            return true;
        });
        if (!ok) return false;
        refs = list.ToArray();
        return true;
    }

    /// <summary>The <c>spool/*</c> blob names this container's chunk refs point at — the startup sweep's input
    /// (<c>Plugin.HistoryStore.SweepOrphanReUploads</c>). Cheap BY CONSTRUCTION: <see cref="Serialize"/> writes
    /// <c>chunkRefs</c> first, so this normally decompresses only a bounded head and stops. A container whose
    /// head does not yield a COMPLETE ref array (V1, a huge list, a future format that moved the key) falls
    /// back to the full read — never to a PARTIAL list, which would make the sweep delete LIVE blobs.</summary>
    internal static IReadOnlyList<string> ReferencedBlobs(byte[] gz)
    {
        if (TryReadRefsFromHead(gz, out var head)) return head;
        return TryDeserialize(gz, out var p) ? BlobNames(p.ChunkRefs) : System.Array.Empty<string>();
    }

    private static string[] BlobNames(IReadOnlyList<SpoolChunkRef> refs)
    {
        var names = new string[refs.Count];
        for (var i = 0; i < names.Length; i++) names[i] = refs[i].BlobName;
        return names;
    }

    // Parse just enough of the container's decompressed HEAD to read `chunkRefs`. Returns false — meaning
    // "ask for the full read" — the moment anything is unreadable (a value truncated by the head cut always
    // lands here: its array/object can no longer close, and the reader reports Error/Eof).
    private static bool TryReadRefsFromHead(byte[] gz, out string[] names)
    {
        names = System.Array.Empty<string>();
        string head;
        try { head = GunzipHead(gz, RefsHeadBytes); }
        catch { return false; }

        var r = new HistoryJsonReader(head);
        if (r.Next() != JsonTokenKind.ObjectStart) return false;
        var refs = System.Array.Empty<SpoolChunkRef>();
        var version = -1;
        var found = false;
        var ok = HistoryStore.ReadObject(r, key =>
        {
            if (key == "v") return HistoryStore.ReadInt(r, out version);
            if (key == "chunkRefs")
            {
                if (!ReadChunkRefs(r, out refs)) return false;
                found = true;
                return false;   // stop the walk: everything after this key is the big bodies
            }
            // Serialize writes v then chunkRefs, so any OTHER key on a pre-refs version proves there are
            // none — stop instead of skimming a megabyte of summary/positions to learn the same thing.
            if (version >= 0 && version < 2) { found = true; return false; }
            return HistoryStore.SkipValue(r);
        });
        // ok==true means the object CLOSED cleanly inside the head — "no refs" is then the honest answer.
        // Otherwise only a `found` stop counts; a genuine parse failure (incl. a head cut mid-value) falls back.
        if (!found && !ok) return false;
        names = BlobNames(refs);
        return true;
    }

    private static string GunzipHead(byte[] gz, int max)
    {
        using var ms = new MemoryStream(gz);
        using var gzs = new GZipStream(ms, CompressionMode.Decompress);
        var buf = new byte[max];
        var read = 0;
        while (read < max)
        {
            var n = gzs.Read(buf, read, max - read);
            if (n <= 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buf, 0, read);
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
