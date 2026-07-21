using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Idle-settle guard (2026-07-18): an AUTO-triggered archive (floor-clear stage change, wipe, boss,
// idle) must NOT commit the instant the engine fires — the mobs' corpses linger and trailing DoT /
// killing-blow ticks are still landing, so snapshotting immediately loses the last hits from the
// record. Instead the pending archive waits until combat has gone QUIET: no combat event of any
// channel (dealt/heal/taken, tracked by _lastCombatEventMs) for ArchiveIdleSettleMs — every event
// RESETS that window. If it's already been that quiet when the trigger fires, the commit is
// immediate. A backstop cap (ArchiveIdleCapMs since the trigger armed) prevents an indefinite defer
// during sustained combat. A MANUAL button/hotkey archive (and the scene-change archive, which must
// beat the teardown) stays immediate. The decisions are pure statics so they unit-test headless
// (Plugin can't be instantiated — the AutoArchiveEngine / ShouldSuppressAutoArchive precedent).
public class AutoArchiveSettleDelayTests
{
    // ---- PendingArchiveDue: quiet-window timing (nowMs - lastCombatEventMs >= idleSettleMs) ----

    [Fact]
    public void Not_due_while_combat_still_updating_under_two_seconds()
        // last combat event 1.5 s ago — window not yet elapsed
        => Assert.False(Plugin.PendingArchiveDue(nowMs: 11_500, lastCombatEventMs: 10_000, idleSettleMs: 2_000));

    [Fact]
    public void Not_due_one_ms_before_the_quiet_window_closes()
        => Assert.False(Plugin.PendingArchiveDue(nowMs: 11_999, lastCombatEventMs: 10_000, idleSettleMs: 2_000));

    [Fact]
    public void Due_exactly_at_two_seconds_of_no_combat()
        => Assert.True(Plugin.PendingArchiveDue(nowMs: 12_000, lastCombatEventMs: 10_000, idleSettleMs: 2_000));

    [Fact]
    public void Due_after_more_than_two_seconds_of_no_combat()
        => Assert.True(Plugin.PendingArchiveDue(nowMs: 13_000, lastCombatEventMs: 10_000, idleSettleMs: 2_000));

    [Fact]
    public void Due_immediately_when_already_quiet_at_arm_time()
        // trigger fires (now) but the last combat event was 5 s ago — already past the window, commit now
        => Assert.True(Plugin.PendingArchiveDue(nowMs: 15_000, lastCombatEventMs: 10_000, idleSettleMs: 2_000));

    [Fact]
    public void A_fresh_combat_event_resets_the_window()
    {
        // At now=13_000 with last event at 10_000 the window is closed (due)…
        Assert.True(Plugin.PendingArchiveDue(nowMs: 13_000, lastCombatEventMs: 10_000, idleSettleMs: 2_000));
        // …but a trailing DoT tick at 12_900 pushes lastCombatEventMs forward, re-opening the wait.
        Assert.False(Plugin.PendingArchiveDue(nowMs: 13_000, lastCombatEventMs: 12_900, idleSettleMs: 2_000));
    }

    // ---- PendingArchiveCapped: the backstop against an indefinite defer during sustained combat ----

    [Fact]
    public void Not_capped_before_the_cap_elapses()
        => Assert.False(Plugin.PendingArchiveCapped(nowMs: 14_000, armedMs: 0, capMs: 15_000));

    [Fact]
    public void Capped_once_the_cap_elapses_since_arm()
        => Assert.True(Plugin.PendingArchiveCapped(nowMs: 15_000, armedMs: 0, capMs: 15_000));

    // ---- the idle-settle window + cap are sane and inside the game's ~5 s "next floor" gate ----

    [Fact]
    public void Idle_settle_is_about_two_seconds_and_under_the_next_floor_window()
        // The settle window became a prefs-configurable field (Task 4); DefaultArchiveSettleMs is the
        // named default value (the AutoArchiveEngine.DefaultCooldownMs precedent) — same assertions,
        // same values, just pointed at the renamed symbol so this stays pinned through the config change.
    {
        Assert.InRange(Plugin.DefaultArchiveSettleMs, 1_000L, 4_000L);
        Assert.True(Plugin.DefaultArchiveSettleMs < 5_000L,
            "idle-settle window must commit well before the game's ~5 s next-floor load");
        Assert.True(Plugin.ArchiveIdleCapMs > Plugin.DefaultArchiveSettleMs,
            "the backstop cap must be longer than the idle-settle window");
    }

    // ---- IsDeferrableArchive: only engine-driven AUTO reasons defer; manual + scene stay immediate ----

    [Fact]
    public void Manual_archive_is_never_deferred()
        => Assert.False(Plugin.IsDeferrableArchive(AutoArchive.ArchiveReason.Manual));

    [Fact]
    public void SceneChange_archive_is_never_deferred()   // must beat the entity teardown at the boundary
        => Assert.False(Plugin.IsDeferrableArchive(AutoArchive.ArchiveReason.SceneChange));

    [Fact]
    public void StageChange_floor_clear_is_deferred()     // the motivating case — trailing DoTs after a floor clear
        => Assert.True(Plugin.IsDeferrableArchive(AutoArchive.ArchiveReason.StageChange));

    [Fact]
    public void Wipe_archive_is_deferred()
        => Assert.True(Plugin.IsDeferrableArchive(AutoArchive.ArchiveReason.Wipe));

    [Fact]
    public void BossPhase_archive_is_deferred()
        => Assert.True(Plugin.IsDeferrableArchive(AutoArchive.ArchiveReason.BossPhase));

    [Fact]
    public void Idle_archive_is_deferred()
        => Assert.True(Plugin.IsDeferrableArchive(AutoArchive.ArchiveReason.Idle));
}
