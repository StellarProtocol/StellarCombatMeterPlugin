// Task 13 (reshape after review): the SIDECAR upload-state format. Per-entry upload state (phase + run
// URL) is persisted OUTSIDE the entry JSON — as a separate "uploadStates" string[] key in the history
// config section — so the entry JSON stays byte-identical to what shipped v10 builds wrote. That keeps a
// rollback to a prior DLL from reading the entries as malformed and wiping the owner's history.
//
// Each record carries its OWN stable composite key (LevelUuid, ArchivedAtMs) — NOT a list index, which
// shifts on eviction/delete. On load, records are matched back to loaded entries by that composite; a
// record with no matching entry (an evicted/deleted run) is simply dropped on the next save (the sidecar
// is rebuilt from the live history). Reflection-free (IL2CPP-safe), reusing the HistoryJson primitives.

using System.Collections.Generic;
using Stellar.CombatMeter.LogUpload;   // UploadPhase

namespace Stellar.CombatMeter;

internal static partial class HistoryStore
{
    /// <summary>A sidecar upload-state record keyed by the stable (LevelUuid, ArchivedAtMs) composite of
    /// the history entry it belongs to.</summary>
    internal readonly record struct UploadStateRecord(long LevelUuid, long ArchivedAtMs, UploadPhase Phase, string? Url);

    // ----- one record -----

    internal static string SerializeUploadState(in UploadStateRecord rec)
    {
        var w = new HistoryJsonWriter();
        w.BeginObject();
        w.Name("luid").Value(rec.LevelUuid);
        w.Name("arch").Value(rec.ArchivedAtMs);
        w.Name("up").Value((int)rec.Phase);
        w.Name("uurl").Value(rec.Url);          // null → empty string
        w.EndObject();
        return w.ToString();
    }

    // Never throws; returns false on any malformed/unsupported shape so the caller skips just that record.
    internal static bool TryDeserializeUploadState(string json, out UploadStateRecord rec)
    {
        rec = default;
        var r = new HistoryJsonReader(json);
        if (r.Next() != JsonTokenKind.ObjectStart) return false;
        long luid = 0, arch = 0; var phase = UploadPhase.Idle; string? url = null;
        var ok = ReadObject(r, key =>
        {
            switch (key)
            {
                case "luid": return ReadLong(r, out luid);
                case "arch": return ReadLong(r, out arch);
                case "up":   if (!ReadInt(r, out var p)) return false; phase = (UploadPhase)p; return true;
                case "uurl": if (!ReadString(r, out var u)) return false; url = string.IsNullOrEmpty(u) ? null : u; return true;
                default:     return SkipValue(r);   // forward-tolerant
            }
        });
        if (!ok) return false;
        rec = new UploadStateRecord(luid, arch, phase, url);
        return true;
    }

    // ----- whole sidecar -----

    /// <summary>Serialize the sidecar for the CURRENT live history: one record per entry carrying a durable
    /// (non-Idle) phase. Idle entries are omitted, so the array only ever holds "uploaded"/"failed" runs and
    /// evicted/deleted runs (absent from <paramref name="live"/>) fall away automatically.</summary>
    internal static string[] SerializeUploadStates(IReadOnlyList<UploadStateRecord> live)
    {
        var list = new List<string>(live.Count);
        foreach (var rec in live)
            if (rec.Phase != UploadPhase.Idle)
                list.Add(SerializeUploadState(rec));
        return list.ToArray();
    }

    /// <summary>Parse a persisted sidecar into a lookup keyed by (LevelUuid, ArchivedAtMs). Malformed records
    /// and Idle records are dropped. The caller matches these against its loaded entries; unmatched records
    /// (orphans of evicted/deleted runs) are simply never applied.</summary>
    internal static Dictionary<(long LevelUuid, long ArchivedAtMs), UploadStateRecord> IndexUploadStates(string[]? sidecar)
    {
        var byKey = new Dictionary<(long, long), UploadStateRecord>();
        if (sidecar is null) return byKey;
        foreach (var s in sidecar)
            if (TryDeserializeUploadState(s, out var rec) && rec.Phase != UploadPhase.Idle)
                byKey[(rec.LevelUuid, rec.ArchivedAtMs)] = rec;
        return byKey;
    }
}
