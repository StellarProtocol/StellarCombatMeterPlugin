using System.Collections.Generic;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// History list GROUPING — one row per run, not per archive.
///
/// <para>Owner report 2026-07-30: a single run banks the fight, a tail per selected run-end stage, and the
/// scene-exit tail, so two runs produced SIX history rows — "it will cause tons of history row from auto
/// archive and it's hard for user to select which archive that user wanna see". Grouping is on
/// <c>LevelUuid</c>, the runId every archive of a run shares (verified in the owner's log: three archives all
/// carrying levelUuid=584088755955040256).</para>
///
/// <para>PINNED, do not weaken: a FIELD fight carries <c>LevelUuid == 0</c> and must never group. Collapsing
/// those together would merge unrelated open-world fights into one bogus "run 0" row and hide them all
/// behind a single selection.</para>
/// </summary>
public class HistoryRunGroupingTests
{
    // durMs is the COMBAT (damage) span — the field the primary-segment choice reads.
    private static Plugin.EncounterHistoryEntry Entry(long levelUuid, long durMs, long archivedAt) =>
        new() { LevelUuid = levelUuid, CombatDurationMs = durMs, ArchivedAtMs = archivedAt, EnteredAtMs = archivedAt - durMs };

    [Fact]
    public void The_owners_six_archives_collapse_to_two_rows()
    {
        // Exactly the reported shape: two runs, each fight + two 0ms tails, oldest first in _history.
        var history = new List<Plugin.EncounterHistoryEntry>
        {
            Entry(584088755955040256, 4438, 1_000), Entry(584088755955040256, 0, 6_000), Entry(584088755955040256, 0, 25_000),
            Entry(721167069613129728, 4376, 60_000), Entry(721167069613129728, 0, 65_000), Entry(721167069613129728, 0, 70_000),
        };
        var runs = Plugin.GroupByRun(history);

        Assert.Equal(2, runs.Count);
        Assert.Equal(3, runs[0].Segments.Length);
        Assert.Equal(3, runs[1].Segments.Length);
    }

    [Fact]
    public void Newest_run_comes_first_and_segments_run_oldest_first()
    {
        var history = new List<Plugin.EncounterHistoryEntry>
        {
            Entry(11, 100, 1_000), Entry(11, 0, 2_000),      // older run, indices 0..1
            Entry(22, 100, 9_000), Entry(22, 0, 10_000),     // newer run, indices 2..3
        };
        var runs = Plugin.GroupByRun(history);

        // Newest run first — the list is presented newest-first.
        Assert.Equal(new[] { 2, 3 }, runs[0].Segments);
        // ...while WITHIN a run the order is chronological, so chip 1 is the run's first archive.
        Assert.Equal(new[] { 0, 1 }, runs[1].Segments);
    }

    [Fact]
    public void A_grouped_row_opens_the_fight_not_a_zero_length_tail()
    {
        // The tails are newer, so a naive "newest in the run" pick would open a 0ms row with nothing in it.
        var history = new List<Plugin.EncounterHistoryEntry>
        {
            Entry(11, 4438, 1_000),   // the fight  (index 0)
            Entry(11, 0, 6_000),      // tail       (index 1)
            Entry(11, 0, 25_000),     // tail       (index 2)
        };
        var runs = Plugin.GroupByRun(history);
        Assert.Single(runs);
        Assert.Equal(0, runs[0].Primary);
    }

    [Fact]
    public void An_all_tail_run_still_opens_deterministically_at_its_first_archive()
    {
        // Every segment has a 0ms combat span (no damage anywhere). Ties must keep the EARLIEST so the row
        // does not open somewhere different between frames.
        var history = new List<Plugin.EncounterHistoryEntry> { Entry(11, 0, 1_000), Entry(11, 0, 2_000) };
        var runs = Plugin.GroupByRun(history);
        Assert.Equal(0, runs[0].Primary);
    }

    [Fact]
    public void Field_fights_never_group_even_though_they_share_levelUuid_zero()
    {
        var history = new List<Plugin.EncounterHistoryEntry>
        {
            Entry(0, 500, 1_000), Entry(0, 700, 2_000), Entry(0, 900, 3_000),
        };
        var runs = Plugin.GroupByRun(history);

        Assert.Equal(3, runs.Count);                     // three separate rows, not one
        foreach (var run in runs) Assert.Single(run.Segments);
    }

    [Fact]
    public void Field_fights_and_instanced_runs_coexist()
    {
        var history = new List<Plugin.EncounterHistoryEntry>
        {
            Entry(0, 500, 1_000),                        // field fight        (index 0)
            Entry(77, 4000, 5_000), Entry(77, 0, 6_000), // one instanced run  (indices 1..2)
            Entry(0, 800, 9_000),                        // another field fight(index 3)
        };
        var runs = Plugin.GroupByRun(history);

        Assert.Equal(3, runs.Count);
        Assert.Equal(new[] { 3 }, runs[0].Segments);        // newest first
        Assert.Equal(new[] { 1, 2 }, runs[1].Segments);
        Assert.Equal(new[] { 0 }, runs[2].Segments);
    }

    [Fact]
    public void Every_archive_appears_exactly_once_across_the_grouped_rows()
    {
        // Nothing may be hidden by grouping: the row count shrinks, the archive count must not.
        var history = new List<Plugin.EncounterHistoryEntry>
        {
            Entry(11, 100, 1_000), Entry(0, 50, 2_000), Entry(11, 0, 3_000),
            Entry(22, 200, 4_000), Entry(11, 0, 5_000), Entry(0, 10, 6_000),
        };
        var seen = new List<int>();
        foreach (var run in Plugin.GroupByRun(history)) seen.AddRange(run.Segments);

        seen.Sort();
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, seen);
    }
}

/// <summary>
/// Run-level "Upload all" queueing. Owner 2026-07-30: "sometimes I just don't wanna click upload manually
/// 6 seqments." The send itself is SEQUENTIAL — there is no global upload concurrency guard, so firing every
/// segment at once would put N concurrent chunk uploads on the worker.
/// </summary>
public class RunUploadQueueTests
{
    [Fact]
    public void Already_uploaded_and_in_flight_segments_are_not_re_sent()
    {
        Assert.False(Plugin.NeedsRunUpload(LogUpload.UploadPhase.Done));
        Assert.False(Plugin.NeedsRunUpload(LogUpload.UploadPhase.InFlight));
    }

    // PINNED: a policy-refused segment MUST stay queueable. The owner's verified workflow is to flip the
    // content's upload cell on and then push the SAME archive by hand — an `other=off` run rendered
    // "Uploads off for this content", and after switching to `manual` it uploaded with its events intact.
    // Treating Skipped as terminal would silently exclude exactly those runs from "Upload all".
    [Fact]
    public void A_policy_skipped_segment_is_still_queueable()
        => Assert.True(Plugin.NeedsRunUpload(LogUpload.UploadPhase.Skipped));

    // "Upload all" doubles as retry-the-rest after a partial failure.
    [Fact]
    public void A_failed_segment_is_queued_for_retry()
        => Assert.True(Plugin.NeedsRunUpload(LogUpload.UploadPhase.Failed));

    [Fact]
    public void A_never_attempted_segment_is_queued()
        => Assert.True(Plugin.NeedsRunUpload(default));
}
