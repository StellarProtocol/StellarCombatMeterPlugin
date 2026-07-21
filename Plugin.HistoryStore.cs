using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;   // UploadPhase / UploadStatusTable

namespace Stellar.CombatMeter;

/// <summary>
/// History persistence + clear controls. The encounter history (<c>_history</c>) is serialized one entry per JSON
/// string via <see cref="HistoryStore"/> and stored as a <c>string[]</c> under the <c>history.entries</c> config
/// key — the per-plugin <c>&lt;guid&gt;.config.json</c> in the game dir, human-viewable. Loaded once on construct,
/// re-saved on every archive / eviction / clear (all user- or scene-driven, never per-frame).
///
/// <c>_history</c> is ordered oldest→newest (Add appends, eviction RemoveAt(0)); that order is preserved on disk
/// so the newest-first session list and the cap-50 eviction behave identically across a restart.
/// </summary>
public sealed partial class Plugin
{
    private const string HistoryEntriesKey = "entries";
    // Sidecar: per-entry upload state (phase + run URL) persisted next to "entries" in the SAME history
    // config section. Kept OUT of the entry JSON so entries stay byte-identical to prior (v10) builds — a
    // rollback DLL reads history intact and simply ignores + round-trips this key (PluginConfigService saves
    // the whole config tree, so an unread key is preserved, not dropped).
    private const string HistoryUploadStatesKey = "uploadStates";
    private readonly IConfigSection _historyPrefs;

    // Populate _history from the persisted string[] (entries are oldest→newest). Malformed/legacy entries are
    // skipped silently (HistoryStore.TryDeserializeEntry never throws), and the cap is enforced on load so a
    // hand-edited file with >50 entries can't blow the in-memory bound.
    private void LoadHistory()
    {
        var raw = _historyPrefs.Get<string[]>(HistoryEntriesKey, null);
        if (raw is null || raw.Length == 0) return;

        _history.Clear();
        var skipped = 0;
        foreach (var s in raw)
        {
            if (HistoryStore.TryDeserializeEntry(s, out var entry) && entry is not null) _history.Add(entry);
            else skipped++;
        }
        // Enforce the cap BEFORE hydrating so evicted runs are never rooted by _uploadStatus; the Forget loop
        // matches the archive path (ManualArchive) and stays correct even if hydration order ever changes.
        foreach (var evicted in TrimToCapacity(_history)) { _uploadStatus.Forget(evicted); ForgetReUpload(evicted); }
        HydrateUploadStatesFromSidecar();   // restore "✓ Uploaded" + URL for the surviving entries
        SweepOrphanReUploads();   // belt-and-braces: drop any container left by a crash mid-evict
        if (skipped > 0) _services.Log.Info($"[CombatMeter] history: skipped {skipped} malformed entr{(skipped == 1 ? "y" : "ies")} on load");
    }

    // Delete a run's retained re-upload payload (mirrors _uploadStatus.Forget). No-op when absent.
    private void ForgetReUpload(EncounterHistoryEntry e)
        => _services.Data.Delete(ReUploadContainer.ContainerName(e.LevelUuid, e.ArchivedAtMs));

    // Belt-and-braces: drop any container file with no matching live entry (e.g. left by a crash mid-evict).
    private void SweepOrphanReUploads()
    {
        var live = new List<(long, long)>(_history.Count);
        foreach (var e in _history) live.Add((e.LevelUuid, e.ArchivedAtMs));
        foreach (var name in ReUploadContainer.OrphanContainerNames(_services.Data.List("replay/"), live))
            _services.Data.Delete(name);
    }

    // Match the persisted sidecar records back to the loaded entries by their stable (LevelUuid, ArchivedAtMs)
    // composite and re-root the live upload state into _uploadStatus — this is what restores "✓ Uploaded" + the
    // run URL after a relaunch. Orphaned records (no matching entry — an evicted/deleted run) are never applied
    // and drop away on the next SaveHistory (the sidecar is rebuilt from live _history).
    private void HydrateUploadStatesFromSidecar()
    {
        var byKey = HistoryStore.IndexUploadStates(_historyPrefs.Get<string[]>(HistoryUploadStatesKey, null));
        if (byKey.Count == 0) return;
        foreach (var e in _history)
            if (byKey.TryGetValue((e.LevelUuid, e.ArchivedAtMs), out var rec))
                _uploadStatus.Set(e, rec.Phase, rec.Url);
    }

    // Cap the history to HistoryCapacity, evicting oldest-first (front of the list). Single source of truth for
    // the cap so load and archive evict identically; testable without a live host. Returns the evicted entries
    // (oldest-first) so the caller can drop their upload status — otherwise they'd be rooted by _uploadStatus.
    internal static List<EncounterHistoryEntry> TrimToCapacity(List<EncounterHistoryEntry> history)
    {
        List<EncounterHistoryEntry>? evicted = null;
        while (history.Count > HistoryCapacity)
        {
            (evicted ??= new List<EncounterHistoryEntry>()).Add(history[0]);
            history.RemoveAt(0);
        }
        return evicted ?? EmptyEntries;
    }

    private static readonly List<EncounterHistoryEntry> EmptyEntries = new();

    // Serialize the whole _history list + the upload-state sidecar and persist them. Called after
    // archive/eviction, after any clear, and (via PersistUploadStateIfDirty) once an async upload settles.
    private void SaveHistory()
    {
        var arr = new string[_history.Count];
        for (var i = 0; i < _history.Count; i++) arr[i] = HistoryStore.SerializeEntry(_history[i]);
        _historyPrefs.Set(HistoryEntriesKey, arr);
        _historyPrefs.Set(HistoryUploadStatesKey, BuildUploadStateSidecar());
        _historyPrefs.Save();
    }

    // Gather the sidecar for the CURRENT live history: one record per entry that carries a durable (non-Idle)
    // upload phase, keyed by (LevelUuid, ArchivedAtMs). A transient InFlight collapses to Idle (never
    // persisted). Entries evicted/deleted since the last save aren't in _history, so their records fall away.
    private string[] BuildUploadStateSidecar()
    {
        var live = new List<HistoryStore.UploadStateRecord>(_history.Count);
        foreach (var e in _history)
        {
            var phase = UploadStatusTable.Persistable(_uploadStatus.PhaseFor(e));
            if (phase == UploadPhase.Idle) continue;
            live.Add(new HistoryStore.UploadStateRecord(e.LevelUuid, e.ArchivedAtMs, phase, _uploadStatus.UrlFor(e)));
        }
        return HistoryStore.SerializeUploadStates(live);
    }

    // ----- clear controls -----

    // Wipe all history. Resets the selected session + chart state, persists, refreshes the snapshots so the
    // window reflects the empty state immediately. The Skill Breakdown closes on its own via the stale-session
    // guard (the drilled Session is no longer in _history) on the next RebuildSkillRows.
    internal void ClearAllHistory()
    {
        foreach (var e in _history) ForgetReUpload(e);
        _history.Clear();
        _uploadStatus.Clear();   // drop all per-entry upload status so evicted runs aren't rooted
        ResetHistorySelection();
        SaveHistory();
        RebuildHistorySnapshots();
    }

    // Delete a single session by its _history index. Fixes up the current selection: if the deleted session was
    // selected, clear the selection; otherwise keep the same session selected by tracking its object across the
    // index shift. Then persist + refresh.
    internal void DeleteSession(int historyIndex)
    {
        if (historyIndex < 0 || historyIndex >= _history.Count) return;

        var wasSelected = _selectedSession;
        var deleted = _history[historyIndex];
        _history.RemoveAt(historyIndex);
        _uploadStatus.Forget(deleted);   // drop this run's upload status so it isn't rooted after delete
        ForgetReUpload(deleted);

        if (ReferenceEquals(wasSelected, deleted)) ResetHistorySelection();
        else if (wasSelected is not null)
        {
            // Re-point _historyIndex at the still-selected entry's new position (its index may have shifted down).
            var newIdx = _history.IndexOf(wasSelected);
            if (newIdx >= 0) { _historyIndex = newIdx; _selectedSession = wasSelected; }
            else ResetHistorySelection();
        }

        SaveHistory();
        RebuildHistorySnapshots();   // newest-first list + stale-session guard for the breakdown
    }

    private void ResetHistorySelection()
    {
        _selectedSession = null;
        _historyIndex = -1;
        _chartedSources.Clear();
        _chartSourcesVersion++;
    }
}
