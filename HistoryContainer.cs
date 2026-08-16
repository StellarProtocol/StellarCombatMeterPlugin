using System;
using System.Collections.Generic;
using System.Text;

namespace Stellar.CombatMeter;

/// <summary>
/// Per-run history persistence (owner ask 2026-08-16). Each archived run is stored as its OWN file under
/// the plugindata <c>history/</c> prefix — next to its <c>replay/</c> file — instead of one big blob in the
/// settings config. That keeps the settings config tiny, so saving a setting no longer re-serializes all of
/// history (the 3-5s freeze). Mirrors <see cref="LogUpload.ReUploadContainer"/>'s naming + orphan-sweep shape.
///
/// Format: <c>&lt;uploadStateJson-or-empty&gt;\n&lt;entryJson&gt;</c>. The upload state comes FIRST (it is small
/// and single-line) and the entry is everything after the first newline, stored RAW — so the entry JSON is
/// never re-escaped and survives byte-for-byte, and even a (theoretical) newline inside the entry is preserved.
/// </summary>
internal static class HistoryContainer
{
    /// <summary>On-disk format version, for any future change to the envelope. The ENTRY JSON keeps its own
    /// independent <c>"v"</c> (HistoryStore.FormatVersion) — this only versions the two-part wrapper.</summary>
    internal const int Version = 1;

    private const string Prefix = "history/";

    internal static string ContainerName(long levelUuid, long archivedAtMs)
        => $"{Prefix}{levelUuid}-{archivedAtMs}.histdoc";

    internal static byte[] Serialize(string entryJson, string? uploadStateJson)
        => Encoding.UTF8.GetBytes((uploadStateJson ?? "") + "\n" + entryJson);

    /// <summary>Splits a container back into its entry JSON + optional upload-state JSON. Never throws;
    /// returns <c>false</c> only when there is no entry payload at all (the caller then skips that file).</summary>
    internal static bool TryDeserialize(byte[]? bytes, out string entryJson, out string? uploadStateJson)
    {
        entryJson = "";
        uploadStateJson = null;
        if (bytes is null || bytes.Length == 0) return false;
        var text = Encoding.UTF8.GetString(bytes);
        var nl = text.IndexOf('\n');
        if (nl < 0)
        {
            // No delimiter (foreign/legacy file) — be lenient: treat the whole thing as the entry.
            entryJson = text;
            return entryJson.Length > 0;
        }
        var up = text.Substring(0, nl);
        entryJson = text.Substring(nl + 1);
        uploadStateJson = up.Length > 0 ? up : null;
        return entryJson.Length > 0;
    }

    /// <summary>Names in <paramref name="existing"/> under <c>history/</c> that no live
    /// (levelUuid, archivedAtMs) key maps to — safe to delete (e.g. a file left by a crash mid-evict).
    /// Non-<c>history/</c> names are ignored.</summary>
    internal static IReadOnlyList<string> OrphanContainerNames(
        IReadOnlyList<string> existing, IReadOnlyList<(long LevelUuid, long ArchivedAtMs)> liveKeys)
    {
        var live = new HashSet<string>();
        foreach (var (l, a) in liveKeys) live.Add(ContainerName(l, a));
        var orphans = new List<string>();
        foreach (var name in existing)
            if (name.StartsWith(Prefix, StringComparison.Ordinal) && !live.Contains(name)) orphans.Add(name);
        return orphans;
    }
}
