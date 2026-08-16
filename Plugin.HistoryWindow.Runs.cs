using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;   // UploadPhase

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
            // Search filter (owner 2026-08-15): match the query against "mapName verdict clock". A filtered-out
            // row is simply not added; the selected run's DETAIL pane still shows (via _selectedSession below),
            // so a search never deselects — it only narrows the list.
            if (!HistoryRowMatches($"{map} {primary.Result} {FormatSessionClock(primary.ArchivedAtMs)}", _historySearch))
                continue;
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

    // Live history search (owner 2026-08-15): filters the grouped run rows by map/verdict/clock as-you-type
    // (RebuildHistorySnapshots runs every shown frame, so setting this in OnChange reflows the list).
    private string _historySearch = "";

    /// <summary>The "Search" input above the session list — a live filter, hidden when the list is empty.
    /// Lives here (not in Plugin.HistoryWindow.cs) beside the filter it drives, and to keep that file off
    /// the 500-LoC threshold.</summary>
    private HudElement BuildHistorySearchRow() => new ConditionalElement(() => _history.Count > 0,
        new RowElement(new HudElement[]
        {
            new TextElement(() => "Search", MutedCol, Width: 52f),
            new InputElement(() => _historySearch, _ => { }, 180f, OnChange: s => _historySearch = s),
        }, Gap: 4f));

    /// <summary>One run's archives. <see cref="Segments"/> is oldest-first (so chip 1 is the run's first
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
            var uuid = history[i].LevelUuid;
            // (levelUuid, dungeonStartMs/1000) — the server's run identity; a reused levelUuid stays split
            // by its per-run start. Field fight (uuid==0): unique negative key => never grouped.
            var key = uuid != 0 ? (uuid, history[i].DungeonStartMs / 1000) : (-(long)(i + 1), 0L);
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
        // NoWrap + enough width: at 60f the word wrapped and dropped its final "s" onto a second line
        // (owner screenshot 2026-07-30). MEASURED via the history sandbox story, not guessed.
        kids[0] = new TextElement(() => "segments", MutedCol, Width: 76f, NoWrap: true);
        for (var i = 0; i < MaxSegmentChips; i++)
        {
            var slot = i;
            kids[slot + 1] = new ConditionalElement(
                () => slot < _selectedSegments.Length,
                // No fixed Width: the chip auto-sizes to its label. Widths cannot be per-frame (Width is a
                // ctor value, not a Func) and the labels vary in length, so pinning one width would either
                // clip "bosskill" or pad "idle" — owner: "each segment should tell user how it created".
                new ButtonElement(
                    () => SegmentChipLabel(slot),
                    () => { if (slot < _selectedSegments.Length) SelectSession(_selectedSegments[slot]); },
                    Active: () => slot < _selectedSegments.Length && _selectedSegments[slot] == _historyIndex));
        }
        // Only meaningful when the run actually HAS multiple archives.
        return new ConditionalElement(() => _selectedSegments.Length > 1, new RowElement(kids, Gap: 4f));
    }

    /// <summary>How this segment came to exist — the archive's own trigger (bosskill / stage / scene / wipe /
    /// boss / idle / manual). Duplicates are expected and honest: selecting two run-end stages really does
    /// produce two `stage` segments, and the chips are chronological so position tells them apart.</summary>
    private string SegmentChipLabel(int slot)
    {
        if (slot >= _selectedSegments.Length) return "";
        var idx = _selectedSegments[slot];
        if (idx < 0 || idx >= _history.Count) return "";
        var trigger = _history[idx].Trigger;
        return string.IsNullOrEmpty(trigger) ? "manual" : trigger;
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

    /// <summary>Label for the per-SEGMENT button. Reads "segment" only when there is more than one, so a
    /// single-archive run keeps the plain wording.</summary>
    private string SegmentUploadVerb() => _selectedSegments.Length > 1 ? "⤓ Upload segment" : "⤓ Upload this run";

    /// <summary>"Upload all (N)" — visible only for a grouped run. Owner: "sometimes I just don't wanna click
    /// upload manually 6 seqments."</summary>
    private HudElement BuildUploadRunButton() => new ConditionalElement(
        () => _selectedSegments.Length > 1,
        new ButtonElement(
            () => _runUploadQueue.Count > 0 ? $"Uploading {_runUploadQueue.Count} left…" : $"⤓ Upload all ({_selectedSegments.Length})",
            QueueRunUpload,
            Enabled: () => _runUploadQueue.Count == 0));

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
