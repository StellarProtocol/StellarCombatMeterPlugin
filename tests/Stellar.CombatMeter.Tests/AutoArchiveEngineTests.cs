using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class AutoArchiveEngineTests
{
    // Baseline live-combat snapshot: 60s of content, damage 61s ago at now=200_000 (not idle),
    // full 4-man roster alive, in a run, no boss, flow version 1 pre-adopted via first Evaluate.
    private static AutoArchiveInputs Live(long nowMs = 200_000) => new()
    {
        NowMs = nowMs, CombatActive = true, CombatStartMs = 100_000, LastDamageMs = 160_000,
        HasStats = true, RosterSize = 4, DeadCount = 0, UnknownCount = 0,
        OutcomeFailed = false, BossPresent = false, BossGone = false,
        InstancedRun = true, FlowStateVersion = 1,
    };

    private static AutoArchiveEngine Armed(AutoArchiveInputs baseline)
    {
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(in baseline));   // adopt flow version / arm latches silently
        return e;
    }

    // ---- wipe ----

    [Fact]
    public void Wipe_fires_when_every_member_reads_dead()
    {
        var e = Armed(Live());
        var s = Live() with { DeadCount = 4 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in s));
    }

    [Fact]
    public void Wipe_blocked_by_unknown_vitals_members()
    {
        var e = Armed(Live());
        var s = Live() with { DeadCount = 3, UnknownCount = 1, RosterSize = 4 };
        Assert.Null(e.Evaluate(in s));
    }

    [Fact]
    public void Wipe_latches_until_a_revive_then_rearms()
    {
        var e = Armed(Live());
        var dead = Live() with { DeadCount = 4 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in dead));
        e.OnArchived(dead.NowMs, ArchiveReason.Wipe);
        var later = dead with { NowMs = dead.NowMs + AutoArchiveEngine.CooldownMs + 1 };
        Assert.Null(e.Evaluate(in later));                       // still all dead — latched
        var revived = later with { DeadCount = 3, NowMs = later.NowMs + 1000 };
        Assert.Null(e.Evaluate(in revived));                     // re-armed, but nobody's wiped
        var deadAgain = revived with { DeadCount = 4, NowMs = revived.NowMs + 1000 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in deadAgain));
    }

    [Fact]
    public void Wipe_fires_on_outcome_failed_edge()
    {
        var e = Armed(Live());
        var s = Live() with { OutcomeFailed = true };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in s));
        e.OnArchived(s.NowMs, ArchiveReason.Wipe);
        var later = s with { NowMs = s.NowMs + AutoArchiveEngine.CooldownMs + 1 };
        Assert.Null(e.Evaluate(in later));                       // edge consumed, sticky Failed doesn't refire
    }

    // ---- boss phase ----

    [Fact]
    public void Boss_sighting_cuts_the_trash_segment_once()
    {
        var e = Armed(Live());
        var s = Live() with { BossPresent = true };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in s));
        e.OnArchived(s.NowMs, ArchiveReason.BossPhase);
        var later = s with { NowMs = s.NowMs + AutoArchiveEngine.CooldownMs + 1 };
        Assert.Null(e.Evaluate(in later));                       // boss segment active — no refire
    }

    [Fact]
    public void Boss_rearms_after_boss_gone()
    {
        var e = Armed(Live());
        var s = Live() with { BossPresent = true };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in s));
        e.OnArchived(s.NowMs, ArchiveReason.BossPhase);
        var gone = s with { BossPresent = false, BossGone = true, NowMs = s.NowMs + AutoArchiveEngine.CooldownMs + 1 };
        Assert.Null(e.Evaluate(in gone));
        var next = gone with { BossPresent = true, BossGone = false, NowMs = gone.NowMs + AutoArchiveEngine.CooldownMs + 1 };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in next));
    }

    [Fact]
    public void NonBoss_archive_ends_the_boss_segment()
    {
        var e = Armed(Live());
        var s = Live() with { BossPresent = true };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in s));
        e.OnArchived(s.NowMs, ArchiveReason.BossPhase);
        e.OnArchived(s.NowMs + 1000, ArchiveReason.Manual);      // user archived mid-boss — segment over
        var later = s with { NowMs = s.NowMs + 1000 + AutoArchiveEngine.CooldownMs + 1 };
        Assert.Equal(ArchiveReason.BossPhase, e.Evaluate(in later));
    }

    // ---- overlap: a banked stage transition must not survive an overlapping archive ----

    [Fact]
    public void Stage_transition_banked_across_an_overlapping_archive_is_consumed()
    {
        // Spec: "a shared 10 s cooldown prevents double-archives when triggers overlap (e.g. wipe +
        // stage change)". A banked _stagePending surviving the wipe's OnArchived and firing a stale
        // StageChange archive once stats re-accumulate IS a double-archive in slow motion — pin that
        // OnArchived consumes any pending transition, whichever trigger actually fired.
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));                                  // adopt flow version 1
        var overlap = Live() with { FlowStateVersion = 2, DeadCount = 4 }; // stage transition AND wipe overlap
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in overlap));          // wipe is checked first, wins the tick
        e.OnArchived(overlap.NowMs, ArchiveReason.Wipe);
        var later = overlap with { NowMs = overlap.NowMs + AutoArchiveEngine.CooldownMs + 1, DeadCount = 0 };
        Assert.Null(e.Evaluate(in later));                                // no stale StageChange fires later
    }

    // ---- idle ----

    [Fact]
    public void Idle_fires_after_timeout_with_content()
    {
        var e = Armed(Live());
        // 60s content (100k->160k), last damage 61s ago.
        var s = Live() with { NowMs = 160_000 + 61_000 };
        Assert.Equal(ArchiveReason.Idle, e.Evaluate(in s));
    }

    [Fact]
    public void Idle_content_guard_blocks_trivial_segments()
    {
        var e = Armed(Live());
        // Only 10s of combat span — under MinContentMs.
        var s = Live() with { CombatStartMs = 150_000, NowMs = 160_000 + 61_000 };
        Assert.Null(e.Evaluate(in s));
    }

    [Fact]
    public void Idle_respects_configured_timeout()
    {
        var e = Armed(Live());
        e.IdleTimeoutMs = 120_000;
        Assert.Null(e.Evaluate(Live(160_000 + 61_000)));
        var s = Live() with { NowMs = 160_000 + 121_000 };
        Assert.Equal(ArchiveReason.Idle, e.Evaluate(in s));
    }

    // ---- stage change ----

    [Fact]
    public void Stage_transition_fires_and_first_observation_is_silent()
    {
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live()));                          // first sight of version 1: adopt, no fire
        var s = Live() with { FlowStateVersion = 2 };
        Assert.Equal(ArchiveReason.StageChange, e.Evaluate(in s));
    }

    [Fact]
    public void Stage_version_reset_is_adopted_silently()
    {
        var e = new AutoArchiveEngine();
        Assert.Null(e.Evaluate(Live() with { FlowStateVersion = 5 }));
        // New run resets the service counter to a LOWER value — adopt, never fire.
        Assert.Null(e.Evaluate(Live() with { FlowStateVersion = 1 }));
    }

    [Fact]
    public void Stage_outside_instanced_run_never_fires()
    {
        var e = Armed(Live());
        var s = Live() with { FlowStateVersion = 2, InstancedRun = false };
        Assert.Null(e.Evaluate(in s));
    }

    // ---- shared gates ----

    [Fact]
    public void Cooldown_spans_all_triggers_including_manual_archives()
    {
        var e = Armed(Live());
        e.OnArchived(Live().NowMs, ArchiveReason.SceneChange);    // scene archive arms the cooldown
        var s = Live() with { DeadCount = 4, NowMs = Live().NowMs + AutoArchiveEngine.CooldownMs - 1 };
        Assert.Null(e.Evaluate(in s));                            // wipe suppressed inside the window
        var later = s with { NowMs = s.NowMs + 2 };
        Assert.Equal(ArchiveReason.Wipe, e.Evaluate(in later));
    }

    [Fact]
    public void No_stats_means_no_fire()
    {
        var e = Armed(Live());
        var s = Live() with { HasStats = false, DeadCount = 4 };
        Assert.Null(e.Evaluate(in s));
    }

    [Fact]
    public void Toggles_off_never_fire()
    {
        var e = Armed(Live());
        e.WipeEnabled = false; e.BossEnabled = false; e.IdleEnabled = false; e.StageEnabled = false;
        var s = Live() with
        {
            DeadCount = 4, BossPresent = true, FlowStateVersion = 2,
            NowMs = 160_000 + 300_001,
        };
        Assert.Null(e.Evaluate(in s));
    }
}
