using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;   // UploadPhase / UploadStatusTable

namespace Stellar.CombatMeter;

/// <summary>
/// History persistence + clear controls. Each archived run is persisted as its OWN file under the plugindata
/// <c>history/</c> prefix — <c>history/&lt;levelUuid&gt;-&lt;archivedAtMs&gt;.histdoc</c>, next to its
/// <c>replay/</c> file (owner ask 2026-08-16). The file holds the entry JSON (<see cref="HistoryStore.SerializeEntry"/>)
/// plus that run's upload state, via <see cref="HistoryContainer"/>. This replaced the old model of one
/// <c>string[]</c> under the config <c>history.entries</c> key: that put ~2.8 MB of history in the SAME file as
/// settings, so every settings save re-serialized all of it on the main thread (a 3-5s freeze). Per-run files
/// mean a settings save touches only the tiny settings section, and archiving/upload-state writes touch only
/// the one run's file.
///
/// A one-time migration (in <see cref="LoadHistory"/>) reads any surviving config <c>history.entries</c>,
/// writes the per-run files, and clears the config section — merge-based, so it is crash-safe and re-syncs a
/// run archived by a rolled-back (pre-per-run) build. <c>_history</c> stays ordered oldest→newest so the
/// newest-first list and the cap eviction behave identically across a restart.
/// </summary>
public sealed partial class Plugin
{
    private const string HistoryEntriesKey = "entries";
    // Legacy sidecar key (pre-per-run): per-entry upload state stored next to "entries" in the config
    // history section. Only READ now — during migration — then the whole section is cleared.
    private const string HistoryUploadStatesKey = "uploadStates";
    private readonly IConfigSection _historyPrefs;

    // Populate _history from the per-run history files (+ a one-time merge from the legacy config section).
    // Malformed/legacy files are skipped silently (never throw); the cap is enforced on load so a stray
    // over-cap set can't blow the in-memory bound.
    private readonly struct LoadedRun
    {
        internal LoadedRun(EncounterHistoryEntry entry, HistoryStore.UploadStateRecord? up) { Entry = entry; Up = up; }
        internal EncounterHistoryEntry Entry { get; }
        internal HistoryStore.UploadStateRecord? Up { get; }
    }

    private void LoadHistory()
    {
        var byKey = ReadPerRunHistoryFiles(out var skipped);
        var configHadEntries = MergeLegacyConfigHistory(byKey, ref skipped);

        // Build _history oldest→newest (List()/dictionary order is not guaranteed), then enforce the cap
        // BEFORE hydrating so evicted runs are never rooted by _uploadStatus.
        var all = new List<LoadedRun>(byKey.Values);
        all.Sort((x, y) => x.Entry.ArchivedAtMs.CompareTo(y.Entry.ArchivedAtMs));
        _history.Clear();
        foreach (var r in all) _history.Add(r.Entry);
        foreach (var evicted in TrimToCapacity(_history, HistoryRetention))
        { _uploadStatus.Forget(evicted); ForgetReUpload(evicted); DeleteHistoryFile(evicted); }

        // Re-root upload state for the survivors — restores "✓ Uploaded" + URL after a relaunch.
        foreach (var r in all)
            if (r.Up is { } rec && _history.Contains(r.Entry)) _uploadStatus.Set(r.Entry, rec.Phase, rec.Url);

        // If the config carried history, guarantee every surviving run has a per-run file, THEN clear the
        // config section (so settings saves stop re-serializing it). Files first, so a crash never loses
        // data — next load re-merges from config.
        if (configHadEntries)
        {
            foreach (var e in _history) WriteHistoryFile(e);
            ClearConfigHistorySection();
            _services.Log.Info($"[CombatMeter] history: migrated {_history.Count} run(s) to per-run files; config history section cleared.");
        }

        SweepOrphanReUploads();
        SweepOrphanHistoryFiles();
        if (skipped > 0) _services.Log.Info($"[CombatMeter] history: skipped {skipped} malformed entr{(skipped == 1 ? "y" : "ies")} on load");
    }

    // Per-run files — the store of record. Keyed by (LevelUuid, ArchivedAtMs) so the config merge can't
    // double-add a run a file already holds. Malformed files bump `skipped` and are dropped.
    private Dictionary<(long, long), LoadedRun> ReadPerRunHistoryFiles(out int skipped)
    {
        var byKey = new Dictionary<(long, long), LoadedRun>();
        skipped = 0;
        foreach (var name in _services.Data.List("history/"))
        {
            var bytes = _services.Data.Read(name);
            if (bytes is null || !HistoryContainer.TryDeserialize(bytes, out var entryJson, out var upJson)) { skipped++; continue; }
            if (!HistoryStore.TryDeserializeEntry(entryJson, out var entry) || entry is null) { skipped++; continue; }
            HistoryStore.UploadStateRecord? up =
                upJson != null && HistoryStore.TryDeserializeUploadState(upJson, out var rec) ? rec : null;
            byKey[(entry.LevelUuid, entry.ArchivedAtMs)] = new LoadedRun(entry, up);
        }
        return byKey;
    }

    // Merge any legacy config entries the per-run files don't already have. First run: files are empty, so
    // everything comes from here (migration). After a rollback to a pre-per-run build that archived a run:
    // that run is in the config and gets re-synced into a file by the caller. Returns whether the config
    // carried any entries (⇒ the caller writes files + clears the section).
    private bool MergeLegacyConfigHistory(Dictionary<(long, long), LoadedRun> byKey, ref int skipped)
    {
        var configRaw = _historyPrefs.Get<string[]>(HistoryEntriesKey, null);
        if (configRaw is not { Length: > 0 }) return false;
        var upIdx = HistoryStore.IndexUploadStates(_historyPrefs.Get<string[]>(HistoryUploadStatesKey, null));
        foreach (var s in configRaw)
        {
            if (!HistoryStore.TryDeserializeEntry(s, out var entry) || entry is null) { skipped++; continue; }
            var key = (entry.LevelUuid, entry.ArchivedAtMs);
            if (byKey.ContainsKey(key)) continue;
            byKey[key] = new LoadedRun(entry, upIdx.TryGetValue(key, out var rec) ? rec : (HistoryStore.UploadStateRecord?)null);
        }
        return true;
    }

    // Delete a run's retained re-upload payload AND the spool blobs it referenced (mirrors _uploadStatus.Forget).
    // No-op when absent. Blob lifetime = CONTAINER lifetime (see the comment in SweepOrphanReUploads below) —
    // this is the SECOND of the two sites that ever delete a container, so it must free the container's blobs
    // itself rather than leaving them for a sweep that may not run again until the next launch.
    private void ForgetReUpload(EncounterHistoryEntry e)
    {
        var name = ReUploadContainer.ContainerName(e.LevelUuid, e.ArchivedAtMs);
        var bytes = _services.Data.Read(name);
        if (bytes is not null)
            foreach (var blob in ReUploadContainer.ReferencedBlobs(bytes)) _services.Data.Delete(blob);
        _services.Data.Delete(name);
    }

    // Belt-and-braces: drop any replay container with no matching live entry (e.g. left by a crash mid-evict),
    // and with it the spool blobs it referenced. STARTUP ONLY (LoadHistory) — see SweepUnreferencedSpoolBlobs.
    private void SweepOrphanReUploads()
    {
        var live = new List<(long, long)>(_history.Count);
        foreach (var e in _history) live.Add((e.LevelUuid, e.ArchivedAtMs));
        foreach (var name in ReUploadContainer.OrphanContainerNames(_services.Data.List("replay/"), live))
        {
            // Blob lifetime = CONTAINER lifetime: a segment's blobs back its re-upload, so they are never
            // deleted on upload success — only at the TWO sites that ever delete a container: here (the
            // startup orphan sweep, for a container whose in-memory entry is already gone) and ForgetReUpload
            // above (an in-memory-tracked entry's container being deleted directly — eviction, DeleteSession,
            // ClearAllHistory). Whichever site reaches a container first frees its blobs with it.
            var bytes = _services.Data.Read(name);
            if (bytes is not null)
                foreach (var blob in ReUploadContainer.ReferencedBlobs(bytes)) _services.Data.Delete(blob);
            _services.Data.Delete(name);
        }
        SweepUnreferencedSpoolBlobs();
    }

    // A crash between a spool blob's write and the archive that would have referenced it leaves a blob no
    // container owns. STARTUP ONLY (reached from LoadHistory, before OnCombatEvent is ever wired) — mid-run
    // this would delete the LIVE segment's blobs, which no container references YET.
    //
    // The delete decision itself is SpoolSweep.Plan (pure, pinned): an incomplete reference set must never
    // authorize a delete, so one unreadable live container skips the sweep entirely for this launch.
    private void SweepUnreferencedSpoolBlobs()
    {
        var blobs = _services.Data.List(SpoolCodec.Prefix);
        if (blobs.Count == 0) return;

        var (toDelete, skip) = SpoolSweep.Plan(blobs, LiveReUploadContainers());
        if (skip is not null)
        {
            _services.Log.Warning(
                $"[CombatMeter.SP1] spool sweep skipped this launch: container {skip} unreadable — leftovers cost disk only");
            return;
        }

        foreach (var blob in toDelete) _services.Data.Delete(blob);
        if (toDelete.Count > 0)
            _services.Log.Info($"[CombatMeter.SP1] Deleted {toDelete.Count} unreferenced spool blob(s) (run never archived).");
    }

    // Lazy so only ONE container's bytes are live at a time (94 containers × ~190 KB on the owner's client).
    // Orphans were already deleted by the caller, so every name here belongs to a live history entry.
    private IEnumerable<(string Name, byte[]? Bytes)> LiveReUploadContainers()
    {
        foreach (var name in _services.Data.List("replay/"))
            yield return (name, _services.Data.Read(name));
    }

    // Same, for per-run history files: drop any history/ file with no live entry (left by a crash mid-evict).
    private void SweepOrphanHistoryFiles()
    {
        var live = new List<(long, long)>(_history.Count);
        foreach (var e in _history) live.Add((e.LevelUuid, e.ArchivedAtMs));
        foreach (var name in HistoryContainer.OrphanContainerNames(_services.Data.List("history/"), live))
            _services.Data.Delete(name);
    }

    // Write ONE run's history file: its entry JSON + (when durable) its upload state, folded into the same
    // per-run file. A transient InFlight collapses to Idle (never persisted, matching the old sidecar rule).
    private void WriteHistoryFile(EncounterHistoryEntry e)
    {
        var entryJson = HistoryStore.SerializeEntry(e);
        var phase = UploadStatusTable.Persistable(_uploadStatus.PhaseFor(e));
        string? upJson = phase == UploadPhase.Idle
            ? null
            : HistoryStore.SerializeUploadState(new HistoryStore.UploadStateRecord(e.LevelUuid, e.ArchivedAtMs, phase, _uploadStatus.UrlFor(e)));
        _services.Data.Write(HistoryContainer.ContainerName(e.LevelUuid, e.ArchivedAtMs), HistoryContainer.Serialize(entryJson, upJson));
    }

    private void DeleteHistoryFile(EncounterHistoryEntry e)
        => _services.Data.Delete(HistoryContainer.ContainerName(e.LevelUuid, e.ArchivedAtMs));

    // Empty the legacy config history section (one final write) — after migration, so settings saves no
    // longer re-serialize megabytes of history. Left as empty arrays rather than removed keys for clarity.
    private void ClearConfigHistorySection()
    {
        _historyPrefs.Set(HistoryEntriesKey, System.Array.Empty<string>());
        _historyPrefs.Set(HistoryUploadStatesKey, System.Array.Empty<string>());
        _historyPrefs.Save();
    }

    // Local-history retention (owner 2026-08-15): a SETTING, not a fixed cap. Clamped to [Min,Max] — Min is
    // the old fixed cap (never fewer), Max is the config-size ceiling. With per-run files this no longer
    // bounds one giant config blob, but the in-memory list + slot pool are still sized to it.
    internal const int MinRetention = 50;
    internal const int MaxRetention = 250;
    internal const int DefaultRetention = 100;
    private const string PrefHistoryRetention = "history.retention";

    /// <summary>Clamp a requested retention to <see cref="MinRetention"/>..<see cref="MaxRetention"/>.
    /// Pure — pinned by HistorySearchAndRetentionTests.</summary>
    internal static int ClampRetention(int value)
        => value < MinRetention ? MinRetention : value > MaxRetention ? MaxRetention : value;

    /// <summary>The current retention (how many past archives the local list keeps), read from prefs and
    /// clamped. Setter persists.</summary>
    internal int HistoryRetention
    {
        get => ClampRetention(_prefs.Get(PrefHistoryRetention, DefaultRetention));
        set { _prefs.Set(PrefHistoryRetention, ClampRetention(value)); _prefs.Save(); }
    }

    // Cap the history to `capacity`, evicting oldest-first (front of the list). Single source of truth for
    // the cap so load and archive evict identically; testable without a live host. Returns the evicted entries
    // (oldest-first) so the caller can drop their upload status + per-run files.
    internal static List<EncounterHistoryEntry> TrimToCapacity(List<EncounterHistoryEntry> history, int capacity)
    {
        List<EncounterHistoryEntry>? evicted = null;
        while (history.Count > capacity)
        {
            (evicted ??= new List<EncounterHistoryEntry>()).Add(history[0]);
            history.RemoveAt(0);
        }
        return evicted ?? EmptyEntries;
    }

    private static readonly List<EncounterHistoryEntry> EmptyEntries = new();

    /// <summary>The history-list search filter: true when <paramref name="query"/> (trimmed) is empty, or a
    /// case-insensitive substring of <paramref name="searchableText"/> (a run row's "mapName verdict clock").
    /// Pure — pinned by HistorySearchAndRetentionTests.</summary>
    internal static bool HistoryRowMatches(string searchableText, string query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return searchableText.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // ----- clear controls -----

    // Wipe all history: delete every per-run file, clear in-memory + upload status, refresh the window.
    internal void ClearAllHistory()
    {
        foreach (var e in _history) { ForgetReUpload(e); DeleteHistoryFile(e); }
        _history.Clear();
        _uploadStatus.Clear();
        ResetHistorySelection();
        SweepOrphanHistoryFiles();   // drop any file a prior crash left behind
        RebuildHistorySnapshots();
    }

    // Delete a single session by its _history index. Fixes up the current selection, deletes that run's
    // per-run file + replay, then refreshes.
    internal void DeleteSession(int historyIndex)
    {
        if (historyIndex < 0 || historyIndex >= _history.Count) return;

        var wasSelected = _selectedSession;
        var deleted = _history[historyIndex];
        _history.RemoveAt(historyIndex);
        _uploadStatus.Forget(deleted);
        ForgetReUpload(deleted);
        DeleteHistoryFile(deleted);

        if (ReferenceEquals(wasSelected, deleted)) ResetHistorySelection();
        else if (wasSelected is not null)
        {
            var newIdx = _history.IndexOf(wasSelected);
            if (newIdx >= 0) { _historyIndex = newIdx; _selectedSession = wasSelected; }
            else ResetHistorySelection();
        }

        RebuildHistorySnapshots();
    }

    private void ResetHistorySelection()
    {
        _selectedSession = null;
        _historyIndex = -1;
        _chartedSources.Clear();
        _chartSourcesVersion++;
    }
}
