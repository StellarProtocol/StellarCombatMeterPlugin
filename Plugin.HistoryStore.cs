using System.Collections.Generic;
using Stellar.Abstractions.Services;

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
        TrimToCapacity(_history);
        if (skipped > 0) _services.Log.Info($"[CombatMeter] history: skipped {skipped} malformed entr{(skipped == 1 ? "y" : "ies")} on load");
    }

    // Cap the history to HistoryCapacity, evicting oldest-first (front of the list). Single source of truth for
    // the cap so load and archive evict identically; testable without a live host.
    internal static void TrimToCapacity(List<EncounterHistoryEntry> history)
    {
        while (history.Count > HistoryCapacity) history.RemoveAt(0);
    }

    // Serialize the whole _history list and persist it. Called after archive/eviction and after any clear.
    private void SaveHistory()
    {
        var arr = new string[_history.Count];
        for (var i = 0; i < _history.Count; i++) arr[i] = HistoryStore.SerializeEntry(_history[i]);
        _historyPrefs.Set(HistoryEntriesKey, arr);
        _historyPrefs.Save();
    }

    // ----- clear controls -----

    // Wipe all history. Resets the selected session + chart state, persists, refreshes the snapshots so the
    // window reflects the empty state immediately. The Skill Breakdown closes on its own via the stale-session
    // guard (the drilled Session is no longer in _history) on the next RebuildSkillRows.
    internal void ClearAllHistory()
    {
        _history.Clear();
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
