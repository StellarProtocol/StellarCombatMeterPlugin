using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Per-run <c>DungeonStartMs</c> latch (owner-verified P0 on prod sea/YvLLO3YSc8 + sea/yVfTrPylk7,
/// levelUuid 366250583092363264). The archive used to stamp <c>DungeonStartMs</c> from the LIVE
/// <c>Dungeon.RunTimerStartMs</c> per banked segment. At run end (Victory/settlement — the "~5s before
/// jump" phase) the GAME re-stamps its run timer (measured 680000 → 802000), so a post-kill boundary/tail
/// segment read a bogus "start"; the server keys run identity on <c>&lt;levelUuid&gt;-&lt;start/1000&gt;</c>,
/// so ONE run split into TWO pages (a ~15s all-zero kill phantom on the boards). <c>LevelUuid</c> did NOT
/// split because it uses the latched <c>_lastRunId</c>; <c>DungeonStartMs</c> was the last field still read
/// live.
///
/// Fix: latch the run-start like <c>_lastRunId</c>, with ONE critical difference — latch ONCE per run,
/// never re-latch per combat start (<c>RunTimerStartMs</c> is unstable; the tail's own combat start would
/// otherwise read the reset value and re-split). Plugin cannot be instantiated headless, so — exactly like
/// <see cref="Plugin.LatchTeamId"/> / <see cref="Plugin.UpdateClearLatch"/> — the mutating field lifecycle
/// is exercised over its one pure seam <see cref="Plugin.LatchRunStartMs"/>: the SET side (0-retry latch,
/// <c>EnsureCombatStarted</c>) and the READ side (archive stamp, <c>BuildHistoryEntry</c>) are the same
/// latch-else-live shape, applied at the two sites. These pins are the contract; do not weaken them.
///
/// OUT OF SCOPE: the mid-dungeon-relaunch split (docs/recon/run-identity-relaunch-split.md) is a separate
/// <c>runStartS</c>-changes-on-relaunch issue this latch does not address.
/// </summary>
public sealed class RunStartLatchTests
{
    // -------------------------------------------------------------------------
    // (1) The exact prod regression: a segment banked AFTER a mid-run timer re-stamp carries the LATCHED
    //     start, not the re-stamped live value (the Guild Hunt / Victory-phase scenario).
    // -------------------------------------------------------------------------

    [Fact]
    public void A_segment_banked_after_the_run_timer_re_stamps_carries_the_latched_start()
    {
        // Run's first combat latched the real start while the timer read 680000.
        long latched = Plugin.LatchRunStartMs(latched: 0, live: 680000);
        Assert.Equal(680000, latched);

        // Run end: the game re-stamps RunTimerStartMs to 802000. A post-kill tail segment's own combat
        // start re-applies the SET seam — which must KEEP the already-latched start (never re-latch)…
        long afterTailCombatStart = Plugin.LatchRunStartMs(latched, live: 802000);
        Assert.Equal(680000, afterTailCombatStart);

        // …and the READ seam the archive stamps DungeonStartMs with returns the same latched start, NOT the
        // re-stamped live value — so the tail keys under the SAME run id and the run does not split.
        Assert.Equal(680000, Plugin.LatchRunStartMs(afterTailCombatStart, live: 802000));
    }

    // -------------------------------------------------------------------------
    // (2) The latch resets at the run boundary; the next run latches fresh (no bleed).
    // -------------------------------------------------------------------------

    [Fact]
    public void After_the_boundary_reset_the_next_run_latches_its_own_fresh_start()
    {
        long runA = Plugin.LatchRunStartMs(latched: 0, live: 680000);
        Assert.Equal(680000, runA);

        // BankRunBoundary zeroes _lastRunStartMs at the confirmed run boundary (modelled as latched = 0).
        // The next run's first combat then latches ITS own start, never inheriting run A's.
        long runB = Plugin.LatchRunStartMs(latched: 0, live: 900000);
        Assert.Equal(900000, runB);
        Assert.NotEqual(runA, runB);
    }

    // -------------------------------------------------------------------------
    // (3) 0-retry until nonzero: a first combat event whose timer isn't ready yet stays unlatched and
    //     picks the start up on the next event (open-world, with no dungeon timer, stays 0 forever).
    // -------------------------------------------------------------------------

    [Fact]
    public void An_unready_zero_timer_stays_unlatched_and_retries_until_nonzero()
    {
        // First combat while the dungeon timer isn't ready → still 0 (retry, not a latched 0).
        long stillUnlatched = Plugin.LatchRunStartMs(latched: 0, live: 0);
        Assert.Equal(0, stillUnlatched);

        // Next combat, timer now live → latches.
        long latched = Plugin.LatchRunStartMs(stillUnlatched, live: 500000);
        Assert.Equal(500000, latched);
    }

    // -------------------------------------------------------------------------
    // (4) Never-latched falls back to the live value (open-world / timer never seen at any combat start).
    // -------------------------------------------------------------------------

    [Fact]
    public void A_never_latched_run_falls_back_to_the_live_run_timer()
        => Assert.Equal(777000, Plugin.LatchRunStartMs(latched: 0, live: 777000));
}
