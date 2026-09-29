using System;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// "Map #9" on the Master Seal tab (owner "yes fix both", 2026-09-29; prod levelUuid 21701554918653952,
/// session BYAo7hMspJ). Between two Cursed Radiant Tomb M6 runs a party fought 92 s in Bahamar Highlands
/// (SceneTable 9, SceneType 1 = field). The field archive fired as they zoned into CRT #2 and uploaded with
/// CRT #2's levelUuid, Master 6, CRT #1's dungeonStartMs (→ the server's 570 s "duration") and `kill`.
///
/// Root cause: the framework keeps the previous dungeon's outcome/settlement/difficulty/run-timer across
/// the drop-to-0 into the field (DungeonStateService.SetCurrentRun clears them only when the NEXT non-zero
/// id latches), and BuildHistoryEntry stamped whatever it saw — including the latch==0 CurrentRunId
/// fallback, which by then had advanced to the next dungeon.
///
/// <c>Plugin</c> can't be instantiated headless (IL2CPP-bound), so the exact sequence is replayed over a
/// faithful model of the framework's DungeonStateService lifecycle (<see cref="SimDungeon"/>) and of the
/// plugin's latch lifecycle (<see cref="SimMeter"/> — EnsureCombatStarted / TrackClearLatch /
/// BankRunBoundary, built from the plugin's OWN pure seams), with every archive stamped through the ONE
/// production resolver, <see cref="Plugin.ResolveArchiveStamp"/>. PINNED — never weaken.
/// </summary>
public sealed class FieldArchiveIdentityTests
{
    private const int KindField = 1, KindInstanced = 2;

    // Real ids/times from the prod timeline (diagnosis §1).
    private const long Crt1Id = 21420079941943296, Crt2Id = 21701554918653952;
    private const long Crt1StartMs = 1790325459000, Crt2StartMs = 1790326058000;
    private const long FieldFightStartMs = 1790325937000, FieldArchiveMs = 1790326029000;   // 92 s

    // ── The exact prod sequence ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Crt_kill_then_92s_field_fight_then_crt_again_field_archive_carries_no_dungeon_identity()
    {
        var (dungeon, meter) = (new SimDungeon(), new SimMeter());

        // CRT #1 (M6): enter, fight, clear, leave to the field → its own scene archive.
        dungeon.EnterRun(Crt1Id, difficulty: 6, timerStartMs: Crt1StartMs);
        meter.CombatStart(dungeon);
        dungeon.Clear(passTime: 468);
        meter.Tick(dungeon);
        dungeon.SetCurrentRun(0);                                     // wire enter-scene: field → run id 0
        var crt1 = meter.Archive(dungeon, KindInstanced);            // scene archive of the OUTGOING dungeon
        meter.BankRunBoundary();

        // Field (Bahamar Highlands): 92 s of combat with the framework's stale CRT #1 state still in view.
        meter.CombatStart(dungeon);
        meter.Tick(dungeon);                                          // stale LastOutcome=Success re-latches the clear
        // Zone into CRT #2: the wire enter-scene latches the NEW id first (framework clears the stale state,
        // Master 6 arrives), THEN SceneChanged fires the field's scene archive.
        dungeon.EnterRun(Crt2Id, difficulty: 6, timerStartMs: 0);
        var field = meter.Archive(dungeon, KindField);

        Assert.Equal(0, field.LevelUuid);           // own identity = none (never CRT #2's id)
        Assert.Equal(0, field.DifficultyLevel);     // no Master level
        Assert.Equal(0, field.DungeonStartMs);      // never CRT #1's start
        Assert.Equal(0, field.Defeated);
        Assert.Null(field.Settlement);              // passTime / masterModeScore / totalScore all 0
        Assert.NotEqual("kill", field.Verdict);
        Assert.Equal("partial", field.Verdict);

        // The uploaded header, built from that entry, spans the fight's OWN 92 s and names no dungeon.
        var enc = CombatLogAssembler.BuildEncounter(EntryFrom(field, "9", FieldFightStartMs, FieldArchiveMs));
        Assert.Equal(92_000, enc.DurationMs);
        Assert.Equal(0, enc.DungeonStartMs);
        Assert.Equal(0, enc.DifficultyLevel);
        Assert.Equal(0, enc.LevelUuid);
        Assert.Equal(9, enc.MapId);

        // CRT #1 was stamped as ITSELF (unchanged dungeon path).
        Assert.Equal(Crt1Id, crt1.LevelUuid);
        Assert.Equal(6, crt1.DifficultyLevel);
        Assert.Equal(Crt1StartMs, crt1.DungeonStartMs);
        Assert.Equal("kill", crt1.Verdict);
        Assert.Equal(468, crt1.Settlement!.Value.PassTimeSeconds);
    }

    [Fact]
    public void The_same_sequence_stamped_the_old_way_reproduces_the_prod_bug()
    {
        // Proves the model reproduces the measured failure (so the fix above is tested against the real
        // shape, not a strawman): the pre-fix expressions give CRT #2's id, M6, CRT #1's start and `kill`.
        var (dungeon, meter) = (new SimDungeon(), new SimMeter());
        dungeon.EnterRun(Crt1Id, 6, Crt1StartMs); meter.CombatStart(dungeon); dungeon.Clear(468); meter.Tick(dungeon);
        dungeon.SetCurrentRun(0); meter.Archive(dungeon, KindInstanced); meter.BankRunBoundary();
        meter.CombatStart(dungeon); meter.Tick(dungeon);
        dungeon.EnterRun(Crt2Id, 6, 0);

        var old = OldStamp(meter.Inputs(dungeon, KindField));
        Assert.Equal(Crt2Id, old.LevelUuid);
        Assert.Equal(6, old.DifficultyLevel);
        Assert.Equal(Crt1StartMs, old.DungeonStartMs);
        Assert.Equal("kill", old.Verdict);
    }

    [Fact]
    public void The_two_crt_runs_keep_their_own_ids_and_starts_and_never_merge()
    {
        var (dungeon, meter) = (new SimDungeon(), new SimMeter());
        dungeon.EnterRun(Crt1Id, 6, Crt1StartMs); meter.CombatStart(dungeon); dungeon.Clear(468); meter.Tick(dungeon);
        dungeon.SetCurrentRun(0);
        var crt1 = meter.Archive(dungeon, KindInstanced); meter.BankRunBoundary();
        meter.CombatStart(dungeon); meter.Tick(dungeon);
        dungeon.EnterRun(Crt2Id, 6, 0);
        var field = meter.Archive(dungeon, KindField); meter.BankRunBoundary();

        // CRT #2: the run timer lands, combat, clear (pass 579), leave.
        dungeon.SetRunTimer(Crt2StartMs);
        meter.CombatStart(dungeon);
        dungeon.Clear(passTime: 579);
        meter.Tick(dungeon);
        dungeon.SetCurrentRun(0);
        var crt2 = meter.Archive(dungeon, KindInstanced);

        Assert.Equal(Crt2Id, crt2.LevelUuid);
        Assert.Equal(Crt2StartMs, crt2.DungeonStartMs);
        Assert.Equal(6, crt2.DifficultyLevel);
        Assert.Equal("kill", crt2.Verdict);
        Assert.Equal(579, crt2.Settlement!.Value.PassTimeSeconds);
        // Three archives, three distinct server keys (levelUuid, dungeonStartMs/1000) — the field one is
        // keyless (0) and refused at upload; neither CRT run shares a key with anything.
        Assert.NotEqual((crt1.LevelUuid, crt1.DungeonStartMs), (crt2.LevelUuid, crt2.DungeonStartMs));
        Assert.NotEqual(crt2.LevelUuid, field.LevelUuid);
        Assert.NotEqual(crt1.LevelUuid, field.LevelUuid);
    }

    [Fact]
    public void A_stale_failed_outcome_does_not_make_a_field_fight_a_fail()
    {
        var (dungeon, meter) = (new SimDungeon(), new SimMeter());
        dungeon.EnterRun(Crt1Id, 6, Crt1StartMs); meter.CombatStart(dungeon);
        dungeon.Outcome = DungeonOutcome.Failed;
        dungeon.SetCurrentRun(0);
        Assert.Equal("fail", meter.Archive(dungeon, KindInstanced).Verdict);   // the wipe itself: unchanged
        meter.BankRunBoundary();
        meter.CombatStart(dungeon);
        Assert.Equal("partial", meter.Archive(dungeon, KindField).Verdict);
    }

    [Fact]
    public void A_field_archive_with_a_carried_over_clear_never_banks_an_empty_clear_marker()
    {
        // ManualArchive's empty-marker gate reads the SAME stamp verdict: with no stats in the field and
        // the clear latch still set from the dungeon, no "kill" marker may bank there.
        var (dungeon, meter) = (new SimDungeon(), new SimMeter());
        dungeon.EnterRun(Crt1Id, 6, Crt1StartMs); meter.CombatStart(dungeon); dungeon.Clear(468); meter.Tick(dungeon);
        dungeon.SetCurrentRun(0); meter.Archive(dungeon, KindInstanced); meter.BankRunBoundary();
        meter.CombatStart(dungeon); meter.Tick(dungeon);
        var verdict = meter.Archive(dungeon, KindField).Verdict;
        Assert.False(Plugin.ShouldBankEmptyClearMarker(
            AutoArchive.ArchiveReason.SceneChange, verdict, alreadyBankedThisRun: false));
    }

    // ── World bosses / field content keep working ────────────────────────────────────────────────────

    [Fact]
    public void World_dominator_is_instanced_content_and_keeps_its_own_identity_and_kill()
    {
        // World Dominator 7150-7152 is SceneType 2 (like dungeons/raids): the dungeon path, untouched.
        const long wdId = 5094642961874944, wdStart = 1790000000000;
        var (dungeon, meter) = (new SimDungeon(), new SimMeter());
        dungeon.EnterRun(wdId, difficulty: 0, timerStartMs: wdStart);
        meter.CombatStart(dungeon);
        dungeon.Clear(passTime: 95);
        meter.Tick(dungeon);
        dungeon.SetCurrentRun(0);
        var wd = meter.Archive(dungeon, KindInstanced);
        Assert.Equal(wdId, wd.LevelUuid);
        Assert.Equal(wdStart, wd.DungeonStartMs);
        Assert.Equal("kill", wd.Verdict);
        Assert.Equal(95, wd.Settlement!.Value.PassTimeSeconds);
    }

    [Fact]
    public void A_world_boss_kill_on_a_field_map_still_archives_as_field_content()
    {
        // An open-world boss/elite on a SceneType-1 map: no run id exists (framework gate), so it archives
        // as the field fight it is — own window, no dungeon values. The banking decision is unchanged
        // (non-zero stats bank; ShouldSuppressAutoArchive is not touched by this fix).
        var (dungeon, meter) = (new SimDungeon(), new SimMeter());
        meter.CombatStart(dungeon);
        var fieldBoss = meter.Archive(dungeon, KindField);
        Assert.Equal(new Plugin.ArchiveStamp(0, 0, 0, 0, null, "partial"), fieldBoss);
        Assert.False(Plugin.ShouldSuppressAutoArchive(AutoArchive.ArchiveReason.BossKill,
            carriesFreshResult: false, allRowsZero: false));
    }

    [Fact]
    public void Only_a_settlement_fresh_to_the_field_fight_itself_can_make_it_kill()
    {
        // "Only a real kill of its own may make it kill": a settlement that CHANGED after this fight's
        // combat start (IsFreshKill, baseline-relative) counts; the carried-over one never does.
        var fresh = new DungeonSettlementInfo(0, 120, 0);
        var s = Plugin.ResolveArchiveStamp(Inputs(latchedRunId: 0, kind: KindField,
            fresh: fresh, cleared: true, outcome: DungeonOutcome.Success));
        Assert.Equal("kill", s.Verdict);
        Assert.Equal(fresh, s.Settlement);
        Assert.Equal(0, s.LevelUuid);
    }

    // ── The field predicate: both terms required ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, 1, true)]      // open world / town, no run id → field
    [InlineData(0L, 0, true)]      // login/memory scene kind → field
    [InlineData(0L, 2, false)]     // instanced scene, run id not latched yet → dungeon (live fallback kept)
    [InlineData(0L, null, false)]  // scene table unknown → dungeon stamping (never strip on doubt)
    [InlineData(Crt1Id, 1, false)] // a latched run id wins (stale _lastSceneName after a missed SceneChanged)
    [InlineData(Crt1Id, 2, false)]
    public void IsFieldArchive_requires_no_latched_run_id_and_a_known_non_instanced_scene(
        long latchedRunId, int? kind, bool expected)
        => Assert.Equal(expected, Plugin.IsFieldArchive(latchedRunId, kind));

    [Fact]
    public void Unknown_scene_kind_keeps_the_live_run_id_fallback()
        => Assert.Equal(Crt2Id, Plugin.ResolveArchiveStamp(Inputs(latchedRunId: 0, kind: null)).LevelUuid);

    [Fact]
    public void Instanced_scene_with_no_latched_id_keeps_the_live_run_id_fallback()
        => Assert.Equal(Crt2Id, Plugin.ResolveArchiveStamp(Inputs(latchedRunId: 0, kind: KindInstanced)).LevelUuid);

    [Fact]
    public void A_latched_run_id_in_a_field_named_scene_keeps_the_full_dungeon_stamp()
    {
        var i = Inputs(latchedRunId: Crt1Id, kind: KindField, cleared: true);
        Assert.Equal(OldStamp(i), Plugin.ResolveArchiveStamp(i));
    }

    // ── The dungeon branch is byte-identical to the pre-fix stamping ─────────────────────────────────

    [Fact]
    public void Every_non_field_archive_is_stamped_exactly_as_before()
    {
        var rng = new Random(20260929);
        int?[] kinds = { null, 0, 1, 2, 3 };
        DungeonSettlementInfo?[] settles = { null, new(0, 0, 0), new(59, 0, 0), new(0, 700, 686), new(0, 0, 40) };
        for (var n = 0; n < 5000; n++)
        {
            var i = new Plugin.ArchiveStampInputs(
                LatchedRunId: rng.Next(3) == 0 ? 0 : rng.Next(1, 1 << 30),
                LiveRunId: rng.Next(3) == 0 ? 0 : rng.Next(1, 1 << 30),
                LatchedDifficulty: rng.Next(0, 21), LiveDifficulty: rng.Next(0, 21),
                LatchedRunStartMs: rng.Next(2) == 0 ? 0 : rng.Next(1, int.MaxValue),
                LiveRunStartMs: rng.Next(2) == 0 ? 0 : rng.Next(1, int.MaxValue),
                LiveDefeated: rng.Next(0, 6),
                FreshSettlement: settles[rng.Next(settles.Length)],
                ClearedSettlement: settles[rng.Next(settles.Length)],
                Outcome: (DungeonOutcome)rng.Next(0, 3), ClearedThisRun: rng.Next(2) == 0,
                SceneKind: kinds[rng.Next(kinds.Length)]);
            if (Plugin.IsFieldArchive(i.LatchedRunId, i.SceneKind)) continue;
            Assert.Equal(OldStamp(i), Plugin.ResolveArchiveStamp(i));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    // The pre-2026-09-29 BuildHistoryEntry expressions, verbatim — the reference the dungeon branch must match.
    private static Plugin.ArchiveStamp OldStamp(in Plugin.ArchiveStampInputs i) => new(
        i.LatchedRunId != 0 ? i.LatchedRunId : i.LiveRunId,
        Math.Max(i.LatchedDifficulty, i.LiveDifficulty),
        Plugin.LatchRunStartMs(i.LatchedRunStartMs, i.LiveRunStartMs),
        i.LiveDefeated,
        i.FreshSettlement ?? i.ClearedSettlement,
        Plugin.ResolveVerdict(i.FreshSettlement, i.Outcome, i.ClearedThisRun));

    private static Plugin.ArchiveStampInputs Inputs(long latchedRunId, int? kind,
        DungeonSettlementInfo? fresh = null, bool cleared = false, DungeonOutcome outcome = DungeonOutcome.None)
        => new(latchedRunId, Crt2Id, 6, 6, Crt1StartMs, 0, 3, fresh,
               new DungeonSettlementInfo(468, 0, 0), outcome, cleared, kind);

    private static Plugin.EncounterHistoryEntry EntryFrom(Plugin.ArchiveStamp s, string scene, long enter, long arch)
        => new()
        {
            SceneName = scene, EnteredAtMs = enter, ArchivedAtMs = arch,
            LevelUuid = s.LevelUuid, DifficultyLevel = s.DifficultyLevel, DungeonStartMs = s.DungeonStartMs,
            Defeated = s.Defeated, Result = s.Verdict,
            PassTime = s.Settlement?.PassTimeSeconds ?? 0, MasterModeScore = s.Settlement?.MasterModeScore ?? 0,
        };

    /// <summary>Model of the framework's DungeonStateService lifecycle (Stellar.Application): state is
    /// cleared ONLY when a genuinely new non-zero run id latches; the drop to 0 (field/town) KEEPS the
    /// previous run's outcome/settlement/difficulty/timer — the root of this bug.</summary>
    private sealed class SimDungeon
    {
        public long CurrentRunId, RunTimerStartMs;
        public int CurrentDifficulty, Defeated;
        public DungeonOutcome Outcome;
        public DungeonSettlementInfo? Settlement;

        public void SetCurrentRun(long id)
        {
            var previous = CurrentRunId;
            CurrentRunId = id;
            if (id != 0 && previous != id)
                (Settlement, CurrentDifficulty, RunTimerStartMs, Outcome, Defeated) = (null, 0, 0, DungeonOutcome.None, 0);
        }

        public void SetRunTimer(long ms) { if (ms != 0 && RunTimerStartMs == 0) RunTimerStartMs = ms; }

        public void EnterRun(long id, int difficulty, long timerStartMs)
        {
            SetCurrentRun(id);
            CurrentDifficulty = difficulty;
            SetRunTimer(timerStartMs);
        }

        public void Clear(int passTime) { Outcome = DungeonOutcome.Success; Settlement = new(passTime, 0, 0); }
    }

    /// <summary>Model of the plugin's run-scoped latches, driven through the plugin's own pure seams in the
    /// same order Plugin.Capture.cs (EnsureCombatStarted), Plugin.AutoArchive.cs (TrackClearLatch) and
    /// Plugin.RunBoundary.cs (BankRunBoundary) apply them.</summary>
    private sealed class SimMeter
    {
        private long _lastRunId, _lastRunStartMs;
        private int _difficultyAtCombatStart;
        private bool _clearedThisRun;
        private DungeonSettlementInfo? _clearedSettlement, _settlementAtCombatStart;

        public void CombatStart(SimDungeon d)
        {
            _clearedThisRun = false; _clearedSettlement = null;
            _lastRunId = d.CurrentRunId;
            _lastRunStartMs = Plugin.LatchRunStartMs(_lastRunStartMs, d.RunTimerStartMs);
            _settlementAtCombatStart = d.Settlement;
            _difficultyAtCombatStart = d.CurrentDifficulty;
        }

        public void Tick(SimDungeon d)
        {
            var hasFreshClear = Plugin.ResolveVerdict(Fresh(d), d.Outcome) == "kill";
            (_clearedThisRun, _clearedSettlement) = Plugin.UpdateClearLatch(
                _clearedThisRun, _clearedSettlement, hasFreshClear, d.Settlement);
        }

        public void BankRunBoundary() { _lastRunId = 0; _lastRunStartMs = 0; }

        public Plugin.ArchiveStamp Archive(SimDungeon d, int? sceneKind)
            => Plugin.ResolveArchiveStamp(Inputs(d, sceneKind));

        public Plugin.ArchiveStampInputs Inputs(SimDungeon d, int? sceneKind) => new(
            _lastRunId, d.CurrentRunId, _difficultyAtCombatStart, d.CurrentDifficulty,
            _lastRunStartMs, d.RunTimerStartMs, d.Defeated,
            Fresh(d), _clearedSettlement, d.Outcome, _clearedThisRun, sceneKind);

        private DungeonSettlementInfo? Fresh(SimDungeon d)
            => Plugin.IsFreshKill(d.Settlement, _settlementAtCombatStart) ? d.Settlement : null;
    }
}
