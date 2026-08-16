using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Pure-decision tests for the mid-dungeon-relaunch recovery marker
// (docs/recon/run-identity-relaunch-split.md § "Concrete owner-approved design"). The marker persists
// the run's original dungeon-start so a relaunch/crash mid-run continues the SAME server run instead of
// splitting on a re-stamped RunTimerStartMs. The restore decision is gated on levelUuid AND partyId AND
// a freshness bound (owner add 2026-08-16: "use party id to recheck too, don't use just levelid" +
// "beware ... reconnect and got moved to outside dungeon by timeout or got kick from party").
// These pins are pure (Plugin can't be headless-instantiated — same convention as RunBoundaryTrackerTests).
public class RelaunchMarkerTests
{
    private static ActiveRunMarker Marker(
        long levelUuid = 100, long partyId = 7, long dungeonStartMs = 5_000, long lastAliveMs = 1_000)
        => new(levelUuid, partyId, dungeonStartMs, lastAliveMs);

    // ---- serialization round-trip -------------------------------------------------------------

    [Fact]
    public void Serialize_then_deserialize_round_trips_every_field()
    {
        var m = new ActiveRunMarker(643789110607085568, 207462788, 1784630009000, 1784630500000);
        Assert.True(RelaunchMarker.TryDeserialize(RelaunchMarker.Serialize(m), out var back));
        Assert.Equal(m.LevelUuid, back.LevelUuid);
        Assert.Equal(m.PartyId, back.PartyId);
        Assert.Equal(m.DungeonStartMs, back.DungeonStartMs);
        Assert.Equal(m.LastAliveMs, back.LastAliveMs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("crm1 1 2 3")]          // too few fields
    [InlineData("crm1 1 2 3 4 5")]      // too many fields
    [InlineData("crm9 1 2 3 4")]        // wrong version tag
    [InlineData("crm1 x 2 3 4")]        // non-numeric
    public void TryDeserialize_rejects_malformed_input(string? text)
        => Assert.False(RelaunchMarker.TryDeserialize(text, out _));

    // ---- ResolveRelaunchStart: the restore gate ----------------------------------------------

    [Fact]
    public void Restore_returns_dungeonStart_when_run_party_and_freshness_all_match()
    {
        long got = RelaunchMarker.ResolveRelaunchStart(
            Marker(levelUuid: 100, partyId: 7, dungeonStartMs: 5_000, lastAliveMs: 1_000),
            currentRunId: 100, currentPartyId: 7, nowMs: 60_000, maxGapMs: RelaunchMarker.MaxRelaunchGapMs);
        Assert.Equal(5_000, got);
    }

    [Fact]
    public void Restore_declines_when_no_marker()
        => Assert.Equal(0, RelaunchMarker.ResolveRelaunchStart(
            null, currentRunId: 100, currentPartyId: 7, nowMs: 60_000, maxGapMs: RelaunchMarker.MaxRelaunchGapMs));

    [Fact]
    public void Restore_declines_on_different_instance_kicked_to_town_or_other_run()
        => Assert.Equal(0, RelaunchMarker.ResolveRelaunchStart(
            Marker(levelUuid: 100), currentRunId: 999, currentPartyId: 7,
            nowMs: 60_000, maxGapMs: RelaunchMarker.MaxRelaunchGapMs));

    // Owner add 2026-08-16: use party id to recheck too. Kicked from party / re-formed → the server keys
    // the run under the new party, so a same-instance continuation with a DIFFERENT party must NOT glue.
    [Fact]
    public void Restore_declines_when_party_changed_even_though_instance_matches()
        => Assert.Equal(0, RelaunchMarker.ResolveRelaunchStart(
            Marker(levelUuid: 100, partyId: 7), currentRunId: 100, currentPartyId: 42,
            nowMs: 60_000, maxGapMs: RelaunchMarker.MaxRelaunchGapMs));

    // Reconnect timing (run sea/kqCsvtAMx3): the GrpcTeam snapshot lags the first post-reconnect combat
    // event, so live party reads 0 while the marker holds the real party. A transient 0 must NOT reject the
    // restore (the server still separates by the uploaded party, resolved late) — else the run splits on the
    // re-stamped runStartS. This is the fix for the second owner test.
    [Fact]
    public void Restore_accepts_when_live_party_not_synced_yet_zero()
        => Assert.Equal(5_000, RelaunchMarker.ResolveRelaunchStart(
            Marker(levelUuid: 100, partyId: 206285121, dungeonStartMs: 5_000, lastAliveMs: 1_000),
            currentRunId: 100, currentPartyId: 0, nowMs: 60_000, maxGapMs: RelaunchMarker.MaxRelaunchGapMs));

    [Fact]
    public void Restore_declines_when_relaunch_gap_exceeds_bound()
        => Assert.Equal(0, RelaunchMarker.ResolveRelaunchStart(
            Marker(lastAliveMs: 1_000), currentRunId: 100, currentPartyId: 7,
            nowMs: 1_000 + RelaunchMarker.MaxRelaunchGapMs + 1, maxGapMs: RelaunchMarker.MaxRelaunchGapMs));

    [Fact]
    public void Restore_accepts_gap_exactly_at_the_bound()
        => Assert.Equal(5_000, RelaunchMarker.ResolveRelaunchStart(
            Marker(dungeonStartMs: 5_000, lastAliveMs: 1_000), currentRunId: 100, currentPartyId: 7,
            nowMs: 1_000 + RelaunchMarker.MaxRelaunchGapMs, maxGapMs: RelaunchMarker.MaxRelaunchGapMs));

    [Fact]
    public void Restore_declines_when_current_run_id_is_zero_open_world_or_between_runs()
        => Assert.Equal(0, RelaunchMarker.ResolveRelaunchStart(
            Marker(levelUuid: 100), currentRunId: 0, currentPartyId: 7,
            nowMs: 60_000, maxGapMs: RelaunchMarker.MaxRelaunchGapMs));

    [Fact]
    public void Restore_declines_when_marker_level_is_zero()
        => Assert.Equal(0, RelaunchMarker.ResolveRelaunchStart(
            Marker(levelUuid: 0), currentRunId: 0, currentPartyId: 7,
            nowMs: 60_000, maxGapMs: RelaunchMarker.MaxRelaunchGapMs));

    // No valid server clock yet (ServerNowMs == 0) → can't judge freshness → do NOT glue (safe fallback
    // to a fresh runStartS). A negative delta (clock ran backwards) is distrusted the same way.
    [Fact]
    public void Restore_declines_when_no_server_clock_yet()
        => Assert.Equal(0, RelaunchMarker.ResolveRelaunchStart(
            Marker(lastAliveMs: 1_000), currentRunId: 100, currentPartyId: 7,
            nowMs: 0, maxGapMs: RelaunchMarker.MaxRelaunchGapMs));

    [Fact]
    public void Restore_declines_when_clock_went_backwards()
        => Assert.Equal(0, RelaunchMarker.ResolveRelaunchStart(
            Marker(lastAliveMs: 10_000), currentRunId: 100, currentPartyId: 7,
            nowMs: 9_000, maxGapMs: RelaunchMarker.MaxRelaunchGapMs));

    // Solo (partyId 0) continues correctly across a relaunch — 0 == 0 party match.
    [Fact]
    public void Restore_accepts_solo_run_matching_party_zero()
        => Assert.Equal(5_000, RelaunchMarker.ResolveRelaunchStart(
            Marker(levelUuid: 100, partyId: 0, dungeonStartMs: 5_000, lastAliveMs: 1_000),
            currentRunId: 100, currentPartyId: 0, nowMs: 60_000, maxGapMs: RelaunchMarker.MaxRelaunchGapMs));

    // Defensive guard: a marker that somehow carries a zero dungeon-start never restores (PersistActiveRunMarker
    // won't write one, but the gate must not hand back a zero "start" that would key a bogus run).
    [Fact]
    public void Restore_declines_when_marker_dungeon_start_is_zero()
        => Assert.Equal(0, RelaunchMarker.ResolveRelaunchStart(
            Marker(levelUuid: 100, partyId: 7, dungeonStartMs: 0, lastAliveMs: 1_000),
            currentRunId: 100, currentPartyId: 7, nowMs: 60_000, maxGapMs: RelaunchMarker.MaxRelaunchGapMs));

    // ---- IsSettledOutOfMarkedRun: the stale-marker-clear raw condition (grace applied by caller). The
    // single `worldActive` arg is IClientState.IsWorldActive — false during boot/title/load AND zone-loads. ----

    // Kicked out by timeout → settled in town (currentRunId 0), a real dungeon marker → out.
    [Fact]
    public void SettledOut_true_when_world_active_and_in_town()
        => Assert.True(RelaunchMarker.IsSettledOutOfMarkedRun(
            markerLevelUuid: 100, currentRunId: 0, worldActive: true));

    // Moved into a DIFFERENT (non-zero) dungeon than the marker's → also out.
    [Fact]
    public void SettledOut_true_when_world_active_and_in_a_different_dungeon()
        => Assert.True(RelaunchMarker.IsSettledOutOfMarkedRun(
            markerLevelUuid: 100, currentRunId: 999, worldActive: true));

    // Good relaunch: back in the marked instance → NOT out (marker must survive to be restored).
    [Fact]
    public void SettledOut_false_when_back_in_the_marked_instance()
        => Assert.False(RelaunchMarker.IsSettledOutOfMarkedRun(
            markerLevelUuid: 100, currentRunId: 100, worldActive: true));

    // Not a stable world scene (boot / title / load / in-world zone-load): CurrentRunId is unreliable, so
    // never read as "out" — this is what keeps the good-case restore's marker alive through the relaunch load.
    [Fact]
    public void SettledOut_false_when_world_not_active()
        => Assert.False(RelaunchMarker.IsSettledOutOfMarkedRun(
            markerLevelUuid: 100, currentRunId: 0, worldActive: false));

    [Fact]
    public void SettledOut_false_when_no_meaningful_marker()
        => Assert.False(RelaunchMarker.IsSettledOutOfMarkedRun(
            markerLevelUuid: 0, currentRunId: 999, worldActive: true));

    // ---- ShouldClearOnBoundary: clear the marker ONLY when the run it belongs to is the one ending ----
    // (BankRunBoundary fires on dungeon ENTRY too — old=0 — where clearing would wipe the marker a
    // reconnect needs BEFORE the first combat can restore from it. Root cause of the first owner test:
    // the reconnect's entry boundary cleared the marker → no restore → runStartS re-stamped → split.)

    // Real leave / run-end of the marked run → clear.
    [Fact]
    public void ClearOnBoundary_true_when_outgoing_run_is_the_marked_run()
        => Assert.True(RelaunchMarker.ShouldClearOnBoundary(Marker(levelUuid: 100), outgoingRunId: 100));

    // Reconnect's dungeon-ENTRY boundary (outgoingRunId 0) → do NOT clear (the restore still needs it).
    [Fact]
    public void ClearOnBoundary_false_on_entry_boundary_outgoing_zero()
        => Assert.False(RelaunchMarker.ShouldClearOnBoundary(Marker(levelUuid: 100), outgoingRunId: 0));

    // A boundary for a DIFFERENT run than the marker's → do NOT clear (defensive; shouldn't happen).
    [Fact]
    public void ClearOnBoundary_false_when_outgoing_run_differs_from_marker()
        => Assert.False(RelaunchMarker.ShouldClearOnBoundary(Marker(levelUuid: 100), outgoingRunId: 999));

    [Fact]
    public void ClearOnBoundary_false_when_no_marker()
        => Assert.False(RelaunchMarker.ShouldClearOnBoundary(null, outgoingRunId: 100));

    [Fact]
    public void ClearOnBoundary_false_when_marker_level_is_zero()
        => Assert.False(RelaunchMarker.ShouldClearOnBoundary(Marker(levelUuid: 0), outgoingRunId: 0));

    // ---- ResolvePartyId: the archive's party id, with the relaunch fallback ----
    // (run sea/UD87unsYz2: after reconnect the game party read 0 for the whole run's archives and only
    // re-synced afterward, so the upload sent partyId=0 and split from MAIN's 206285121. The marker held
    // the run's real party — restore it as a LAST-RESORT fallback, preferring any known live party.)

    // Normal run: the once-per-run latch (nonzero) wins — unchanged from LatchTeamId.
    [Fact]
    public void ResolveParty_prefers_the_run_latch()
        => Assert.Equal(7, RelaunchMarker.ResolvePartyId(latched: 7, live: 9, relaunchFallback: 5));

    // Latch 0 (reconnect), live known → live wins (fallback ignored) — covers kick-to-a-new-party.
    [Fact]
    public void ResolveParty_uses_live_when_latch_zero_and_live_known()
        => Assert.Equal(9, RelaunchMarker.ResolvePartyId(latched: 0, live: 9, relaunchFallback: 5));

    // Latch 0 AND live still 0 (party not re-synced) → the marker's party fills in. THE FIX.
    [Fact]
    public void ResolveParty_falls_back_to_marker_party_when_latch_and_live_both_zero()
        => Assert.Equal(206285121, RelaunchMarker.ResolvePartyId(latched: 0, live: 0, relaunchFallback: 206285121));

    // Solo, no fallback → 0 (unchanged).
    [Fact]
    public void ResolveParty_zero_when_all_unknown()
        => Assert.Equal(0, RelaunchMarker.ResolvePartyId(latched: 0, live: 0, relaunchFallback: 0));

    // Regression pin (qa 2026-08-16): with NO fallback (every non-relaunch run passes 0), ResolvePartyId
    // must be byte-identical to the old LatchTeamId — so a future edit that breaks the "normal runs
    // unchanged" guarantee turns this red. Covers the latch-wins, live-wins, and both-zero branches.
    [Theory]
    [InlineData(7, 9)]
    [InlineData(0, 9)]
    [InlineData(0, 0)]
    [InlineData(206285121, 0)]
    public void ResolveParty_with_zero_fallback_equals_LatchTeamId(long latched, long live)
        => Assert.Equal(Plugin.LatchTeamId(latched, live), RelaunchMarker.ResolvePartyId(latched, live, 0));
}
