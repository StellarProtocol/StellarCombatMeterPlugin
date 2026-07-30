using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.CombatMeter;

// History list GROUPING — one row per run (levelUuid) rather than one per archive, plus the snapshot that
// feeds the list. Split out of Plugin.HistoryWindow.cs: that file was already 569 LoC, over the 500-LoC
// major threshold in docs/coding-standards.md, and the standing rule is that a change must not grow a
// pre-existing violation.
public sealed partial class Plugin
{
    // ----- snapshots -----

    private void RebuildHistorySnapshots()
    {
        _historyView.Clear();
        // Recomputed alongside the view rather than in a separate scan: this runs every shown frame, so the
        // run containing the selection is identified for free while the rows are being built.
        _selectedSegments = Array.Empty<int>();
        // ONE ROW PER RUN, not per archive (owner request 2026-07-30). A single run banks several archives —
        // the fight, then a tail for each selected run-end stage, then the scene-exit tail — so the flat list
        // showed 6 rows for 2 runs and the owner could not tell which row to open. Grouped on levelUuid,
        // which is the runId and is shared by every archive of a run (verified in the owner's log: three
        // archives all carrying levelUuid=584088755955040256).
        foreach (var run in GroupHistoryByRun())
        {
            var primary = _history[run.Primary];
            // REAL elapsed span only — the combat (damage) span lives in the detail pane. The row is
            // capped at HistListWidth (180f) and a measured render showed the combined
            // "8.3s (0s combat)" form truncating mid-parenthetical (owner ruling 2026-07-28, option 1).
            // For a grouped row the span covers the WHOLE run (first archive's start -> last archive's end),
            // because the row now represents the run rather than one of its segments.
            var dur = FormatDurationWithTenths(RealDurationMs(
                _history[run.Segments[0]].EnteredAtMs, _history[run.Segments[^1]].ArchivedAtMs));
            var map = ResolveSceneName(primary.SceneName);
            // The trigger tag is per-ARCHIVE, so it only means something on a single-archive row; a grouped
            // row shows the archive count instead.
            var tail = run.Segments.Length > 1 ? "" : TriggerSuffix(primary.Trigger);
            var count = run.Segments.Length > 1 ? $"  ×{run.Segments.Length}" : "";
            _historyView.Add(new SessionEntry(
                run.Primary,
                FormatSessionClock(primary.ArchivedAtMs) + count,
                $"{map} · {dur} · {primary.MemberCount}p{tail}",
                run.Segments));
            foreach (var seg in run.Segments) if (seg == _historyIndex) _selectedSegments = run.Segments;
        }
        // Keep the selected session in sync (it may have been evicted).
        if (_historyIndex >= 0 && _historyIndex < _history.Count) _selectedSession = _history[_historyIndex];
        else { _selectedSession = null; _historyIndex = -1; _chartedSources.Clear(); _chartSourcesVersion++; }
        RebuildSessionRows();
    }
    /// <summary>One run's archives. <see cref="Segments"/> is oldest-first (so chip 1 is the run's first
    /// archive); <see cref="Primary"/> is the archive the row opens.</summary>
    internal readonly struct RunGroup
    {
        public RunGroup(int primary, int[] segments) { Primary = primary; Segments = segments; }
        public readonly int Primary; public readonly int[] Segments;
    }

    /// <summary>Groups history into runs, newest run first, each run's segments oldest first.
    ///
    /// <para>Key is <c>LevelUuid</c> — the runId every archive of a run shares. A FIELD fight carries
    /// <c>LevelUuid == 0</c> and is deliberately ungroupable: those get a unique negative key so each stays
    /// its own row rather than all collapsing into one bogus "run 0".</para></summary>
    private List<RunGroup> GroupHistoryByRun() => GroupByRun(_history);

    /// <inheritdoc cref="GroupHistoryByRun"/>
    /// <remarks>Pure + static so the grouping pins headless — Plugin cannot be instantiated in a test. The
    /// instance overload above only binds _history, so tests exercise the SAME code the window runs.</remarks>
    internal static List<RunGroup> GroupByRun(IReadOnlyList<EncounterHistoryEntry> history)
    {
        var order = new List<long>();
        var groups = new Dictionary<long, List<int>>();
        for (var i = history.Count - 1; i >= 0; i--)      // newest first, so runs come out newest first
        {
            var uuid = history[i].LevelUuid;
            var key = uuid != 0 ? uuid : -(i + 1);         // field fight: unique key => never grouped
            if (!groups.TryGetValue(key, out var list)) { list = new List<int>(); groups[key] = list; order.Add(key); }
            list.Add(i);
        }
        var runs = new List<RunGroup>(order.Count);
        foreach (var key in order)
        {
            var idxs = groups[key];
            idxs.Reverse();                                // oldest first within the run
            runs.Add(new RunGroup(PrimarySegment(history, idxs), idxs.ToArray()));
        }
        return runs;
    }

    /// <summary>The archive a grouped row opens: the one with the longest COMBAT (damage) span, i.e. the
    /// actual fight rather than a 0ms post-kill tail. Ties keep the earliest, so a run whose archives are all
    /// tails still opens deterministically at its first.</summary>
    internal static int PrimarySegment(IReadOnlyList<EncounterHistoryEntry> history, List<int> segments)
    {
        var best = segments[0];
        foreach (var i in segments)
            if (history[i].CombatDurationMs > history[best].CombatDurationMs) best = i;
        return best;
    }

    /// <summary>Segment chips for the selected run — one per archive, oldest first, so a grouped row can be
    /// drilled into without putting every archive back in the list. Hidden for a single-archive run, where a
    /// lone "1" chip would be noise.
    ///
    /// <para>Chips, not a dropdown: the set is small, and which segment you are on should be readable at a
    /// glance rather than behind a click. Labels are 1-based positions; the archive's own trigger is already
    /// named in the summary line below.</para></summary>
    private HudElement BuildSegmentPicker()
    {
        var kids = new HudElement[MaxSegmentChips + 1];
        kids[0] = new TextElement(() => "segments", MutedCol, Width: 60f);
        for (var i = 0; i < MaxSegmentChips; i++)
        {
            var slot = i;
            kids[slot + 1] = new ConditionalElement(
                () => slot < _selectedSegments.Length,
                new ButtonElement(
                    () => (slot + 1).ToString(),
                    () => { if (slot < _selectedSegments.Length) SelectSession(_selectedSegments[slot]); },
                    Active: () => slot < _selectedSegments.Length && _selectedSegments[slot] == _historyIndex,
                    Width: 28f));
        }
        // Only meaningful when the run actually HAS multiple archives.
        return new ConditionalElement(() => _selectedSegments.Length > 1, new RowElement(kids, Gap: 4f));
    }
}
