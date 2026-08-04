using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Idle-settle guard (2026-07-18): an AUTO-triggered archive (floor-clear stage change, wipe, boss,
// idle) must NOT commit the instant the engine fires — the mobs' corpses linger and trailing DoT /
// killing-blow ticks are still landing, so snapshotting immediately loses the last hits from the
// record. Instead the pending archive waits until the relevant activity clock has gone QUIET for
// ArchiveIdleSettleMs — every fresh activity event RESETS that window. Owner ruling 2026-07-26: the
// clock is DAMAGE-only (heals/damage-taken never reset it) — production feeds PendingArchiveDue
// _lastDamageMs directly via its lastActivityMs parameter (a caller-selected clock, not any one fixed
// field, so the seam still unit-tests without a live Plugin).
//
// SUPERSEDED (owner ruling 2026-07-28, defect 2 of the bosskill-settle branch's raid-testing fixes):
// the 2026-07-26 fix ALSO narrowed a BossKill pending to watch damage aimed at the boss specifically
// (SettleClockMs(reason, lastDamageMs, lastBossDamageMs) + IsSettleBossDamage + the Plugin fields
// _settleBossId/_lastBossDamageMs feeding it — ALL now RETIRED), reasoning that add cleanup elsewhere
// shouldn't hold the boss archive open. That reading was wrong: the owner reported residual damage at
// the head of the FOLLOWING archive ("there's mini dps that left to early of 2,4,6") — quietMs looked
// satisfied (>= settle) because the boss-only clock had gone quiet, but adds/DoTs elsewhere kept
// landing and spilled into the next segment's head. Corrected ruling: the boss's death only STARTS the
// settle timer; the window itself watches ALL damage — the SAME general clock (_lastDamageMs) as every
// other reason. Heals still never count (the half of the 2026-07-26 ruling that stands — _lastDamageMs
// is only ever stamped by AccumulateDamage, player-source non-heal damage). RED/GREEN captured against
// the retired SettleClockMs before deletion (see the branch-fix-report's third wave for the exact
// assertion); the four tests that pinned the withdrawn boss-only contract are deleted below, per-test,
// with the same rationale repeated at each site (house style: a withdrawn contract is documented, not
// silently dropped). See the 2026-07-26-combatmeter-bosskill-settle-design.md spec's corrected §2.6.
// If it's already been that quiet when the trigger fires, the commit is immediate. A backstop cap
// (ArchiveIdleCapMs since the trigger armed) prevents an indefinite defer during sustained combat. A
// MANUAL button/hotkey archive (and the scene-change archive, which must beat the teardown) stays
// immediate. The decisions are pure statics so they unit-test headless (Plugin can't be instantiated —
// the AutoArchiveEngine / ShouldSuppressAutoArchive precedent).
public class AutoArchiveSettleDelayTests
{
    // ---- PendingArchiveDue: quiet-window timing (nowMs - lastActivityMs >= idleSettleMs) ----

    [Fact]
    public void Not_due_while_combat_still_updating_under_two_seconds()
        // last combat event 1.5 s ago — window not yet elapsed
        => Assert.False(Plugin.PendingArchiveDue(nowMs: 11_500, lastActivityMs: 10_000, idleSettleMs: 2_000));

    [Fact]
    public void Not_due_one_ms_before_the_quiet_window_closes()
        => Assert.False(Plugin.PendingArchiveDue(nowMs: 11_999, lastActivityMs: 10_000, idleSettleMs: 2_000));

    [Fact]
    public void Due_exactly_at_two_seconds_of_no_combat()
        => Assert.True(Plugin.PendingArchiveDue(nowMs: 12_000, lastActivityMs: 10_000, idleSettleMs: 2_000));

    [Fact]
    public void Due_after_more_than_two_seconds_of_no_combat()
        => Assert.True(Plugin.PendingArchiveDue(nowMs: 13_000, lastActivityMs: 10_000, idleSettleMs: 2_000));

    [Fact]
    public void Due_immediately_when_already_quiet_at_arm_time()
        // trigger fires (now) but the last combat event was 5 s ago — already past the window, commit now
        => Assert.True(Plugin.PendingArchiveDue(nowMs: 15_000, lastActivityMs: 10_000, idleSettleMs: 2_000));

    [Fact]
    public void A_fresh_combat_event_resets_the_window()
    {
        // At now=13_000 with last event at 10_000 the window is closed (due)…
        Assert.True(Plugin.PendingArchiveDue(nowMs: 13_000, lastActivityMs: 10_000, idleSettleMs: 2_000));
        // …but a trailing DoT tick at 12_900 pushes lastActivityMs forward, re-opening the wait.
        Assert.False(Plugin.PendingArchiveDue(nowMs: 13_000, lastActivityMs: 12_900, idleSettleMs: 2_000));
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
    public void BossPhase_archive_is_immediate()
        // RE-PINNED (Task 7, 2026-07-21): BossPhase used to defer (Assert.True) alongside the other AUTO
        // reasons. It is now IMMEDIATE — the boss cut moved INLINE into Plugin.Capture.cs
        // (MaybeCutForBossPhase), firing at the first boss hit BEFORE the hit is accumulated so the boss
        // fight is one clean segment. The old deferred path hit the 15 s settle cap MID-FIGHT and chopped
        // the fight (owner-reported). Deliberate contract change → re-pinned in the same commit per the
        // agent process rules (a pinned test that changed contract is re-pinned with rationale, not
        // silently deleted). Should a BossPhase reason ever reach the settle path it must NOT defer.
        => Assert.False(Plugin.IsDeferrableArchive(AutoArchive.ArchiveReason.BossPhase));

    [Fact]
    public void Idle_archive_is_deferred()
        => Assert.True(Plugin.IsDeferrableArchive(AutoArchive.ArchiveReason.Idle));

    [Fact]
    public void BossKill_archive_is_deferred()
        // The whole point of the 2026-07-26 fix: a confirmed boss death waits out the settle window so
        // the post-kill tail (trailing DoTs, killing blow) lands INSIDE the fight's archive. Contrast
        // BossPhase_archive_is_immediate above — the trash->boss cut must stay immediate.
        => Assert.True(Plugin.IsDeferrableArchive(AutoArchive.ArchiveReason.BossKill));

    // ---- which clock the settle window watches ----
    //
    // RETIRED (owner ruling 2026-07-28, defect 2 — see the file banner above for the full story):
    // SettleClockMs(reason, lastDamageMs, lastBossDamageMs) narrowed a BossKill pending to a
    // boss-targeted clock. That contract is withdrawn — the settle window now watches the SAME general
    // damage clock (_lastDamageMs) for every reason, no per-reason branching, so the pure selection
    // function collapsed to an identity and was deleted rather than kept as vestigial indirection
    // (Plugin.AutoArchive.cs). Four tests pinned the withdrawn contract and are deleted with it, none
    // replaced 1:1 — see why below each:
    //   • BossKill_settle_watches_boss_damage — pinned the boss-only narrowing itself. No replacement:
    //     there is no longer a decision to pin (every reason uses the one clock).
    //   • BossKill_settle_falls_back_to_general_damage_when_no_boss_damage_was_seen — pinned the
    //     fallback branch that only existed because of the narrowing above.
    //   • Other_reasons_settle_on_general_damage — pinned the (now meaningless) DISTINCTION between
    //     BossKill and "other reasons" — every reason IS "other reasons" now, so asserting it is a
    //     tautology (PendingArchiveDue already exercises the general clock exhaustively above).
    //   • Post_kill_heals_do_not_extend_a_bosskill_window — the "heals don't count" guarantee is real
    //     (_lastDamageMs is only ever stamped by AccumulateDamage — player-source, non-heal damage; see
    //     its field doc in Plugin.cs) but lives entirely in Plugin-side wiring that cannot be driven
    //     headless (Plugin cannot be instantiated in tests — the same accepted-residual limitation
    //     BossStatus's mark-before-clear ordering already carries in this codebase). A rewrite that
    //     dropped the SettleClockMs call would just re-assert PendingArchiveDue's own arithmetic — every
    //     shape of that is already covered by the PendingArchiveDue block above (e.g.
    //     Due_exactly_at_two_seconds_of_no_combat), so it would be a duplicate, not new coverage.
    //     Deleted rather than padded with a test that pins nothing new.

    // ---- pre-emption (owner ruling 2026-07-26) ----

    [Fact]
    public void A_pending_archive_is_preempted_by_a_fresh_boss_engagement()
        // With damage-only settle this should be unreachable (the previous fight closes ~2 s after its
        // last hit, long before the next pull) — the guard exists so the new fight's opener leaking into
        // the previous boss's archive is structurally impossible rather than merely unlikely.
        => Assert.True(Plugin.ShouldPreemptPendingForBoss(hasPending: true));

    [Fact]
    public void No_pending_means_nothing_to_preempt()
        => Assert.False(Plugin.ShouldPreemptPendingForBoss(hasPending: false));
}
