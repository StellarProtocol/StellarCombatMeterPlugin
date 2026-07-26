using Stellar.CombatMeter.AutoArchive;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Idle timeout tests (content guard, configurable timeout) plus the idle re-fire guard (the
// single !CombatActive gate and its re-arm). Split out of AutoArchiveEngineTests.cs (2026-07-26,
// review round) — see that file's banner for the full partial map. Live()/Armed() live there.
public partial class AutoArchiveEngineTests
{
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

    // ---- idle re-fire guard (Fix 2, review round): the ONLY thing stopping Idle from refiring
    // every cooldown window is `!s.CombatActive` — the real re-arm happens out-of-engine (caller's
    // archive -> Clear() -> CombatActive false). Pin that this single guard actually holds, and
    // that it's genuinely re-armable rather than a one-way latch. ----

    [Fact]
    public void Idle_does_not_refire_while_combat_stays_inactive_after_archive()
    {
        var e = Armed(Live());
        var s = Live() with { NowMs = 221_000 };   // 61s after last damage, 60s of content — idle fires
        Assert.Equal(ArchiveReason.Idle, e.Evaluate(in s));
        e.OnArchived(s.NowMs, ArchiveReason.Idle);
        var cleared = s with { CombatActive = false, NowMs = s.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Null(e.Evaluate(in cleared));       // past cooldown, but CombatActive=false blocks
        var stillInactive = cleared with { NowMs = cleared.NowMs + AutoArchiveEngine.DefaultCooldownMs + 1 };
        Assert.Null(e.Evaluate(in stillInactive)); // a second window later — still no refire
    }

    [Fact]
    public void Idle_blocked_when_no_damage_ever_recorded()
    {
        // Fixed (reviewer minor, round 2): pre-fix this passed for the wrong reason — with the
        // Live() baseline CombatStartMs (100_000), the content-guard math alone already failed
        // (0 - 100_000 = -100_000, well under MinContentMs) regardless of the explicit
        // `LastDamageMs == 0` guard. Override CombatStartMs so the content-guard math would PASS on
        // its own (LastDamageMs - CombatStartMs >= MinContentMs), isolating the explicit guard as
        // the ONLY thing that can be blocking the fire.
        var e = Armed(Live());
        var s = Live() with { LastDamageMs = 0, CombatStartMs = -40_000, NowMs = 300_000 };
        Assert.Null(e.Evaluate(in s));             // LastDamageMs == 0 blocks even though content-guard math would pass
    }

    [Fact]
    public void Idle_refires_after_fresh_combat_following_the_clear()
    {
        var e = Armed(Live());
        var s = Live() with { NowMs = 221_000 };
        Assert.Equal(ArchiveReason.Idle, e.Evaluate(in s));
        e.OnArchived(s.NowMs, ArchiveReason.Idle);
        var cleared = s with { CombatActive = false, NowMs = s.NowMs + 1000 };
        Assert.Null(e.Evaluate(in cleared));
        // Fresh combat starts: CombatActive true again with a new span, well past cooldown + idle timeout.
        var fresh = cleared with
        {
            CombatActive = true, CombatStartMs = 225_000, LastDamageMs = 256_000, NowMs = 316_001,
        };
        Assert.Equal(ArchiveReason.Idle, e.Evaluate(in fresh)); // fresh combat re-enables a later idle fire
    }
}
