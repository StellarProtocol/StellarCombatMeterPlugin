using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Test class split into feature-named partials (2026-07-26, review round) — the file was 665 LoC
// at the fix/bosskill-settle branch base and reached 820 LoC (past the project's 800-LoC blocker
// threshold) once Tasks 1-9 added BossKill + the end-to-end sequence coverage. This base file
// holds only the fixtures every partial shares:
//   AutoArchiveEngineTests.Wipe.cs      wipe episode/latch + revive-grace/ignore-solo
//   AutoArchiveEngineTests.Boss.cs      boss-phase invariant, BossKill, end-to-end sequence pin
//   AutoArchiveEngineTests.Stage.cs     stage/flow transitions + the wipe/stage overlap pin
//   AutoArchiveEngineTests.Idle.cs      idle timeout + re-fire guard
//   AutoArchiveEngineTests.Cooldown.cs  inline boss-cut gate + the shared cooldown/toggle gates
// Every test name is unchanged from the pre-split file — this is a pure file-layout move, not a
// content edit.
public partial class AutoArchiveEngineTests
{
    // Baseline live-combat snapshot: 60s of content, damage 61s ago at now=200_000 (not idle),
    // full 4-man roster alive, in a run, no boss, flow version 1 pre-adopted via first Evaluate.
    private static AutoArchiveInputs Live(long nowMs = 200_000) => new()
    {
        NowMs = nowMs, CombatActive = true, CombatStartMs = 100_000, LastDamageMs = 160_000,
        HasStats = true, RosterSize = 4, DeadCount = 0, UnknownCount = 0,
        OutcomeFailed = false, BossPresent = false, BossGone = false, BossDead = false,
        InstancedRun = true, FlowStateVersion = 1,
    };

    private static AutoArchiveEngine Armed(AutoArchiveInputs baseline)
    {
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(in baseline));   // adopt flow version / arm latches silently
        return e;
    }
}
