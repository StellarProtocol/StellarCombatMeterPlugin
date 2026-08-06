using Stellar.Abstractions.Domain;
using Stellar.CombatMeter;
using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Vault-floor "partial-not-kill" P0 (run sea/qyvCSXteqC — owner's #1 bug). A multi-floor dungeon's
/// floor is its OWN framework run; when the NEXT floor's run-id latches the framework's
/// <c>SetCurrentRun</c> WIPES <c>IDungeonState.LastOutcome</c>/<c>LastSettlement</c> — and that wipe
/// happens BEFORE the plugin's always-firing run-end (scene) archive banks the OUTGOING floor. So
/// <c>BuildHistoryEntry</c> read a blank live latch and the freshly-cleared floor archived as "partial".
///
/// Fix (config-independent kill latch): a run-scoped latch in the plugin (<c>_clearedThisRun</c> /
/// <c>_clearedSettlement</c>) set the tick the clear is observed (<c>BuildAutoArchiveInputs</c>, while
/// the framework latch is still fresh), read by the archive verdict, and reset ONLY at the next
/// encounter's combat start (<c>EnsureCombatStarted</c>) — which the code proves runs AFTER the outgoing
/// floor's run-end archive banks. These pins are the contract; they are never to be weakened. The
/// mutating field lifecycle is exercised over the two pure seams the plugin drives it with:
/// <see cref="Plugin.UpdateClearLatch"/> (per-tick set/carry) and the <c>clearedThisRun</c> arg of
/// <see cref="Plugin.ResolveVerdict"/> (archive read).
/// </summary>
public sealed class VaultClearLatchTests
{
    private static DungeonSettlementInfo Clear(int passTime = 59) => new(passTime, 0, 0);

    // -------------------------------------------------------------------------
    // P0 — config-independent kill / survives the framework wipe (the exact qyvCSXteqC regression).
    // -------------------------------------------------------------------------

    [Fact]
    public void Latched_clear_reads_kill_after_the_framework_wipes_outcome_and_settlement()
    {
        // A tick during the floor's clear: LastOutcome=Success (+ a real settlement) makes the LIVE verdict
        // "kill" (hasFreshClear true), so the plugin latches the clear fact + the settlement's pass-time.
        var (cleared, latched) = Plugin.UpdateClearLatch(
            wasCleared: false, latchedSettlement: null, hasFreshClear: true, liveSettlement: Clear(59));
        Assert.True(cleared);
        Assert.Equal(59, latched!.Value.PassTimeSeconds);

        // The NEXT floor's run-id latches → framework wipes BOTH. The always-firing run-end (scene) archive
        // banks the OUTGOING floor now, seeing a blank LIVE state (freshSettlement null, outcome None) — but
        // the run-scoped latch survives the wipe, so the verdict is still "kill", NOT "partial".
        Assert.Equal("kill",
            Plugin.ResolveVerdict(freshSettlement: null, DungeonOutcome.None, clearedThisRun: cleared));
    }

    [Fact]
    public void Clear_latch_survives_a_quiet_tick_with_no_fresh_signal()
    {
        // Once latched, subsequent ticks whose live signal is gone (settlement already wiped →
        // hasFreshClear false) must carry the latch UNCHANGED. Only the encounter reset clears it; a quiet
        // tick never does — otherwise the latch could evaporate between the clear and the run-end archive.
        var (cleared, latched) = Plugin.UpdateClearLatch(
            wasCleared: true, latchedSettlement: Clear(59), hasFreshClear: false, liveSettlement: null);
        Assert.True(cleared);
        Assert.Equal(59, latched!.Value.PassTimeSeconds);
    }

    [Fact]
    public void Clear_latch_keeps_its_captured_settlement_when_a_later_clear_signal_carries_none()
    {
        // hasFreshClear can rise via a bare LastOutcome=Success while LastSettlement is momentarily null;
        // that must NOT null out an already-captured pass-time (only overwrite when the live one is non-null).
        var (_, latched) = Plugin.UpdateClearLatch(
            wasCleared: true, latchedSettlement: Clear(59), hasFreshClear: true, liveSettlement: null);
        Assert.Equal(59, latched!.Value.PassTimeSeconds);
    }

    [Fact]
    public void Clear_latch_captures_the_live_settlement_the_tick_the_clear_is_first_observed()
    {
        var (cleared, latched) = Plugin.UpdateClearLatch(
            wasCleared: false, latchedSettlement: null, hasFreshClear: true, liveSettlement: Clear(123));
        Assert.True(cleared);
        Assert.Equal(123, latched!.Value.PassTimeSeconds);
    }

    // -------------------------------------------------------------------------
    // P0 — no bleed to the next run. After the encounter reset (EnsureCombatStarted sets
    // _clearedThisRun=false, _clearedSettlement=null), a run that never clears reads "partial".
    // -------------------------------------------------------------------------

    [Fact]
    public void A_never_cleared_run_reads_partial()
        => Assert.Equal("partial",
            Plugin.ResolveVerdict(freshSettlement: null, DungeonOutcome.None, clearedThisRun: false));

    [Fact]
    public void No_fresh_clear_ever_leaves_the_latch_unset()
    {
        // A whole run of quiet ticks (never cleared) keeps the latch false with no captured settlement —
        // so the next run inherits nothing to bleed.
        var (cleared, latched) = Plugin.UpdateClearLatch(
            wasCleared: false, latchedSettlement: null, hasFreshClear: false, liveSettlement: Clear(59));
        Assert.False(cleared);
        Assert.Null(latched);
    }

    [Fact]
    public void A_failed_run_still_reads_fail_even_if_a_clear_was_somehow_latched()
    {
        // Fail precedence is preserved: a wipe (outcome Failed) reads "fail" regardless of the latch.
        Assert.Equal("fail",
            Plugin.ResolveVerdict(freshSettlement: null, DungeonOutcome.Failed, clearedThisRun: true));
    }

    // -------------------------------------------------------------------------
    // P0 — no double-bank / exactly one entry (guards the replay-window-contiguity P0).
    // -------------------------------------------------------------------------

    [Fact]
    public void A_stats_bearing_cleared_floor_banks_kill_and_blocks_a_second_empty_marker()
    {
        // The floor's OWN (stats-bearing) run-end archive reads "kill" via the latch even after the wipe …
        Assert.Equal("kill",
            Plugin.ResolveVerdict(freshSettlement: null, DungeonOutcome.None, clearedThisRun: true));
        // … which sets _clearMarkerBanked, so any later empty run-end archive of the same dungeon exit does
        // NOT bank a SECOND (empty) kill marker — exactly one entry, one contiguous replay window.
        Assert.False(Plugin.ShouldBankEmptyClearMarker(
            ArchiveReason.SceneChange, "kill", alreadyBankedThisRun: true));
    }

    [Fact]
    public void An_empty_clear_marker_still_banks_once_when_the_latch_survives_the_wipe()
    {
        // The fast-single-boss path (boss-kill archive already banked + Clear()ed the fight): the run-end
        // archive has zero stat rows, but the latch survives the wipe → verdict "kill" → the first empty
        // run-end archive banks the clear marker (once per run).
        Assert.True(Plugin.ShouldBankEmptyClearMarker(
            ArchiveReason.SceneChange, "kill", alreadyBankedThisRun: false));
    }

    // -------------------------------------------------------------------------
    // P0 — junk unaffected. An all-zero run that never cleared is still suppressed exactly as before
    // (the latch is never set, so it contributes nothing to carriesFreshResult).
    // -------------------------------------------------------------------------

    [Fact]
    public void An_all_zero_never_cleared_auto_archive_is_still_suppressed()
    {
        var (cleared, _) = Plugin.UpdateClearLatch(
            wasCleared: false, latchedSettlement: null, hasFreshClear: false, liveSettlement: null);
        Assert.False(cleared);
        // carriesFreshResult == (_clearedThisRun || IsFreshKill(...)); with no clear and no fresh
        // settlement it is false, so an all-zero auto archive is suppressed, unchanged from before.
        Assert.True(Plugin.ShouldSuppressAutoArchive(
            ArchiveReason.SceneChange, carriesFreshResult: cleared, allRowsZero: true));
    }
}
