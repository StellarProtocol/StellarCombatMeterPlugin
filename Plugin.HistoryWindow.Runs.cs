using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;   // UploadPhase

namespace Stellar.CombatMeter;

// History list GROUPING — one row per run (levelUuid) rather than one per archive, plus the snapshot that
// feeds the list. Split out of Plugin.HistoryWindow.cs (500-LoC cap).
public sealed partial class Plugin
{
    // ----- snapshots -----

    // Dirty key of the last view rebuild. The window ticks this at ~10 Hz while shown, but the history only changes on an
    // archive / delete / search / selection / language change — so the grouping + every row string are rebuilt
    // only then, never per tick (Plugin-Performance-Rules §2: no per-poll allocation).
    private int _hvCount = -1, _hvIndex = -2;
    private EncounterHistoryEntry? _hvFirst, _hvLast;
    private string? _hvSearch, _hvLang;

    private void RebuildHistorySnapshots()
    {
        RefreshUploadBlock();   // cheap; the upload policy can change in Settings while History is open
        RefreshRunUploadState(force: false);   // cheap poll; re-composes the status only when an upload phase changed
        var first = _history.Count > 0 ? _history[0] : null;
        var last  = _history.Count > 0 ? _history[^1] : null;
        string lang = _loc.Language;
        if (_history.Count == _hvCount && ReferenceEquals(first, _hvFirst) && ReferenceEquals(last, _hvLast)
            && ReferenceEquals(_historySearch, _hvSearch) && _historyIndex == _hvIndex
            && ReferenceEquals(lang, _hvLang))   // the metric is NOT a key: no run-list string depends on it (SelectHistoryMetric)
            return;

        _historyView.Clear();
        _selectedSegments = Array.Empty<int>();
        var runs = GroupHistoryByRun();
        _historyRunCount = runs.Count;
        string clockFmt = _loc.T("history.clock.format");
        // ONE ROW PER RUN, not per archive (owner request 2026-07-30). A single run banks several archives —
        // the fight, then a tail for each selected run-end stage, then the scene-exit tail — so the flat list
        // showed 6 rows for 2 runs and the owner could not tell which row to open. Grouped on levelUuid,
        // which is the runId and is shared by every archive of a run (verified in the owner's log: three
        // archives all carrying levelUuid=584088755955040256).
        foreach (var run in runs)
        {
            // The selected run's segments are found over ALL runs, so a search that hides the selected row never hides
            // its segment cards.
            foreach (var seg in run.Segments) if (seg == _historyIndex) _selectedSegments = run.Segments;
            var primary = _history[run.Primary];
            // REAL elapsed span only — the combat (damage) span lives in the run panel. For a grouped row the span
            // covers the WHOLE run (first archive's start -> last archive's end), because the row represents the run.
            var dur = FormatDurationWithTenths(RealDurationMs(
                _history[run.Segments[0]].EnteredAtMs, _history[run.Segments[^1]].ArchivedAtMs));
            var map = ResolveSceneName(primary.SceneName);
            var clock = primary.ArchivedAtMs <= 0 ? "—"
                : DateTimeOffset.FromUnixTimeMilliseconds(primary.ArchivedAtMs).LocalDateTime.ToString(clockFmt, CultureInfo.InvariantCulture);
            // Search filter (owner 2026-08-15): match the query against "mapName verdict clock" (both the shown clock and
            // the old compact "2:14p" form). A filtered-out row is simply not added; the selected run's detail still
            // shows, so a search never deselects — it only narrows the list.
            if (!HistoryRowMatches($"{map} {primary.Result} {clock} {FormatSessionClock(primary.ArchivedAtMs)}", _historySearch))
                continue;
            // The trigger tag is per-ARCHIVE, so it only means something on a single-archive row (readable name, e.g.
            // "· Boss kill"); a grouped row shows the archive count "×N" beside the time instead.
            var tail = run.Segments.Length > 1 || TriggerSuffix(primary.Trigger).Length == 0 ? "" : " · " + SegmentLabel(primary.Trigger);
            var count = run.Segments.Length > 1 ? "×" + run.Segments.Length.ToString(CultureInfo.InvariantCulture) : "";
            _historyView.Add(new SessionEntry(run.Primary, clock, count,
                map + " · " + dur + " · " + _loc.TFormat("history.run.players", primary.MemberCount) + tail,
                run.Segments));
        }
        // Keep the selected session in sync (it may have been evicted).
        if (_historyIndex >= 0 && _historyIndex < _history.Count) _selectedSession = _history[_historyIndex];
        else { _selectedSession = null; _historyIndex = -1; _chartedSources.Clear(); _chartSourcesVersion++; }

        // Nothing selected (first open, or the selection was deleted) → open the newest run, so the right column always
        // shows a run instead of an empty frame.
        if (_selectedSession is null && _historyView.Count > 0)
        {
            _selectedSegments = _historyView[0].Segments;
            SelectSession(_historyView[0].Index);
        }
        else RebuildSessionRows();

        _hvCount = _history.Count; _hvFirst = first; _hvLast = last; _hvSearch = _historySearch;
        _hvIndex = _historyIndex; _hvLang = lang;
    }

    // Live history search (owner 2026-08-15): filters the grouped run rows by map/verdict/clock as-you-type (the next
    // ~10 Hz snapshot tick sees the new string reference and rebuilds the list).
    private string _historySearch = "";

    // Resolve a stored scene token (a raw id string like "7") to a friendly map/dungeon name via the live game
    // World table. Falls back to "Scene {id}" for an unknown numeric id, or the raw token when non-numeric/empty.
    private string ResolveSceneName(string? sceneToken)
    {
        if (string.IsNullOrEmpty(sceneToken)) return _loc.T("common.unknown");
        if (!int.TryParse(sceneToken, out var id)) return sceneToken;
        var name = _services.GameData.World.GetScene(id)?.Name;
        return string.IsNullOrEmpty(name) ? _loc.TFormat("history.scene.numbered", id) : name;
    }

    /// <summary>One run's archives. <see cref="Segments"/> is oldest-first (so card 1 is the run's first
    /// archive); <see cref="Primary"/> is the archive the row opens.</summary>
    internal readonly struct RunGroup
    {
        public RunGroup(int primary, int[] segments) { Primary = primary; Segments = segments; }
        public readonly int Primary; public readonly int[] Segments;
    }

    /// <summary>Groups history into runs, newest run first, each run's segments oldest first.
    ///
    /// <para>Key is <c>(LevelUuid, DungeonStartMs/1000)</c> — the SERVER's canonical run key
    /// (<c>levelUuid-dungeonStartMs/1000</c>). <c>LevelUuid</c> ALONE is <b>not</b> run-unique: the game
    /// reuses the level-instance id when the same party re-enters the same dungeon (CLAUDE.md hard
    /// requirement), so two separate runs can share a <c>LevelUuid</c> (prod sea/XCNEJMFvLt +
    /// sea/rw3OyTj58G, Tina's Mindrealm master, 42 min apart). The per-run start
    /// (<c>DungeonStartMs</c>, latched per run since 2.1.1) is what separates them, so each local row maps
    /// 1:1 to a server session / short id instead of collapsing two runs (two boss kills) into one row.
    /// The <c>/1000</c> matches the server's SECOND granularity, tolerating sub-second start jitter within
    /// one run. A FIELD fight carries <c>LevelUuid == 0</c> and is deliberately ungroupable: those get a
    /// unique negative key so each stays its own row rather than all collapsing into one bogus "run 0".</para></summary>
    private List<RunGroup> GroupHistoryByRun() => GroupByRun(_history);

    /// <inheritdoc cref="GroupHistoryByRun"/>
    /// <remarks>Pure + static so the grouping pins headless — Plugin cannot be instantiated in a test. The
    /// instance overload above only binds _history, so tests exercise the SAME code the window runs.</remarks>
    internal static List<RunGroup> GroupByRun(IReadOnlyList<EncounterHistoryEntry> history)
    {
        var order = new List<(long uuid, long startS)>();
        var groups = new Dictionary<(long uuid, long startS), List<int>>();
        for (var i = history.Count - 1; i >= 0; i--)      // newest first, so runs come out newest first
        {
            var key = RunKey(history, i);
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

    // (levelUuid, dungeonStartMs/1000) — the server's run identity; a reused levelUuid stays split by its per-run start.
    // Field fight (uuid==0): unique negative key => never grouped. Shared by GroupByRun and CountRuns so the hub /
    // ⋯ card count can never drift from the History list.
    private static (long uuid, long startS) RunKey(IReadOnlyList<EncounterHistoryEntry> history, int i)
    {
        var uuid = history[i].LevelUuid;
        return uuid != 0 ? (uuid, history[i].DungeonStartMs / 1000) : (-(long)(i + 1), 0L);
    }

    /// <summary>Number of runs <see cref="GroupByRun"/> would produce, without building the groups — the
    /// "N runs saved" figure the History header shows.</summary>
    internal static int CountRuns(IReadOnlyList<EncounterHistoryEntry> history)
    {
        var seen = new HashSet<(long uuid, long startS)>();
        for (var i = 0; i < history.Count; i++) seen.Add(RunKey(history, i));
        return seen.Count;
    }

    // ----- saved-run count for the hub card + the HUD ⋯ History card -----
    // Those cards used _history.Count — the ARCHIVE count, which is what retention caps — so a full history read
    // "100 runs saved" while the History header (grouped runs) said "19 runs saved · keeps 100". Both now show the
    // grouped run count. Polled every refresh, so it re-counts only when the history changed: the same
    // (count, first, last) dirty key as RebuildHistorySnapshots covers archive (new last), eviction (new first),
    // delete (count) and clear (count 0) — no allocation on an unchanged poll (Plugin-Performance-Rules §2).
    private int _srCount = -1, _savedRunCount;
    private EncounterHistoryEntry? _srFirst, _srLast;

    private int SavedRunCount()
    {
        var first = _history.Count > 0 ? _history[0] : null;
        var last  = _history.Count > 0 ? _history[^1] : null;
        if (_history.Count != _srCount || !ReferenceEquals(first, _srFirst) || !ReferenceEquals(last, _srLast))
        {
            _savedRunCount = CountRuns(_history);
            _srCount = _history.Count; _srFirst = first; _srLast = last;
        }
        return _savedRunCount;
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

    /// <summary>The uploaded run's identity for the status line — <c>sea/HpPqOu76Bh</c> rather than the whole
    /// URL.
    ///
    /// <para>The full URL is ~55 characters of NoWrap text. Adding the "Upload all" button pushed the row 39px
    /// past the pane at the default 780f width and Copy link overdrew the URL (MEASURED in the history sandbox
    /// story). Wrapping the text in a weighted CellElement did NOT help: NoWrap reports a large preferred
    /// width, so the cell grows rather than clipping. Shortening the text is what actually fits, and it costs
    /// nothing — Copy link right beside it still yields the full link.</para>
    ///
    /// <para>Pure + static so it pins headless. Anything not shaped like a run URL is returned unchanged
    /// rather than mangled.</para></summary>
    internal static string ShortRunLabel(string url)
    {
        const string marker = "/run/";
        var at = url.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? url : url.Substring(at + marker.Length);
    }

    // ----- run-level upload -----

    // Segments still to upload for the selected run, held as ENTRY REFERENCES rather than indices: history is
    // capacity-trimmed, so an index could point at a different (or evicted) run by the time its turn comes.
    private readonly List<EncounterHistoryEntry> _runUploadQueue = new();

    /// <summary>Label for the per-SEGMENT upload pill. Reads "segment" only when there is more than one, so a
    /// single-archive run keeps the plain wording.</summary>
    private string SegmentUploadVerb() => _selectedSegments.Length > 1 ? _loc.T("history.upload.segment") : _loc.T("history.upload.thisRun");

    /// <summary>Whether a segment in that upload phase is eligible for "Upload all". Everything EXCEPT an
    /// in-flight send is — including <see cref="UploadPhase.Done"/>.
    ///
    /// <para>Done was excluded at first, which made the button do NOTHING on the owner's runs: those archives
    /// auto-uploaded at archive time (<c>banked+upload</c>), so all three segments were already Done and the
    /// queue came out empty (measured 2026-07-30 — the log showed no send at all after the click). It also
    /// contradicted the per-SEGMENT button beside it, which happily re-uploads a Done archive verbatim. "Upload
    /// all" now means what it says, and matches its single-segment neighbour.</para>
    ///
    /// <para><see cref="UploadPhase.Skipped"/> stays eligible: it is a policy refusal, and the owner's verified
    /// workflow is to flip the cell on and push the same archive — an `other=off` run uploaded with its events
    /// intact once set to `manual`. <see cref="UploadPhase.Failed"/> makes this a retry-the-rest.
    /// <see cref="UploadPhase.Outdated"/> is EXCLUDED: a run recorded below the upload floor carries an old
    /// baked-in pluginVer the server 426s forever, so re-queuing it only wastes a request.</para>
    ///
    /// <para>Only InFlight is excluded, because a second concurrent send of the same archive is the one thing
    /// that is never wanted. Pure + static so the rule pins headless.</para></summary>
    internal static bool NeedsRunUpload(UploadPhase phase) => phase != UploadPhase.InFlight && phase != UploadPhase.Outdated;

    /// <summary>Queues every not-yet-sent segment of the selected run. Already-uploaded and in-flight segments
    /// are skipped, so pressing this after a partial upload only sends the remainder.</summary>
    private void QueueRunUpload()
    {
        _runUploadQueue.Clear();
        foreach (var idx in _selectedSegments)
        {
            if (idx < 0 || idx >= _history.Count) continue;
            var entry = _history[idx];
            if (!NeedsRunUpload(UploadStateFor(entry))) continue;
            _runUploadQueue.Add(entry);
        }
    }

    /// <summary>Drains the run-upload queue ONE segment at a time, from the ~10 Hz plugin tick.
    ///
    /// <para>SEQUENTIAL on purpose. There is no global upload concurrency guard — <c>UploadHistoryEntry</c>
    /// debounces per entry only — so firing six at once would put six concurrent chunk uploads on the worker.
    /// This session already saw <c>Re-upload chunk 0 FAILED after retries</c>, and multi-uploader capacity has
    /// its own design note (docs/superpowers/specs/2026-07-11-multi-uploader-capacity-design.md). Slower, but
    /// it cannot self-inflict a rate limit.</para>
    ///
    /// <para>Ticked outside the history window's own rebuild so closing the window mid-queue does not stall
    /// the remaining uploads.</para></summary>
    private void TickRunUploadQueue()
    {
        if (_runUploadQueue.Count == 0) return;
        // Hold until nothing is in flight — including an upload started elsewhere (auto-archive, single
        // segment button), so this can never be the second concurrent sender.
        foreach (var h in _history) if (UploadStateFor(h) == UploadPhase.InFlight) return;
        var next = _runUploadQueue[0];
        _runUploadQueue.RemoveAt(0);
        if (_history.Contains(next)) UploadHistoryEntry(next);   // dropped from history while queued: skip
    }
}
