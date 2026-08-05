using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter;
using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class HistoryCaptureTests
{
    [Fact]
    public void Frozen_series_are_per_source_per_channel()
    {
        var entry = new Plugin.EncounterHistoryEntry();
        var id = new EntityId(0x0000_0001_0000_0280L);
        entry.Series[id] = new SourceSeries
        {
            BucketMs = 1000,
            Dealt    = new long[] { 100, 200, 150 },
            Healing  = new long[] { 0, 0, 0 },
            Taken    = new long[] { 50, 0, 25 },
        };
        Assert.Equal(3, entry.Series[id].Dealt.Length);
        Assert.Equal(200, entry.Series[id].Dealt[1]);
        Assert.Equal(25,  entry.Series[id].Taken[2]);
    }

    [Fact]
    public void Freeze_round_trips_all_three_channels_with_sparse_trailing_gap()
    {
        var t = new SourceTimeline(bucketMs: 1000, maxBuckets: 600);
        // Dealt at 0s and 1s; a sparse trailing gap then a hit at 4s (buckets 2 & 3 stay empty).
        t.Add(TimelineChannel.Dealt, atMs: 0,    startMs: 0, amount: 100);
        t.Add(TimelineChannel.Dealt, atMs: 1000, startMs: 0, amount: 200);
        t.Add(TimelineChannel.Dealt, atMs: 4000, startMs: 0, amount: 50);
        t.Add(TimelineChannel.Healing, atMs: 1000, startMs: 0, amount: 30);
        t.Add(TimelineChannel.Taken,   atMs: 2000, startMs: 0, amount: 70);

        // Mirror the real archive path: FreezeTimelines() builds SourceSeries from these three Freeze calls,
        // incl. the Taken channel via the real SourceTimeline.Freeze(TimelineChannel.Taken).
        var frozen = new SourceSeries
        {
            BucketMs = t.BucketMs,
            Dealt    = t.Freeze(TimelineChannel.Dealt),
            Healing  = t.Freeze(TimelineChannel.Healing),
            Taken    = t.Freeze(TimelineChannel.Taken),
        };

        Assert.Equal(1000, frozen.BucketMs);
        // Length spans up to the highest occupied index (3 -> length 4), interior gaps are zero.
        Assert.Equal(5, frozen.Dealt.Length);
        Assert.Equal(new long[] { 100, 200, 0, 0, 50 }, frozen.Dealt);
        // Healing's highest index is 1 -> length 2.
        Assert.Equal(new long[] { 0, 30 }, frozen.Healing);
        // Taken's highest index is 2 -> length 3.
        Assert.Equal(new long[] { 0, 0, 70 }, frozen.Taken);
    }

    [Fact]
    public void Frozen_arrays_are_isolated_from_subsequent_live_mutation()
    {
        var t = new SourceTimeline(1000, 600);
        t.Add(TimelineChannel.Dealt, atMs: 0,    startMs: 0, amount: 100);
        t.Add(TimelineChannel.Taken, atMs: 0,    startMs: 0, amount: 40);

        var frozenDealt = t.Freeze(TimelineChannel.Dealt);
        var frozenTaken = t.Freeze(TimelineChannel.Taken);

        // After freezing, the live timeline keeps accruing (next encounter ticks before Clear()).
        t.Add(TimelineChannel.Dealt, atMs: 0,    startMs: 0, amount: 999);
        t.Add(TimelineChannel.Dealt, atMs: 5000, startMs: 0, amount: 999);
        t.Add(TimelineChannel.Taken, atMs: 0,    startMs: 0, amount: 999);

        // Freeze allocates fresh arrays (deep-copy semantics), so the archived snapshot is untouched.
        Assert.Equal(new long[] { 100 }, frozenDealt);
        Assert.Equal(new long[] { 40 },  frozenTaken);
    }

    [Fact]
    public void ComputeUptime_is_active_span_over_duration()
    {
        Assert.Equal(0.5f, Plugin.ComputeUptime(firstHitMs: 0, lastHitMs: 30000, durationMs: 60000));
    }

    [Fact]
    public void ComputeUptime_zero_duration_is_zero()
    {
        Assert.Equal(0f, Plugin.ComputeUptime(0, 30000, 0));
    }

    [Fact]
    public void ComputeUptime_clamps_to_one_when_span_exceeds_duration()
    {
        Assert.Equal(1f, Plugin.ComputeUptime(0, 90000, 60000));
    }

    [Fact]
    public void ComputeUptime_zero_when_no_active_span()
    {
        // lastHit <= firstHit (no progress) → 0, regardless of duration.
        Assert.Equal(0f, Plugin.ComputeUptime(5000, 5000, 60000));
    }

    // -------------------------------------------------------------------------
    // IsFreshKill — false-"KILL"-badge fix (bug: manual mid-dungeon archive with
    // no boss killed still showed the "kill" pill, because IDungeonState.LastSettlement
    // is sticky for the whole run and doesn't reset between segments/pulls).
    // -------------------------------------------------------------------------

    [Fact]
    public void IsFreshKill_false_when_no_settlement_observed()
    {
        Assert.False(Plugin.IsFreshKill(current: null, baseline: null));
    }

    [Fact]
    public void IsFreshKill_false_when_settlement_unchanged_since_encounter_started()
    {
        // Reproduces the reported bug: a stale settlement from an earlier segment of the same
        // run was already sitting in LastSettlement before this encounter's first hit, and never
        // changed — a manual archive here must NOT be tagged "kill".
        var stale = new DungeonSettlementInfo(120, 500, 0);
        Assert.False(Plugin.IsFreshKill(current: stale, baseline: stale));
    }

    [Fact]
    public void IsFreshKill_true_when_settlement_newly_appears_during_the_encounter()
    {
        var fresh = new DungeonSettlementInfo(95, 800, 0);
        Assert.True(Plugin.IsFreshKill(current: fresh, baseline: null));
    }

    [Fact]
    public void IsFreshKill_true_when_settlement_changes_from_an_earlier_kill_in_the_same_run()
    {
        // Multi-boss run: baseline already holds boss #1's settlement; boss #2's differing
        // settlement is genuine evidence that THIS encounter ended in a kill too.
        var boss1 = new DungeonSettlementInfo(100, 400, 0);
        var boss2 = new DungeonSettlementInfo(140, 650, 0);
        Assert.True(Plugin.IsFreshKill(current: boss2, baseline: boss1));
    }

    // -------------------------------------------------------------------------
    // ResolveVerdict — 3-way run verdict (fail/kill/partial). Fail wins outright
    // (a wipe), independent of any stale/fresh settlement lying around from an
    // earlier segment of the same run.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(false, DungeonOutcome.Failed,  "fail")]
    [InlineData(true,  DungeonOutcome.Failed,  "fail")]
    [InlineData(true,  DungeonOutcome.None,    "kill")]
    [InlineData(false, DungeonOutcome.Success, "kill")]
    [InlineData(false, DungeonOutcome.None,    "partial")]
    public void ResolveVerdict_truth_table(bool hasSettlement, DungeonOutcome outcome, string expected)
    {
        var s = hasSettlement ? new DungeonSettlementInfo(481, 425, 0) : (DungeonSettlementInfo?)null;
        Assert.Equal(expected, Plugin.ResolveVerdict(s, outcome));
    }

    [Fact]
    public void ResolveVerdict_partial_when_settlement_carries_only_total_score()
    {
        // Regression (686/700 capture): total_score is a LIVE progress score the game sends
        // mid-run and on partials. A settlement with ONLY total_score (no pass_time / no
        // master_mode_score) must NOT be promoted to "kill".
        var scoreOnly = new DungeonSettlementInfo(0, 0, 340);
        Assert.Equal("partial", Plugin.ResolveVerdict(scoreOnly, DungeonOutcome.None));
    }

    // -------------------------------------------------------------------------
    // ShouldBankEmptyClearMarker — Option B (owner ruling 2026-08-05, run sea/xHC0xrYY8r): a quick
    // single-boss clear had the boss-kill archive bank + Clear() the fight ~1s BEFORE the game's late
    // settlement/clear packet arrived, so the run-end (scene) archive that carries the clear had zero
    // stat rows and was dropped as "skip-empty" — the run read "partial" though the boss died. Fix: bank
    // that empty archive as a small CLEAR marker so the run reads as a kill. Guards: only a genuine kill
    // (a bare pass=0 re-delivery on exit is "partial" and must not mark), never a manual click, and at
    // most once per run (a dungeon exit fires several run-end archives while the clear is still sticky).
    // -------------------------------------------------------------------------

    [Fact]
    public void ShouldBankEmptyClearMarker_banks_a_fresh_kill_on_a_run_end_archive()
    {
        Assert.True(Plugin.ShouldBankEmptyClearMarker(ArchiveReason.SceneChange, "kill", alreadyBankedThisRun: false));
        Assert.True(Plugin.ShouldBankEmptyClearMarker(ArchiveReason.StageChange, "kill", alreadyBankedThisRun: false));
    }

    [Fact]
    public void ShouldBankEmptyClearMarker_stays_skip_empty_without_a_clear()
    {
        // A bare pass=0 settlement re-delivery on exit resolves to "partial" — it must NOT bank a junk
        // marker; a "fail" is not a clear either.
        Assert.False(Plugin.ShouldBankEmptyClearMarker(ArchiveReason.SceneChange, "partial", alreadyBankedThisRun: false));
        Assert.False(Plugin.ShouldBankEmptyClearMarker(ArchiveReason.SceneChange, "fail", alreadyBankedThisRun: false));
    }

    [Fact]
    public void ShouldBankEmptyClearMarker_banks_at_most_once_per_run()
    {
        // A dungeon exit steps through several run-end archives while the clear settlement is still
        // sticky; only the first marks.
        Assert.False(Plugin.ShouldBankEmptyClearMarker(ArchiveReason.SceneChange, "kill", alreadyBankedThisRun: true));
    }

    [Fact]
    public void ShouldBankEmptyClearMarker_never_on_a_manual_click_with_nothing_to_save()
        => Assert.False(Plugin.ShouldBankEmptyClearMarker(ArchiveReason.Manual, "kill", alreadyBankedThisRun: false));

    // -------------------------------------------------------------------------
    // LatchTeamId — run-identity fix (Task B1): the party id (GrpcTeam team_id) that BuildHistoryEntry
    // stamps onto EncounterHistoryEntry.PartyId must be the value LATCHED at combat start
    // (Plugin.Capture.cs's EnsureCombatStarted -> _lastTeamId), not a live re-read at archive time —
    // otherwise a mid-run/post-run party change (member leaves, party disbands, re-forms) would
    // retroactively relabel an already-in-progress or already-archived encounter with the WRONG party,
    // defeating the server's per-party run-identity key (docs/superpowers/specs/
    // 2026-08-04-run-identity-party-teamkey-design.md). Mirrors LevelUuid's latched-fallback shape,
    // but with the opposite preference order: latched wins here; live is only a fallback for the
    // solo-at-combat-start (latch == 0) edge case.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(500, 999, 500)]  // latched preferred over a DIFFERENT live value (party changed mid-run)
    [InlineData(500, 0,   500)]  // latched preferred even after the live party has since disbanded (live -> 0)
    [InlineData(0,   777, 777)]  // solo at combat start (no latch) -> falls back to a live read
    [InlineData(0,   0,   0)]    // solo throughout (both unformed)
    public void LatchTeamId_prefers_the_run_start_latch_over_a_live_read(long latched, long live, long expected)
        => Assert.Equal(expected, Plugin.LatchTeamId(latched, live));

    // -------------------------------------------------------------------------
    // EnsurePartyMembersTracked — owner 2026-08-05: the run/meter must list EVERY party member,
    // including ones who were silent (0 dmg / 0 heal / 0 taken) in a short archived window. Injects a
    // zero-stat row per current party member WITHOUT overwriting an active one; an empty roster (a true
    // solo run — no party or bots) is a no-op so solo runs stay byte-identical.
    // -------------------------------------------------------------------------

    private static PartyMember Member(long charId, int sceneId = 100, bool isSelf = false) => new(
        CharId: charId, Name: "P" + charId, Profession: 5, Level: 1, Hp: 1, MaxHp: 1,
        SceneId: sceneId, Position: default, IsOnline: true, IsSelf: isSelf, GroupId: 0);

    [Fact]
    public void EnsurePartyMembersTracked_injects_in_scene_silent_members_only()
    {
        var self       = Member(100, sceneId: 7, isSelf: true);   // self, in-instance, already active
        var silentHere = Member(200, sceneId: 7);                 // in-instance, silent -> inject a 0 row
        var elsewhere  = Member(300, sceneId: 9);                 // another floor / town -> MUST be skipped
        var stats = new Dictionary<EntityId, SourceStats>
        {
            [self.EntityId] = new SourceStats { TotalDamage = 500 },   // already-active member
        };

        Plugin.EnsurePartyMembersTracked(new[] { self, silentHere, elsewhere }, stats);

        Assert.Equal(2, stats.Count);
        Assert.Equal(500, stats[self.EntityId].TotalDamage);       // active row NOT overwritten
        Assert.True(stats.ContainsKey(silentHere.EntityId));       // in-instance silent member present …
        Assert.Equal(0, stats[silentHere.EntityId].TotalDamage);   // … as a 0/0/0 actor
        Assert.False(stats.ContainsKey(elsewhere.EntityId));       // out-of-instance member NOT injected
    }

    [Fact]
    public void EnsurePartyMembersTracked_empty_roster_is_a_noop_for_solo()
    {
        var self  = Member(100).EntityId;
        var stats = new Dictionary<EntityId, SourceStats> { [self] = new SourceStats { TotalDamage = 7 } };

        Plugin.EnsurePartyMembersTracked(System.Array.Empty<PartyMember>(), stats);

        Assert.Single(stats);
        Assert.Equal(7, stats[self].TotalDamage);
    }
}
