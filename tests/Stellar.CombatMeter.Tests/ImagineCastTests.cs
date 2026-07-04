using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.CombatMeter;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Covers the Battle-Imagine cast-detection redesign: casts are recorded at the SkillUsed Begin
/// (101) event's true timestamp, not inferred from later damage dealt by a summon. Plugin itself
/// cannot be headless-instantiated (IL2CPP-bound services — see ReplayCaptureTests' doc comment),
/// so these exercise the extracted pure predicates <see cref="Plugin.IsImagineCastBegin"/> and
/// <see cref="Plugin.ShouldRecordImagineCast"/> that back <c>ObserveResonanceCastBegin</c> /
/// <c>RecordImagineCast</c> in Plugin.Capture.cs.
/// </summary>
public sealed class ImagineCastTests
{
    private static readonly ImagineInfo SomeImagine = new(
        SkillId: 12345, Name: "Test Imagine", IconAddress: "", ChargeCount: 2, RechargeMs: 20000, CooldownMs: 0);

    // -------------------------------------------------------------------------
    // IsImagineCastBegin — only a player's Begin phase against a resolved imagine counts as a cast.
    // -------------------------------------------------------------------------

    [Fact]
    public void IsImagineCastBegin_true_for_player_begin_with_imagine_info()
    {
        Assert.True(Plugin.IsImagineCastBegin(SkillEventPhase.Begin, casterIsPlayer: true, SomeImagine));
    }

    [Fact]
    public void IsImagineCastBegin_false_when_skill_is_not_an_imagine()
    {
        // GetImagineForSkill returned null — not a Battle Imagine skill at all.
        Assert.False(Plugin.IsImagineCastBegin(SkillEventPhase.Begin, casterIsPlayer: true, info: null));
    }

    [Theory]
    [InlineData(SkillEventPhase.StageBegin)]
    [InlineData(SkillEventPhase.StageEnd)]
    [InlineData(SkillEventPhase.AccumulateEnd)]
    [InlineData(SkillEventPhase.SkillEnd)]
    public void IsImagineCastBegin_false_for_non_begin_phases_of_the_same_cast(SkillEventPhase phase)
    {
        // These are later phases of the SAME cast (or a long-lived summon's later animation stages) —
        // must never create a second cast entry. This is the fix for the "1:09 phantom cast bubble
        // when the player had no stacks left" symptom: the old damage-based inference re-fired on the
        // summon's later ACTION; Begin-only detection structurally excludes every later phase.
        Assert.False(Plugin.IsImagineCastBegin(phase, casterIsPlayer: true, SomeImagine));
    }

    [Fact]
    public void IsImagineCastBegin_false_when_caster_is_not_a_player()
    {
        // A long-lived summon's own autonomous actions carry the summon's EntityId as CasterId, which
        // is never IsPlayer — excluded structurally, no combat-active/encounter gating needed.
        Assert.False(Plugin.IsImagineCastBegin(SkillEventPhase.Begin, casterIsPlayer: false, SomeImagine));
    }

    // -------------------------------------------------------------------------
    // ShouldRecordImagineCast — dedup keyed on (src, base-skill) elsewhere; this is the time-window
    // predicate in isolation.
    // -------------------------------------------------------------------------

    [Fact]
    public void ShouldRecordImagineCast_true_on_first_sighting()
    {
        Assert.True(Plugin.ShouldRecordImagineCast(ms: 1000, lastMs: null, dedupWindowMs: 1500));
    }

    [Fact]
    public void ShouldRecordImagineCast_false_within_the_dedup_window()
    {
        // A resent/duplicate Begin for the same cast, 400ms later — must not double-record.
        Assert.False(Plugin.ShouldRecordImagineCast(ms: 1400, lastMs: 1000, dedupWindowMs: 1500));
    }

    [Fact]
    public void ShouldRecordImagineCast_true_once_the_window_has_elapsed()
    {
        Assert.True(Plugin.ShouldRecordImagineCast(ms: 2600, lastMs: 1000, dedupWindowMs: 1500));
    }

    [Fact]
    public void ShouldRecordImagineCast_true_at_exactly_the_window_boundary()
    {
        Assert.True(Plugin.ShouldRecordImagineCast(ms: 2500, lastMs: 1000, dedupWindowMs: 1500));
    }

    [Fact]
    public void Two_different_imagines_back_to_back_both_record()
    {
        // Reproduces the "of two imagines cast back-to-back, only one was recorded" symptom. The dedup
        // key (elsewhere) is (src, baseSkillId) — different base ids never collide, so both casts must
        // pass ShouldRecordImagineCast independently even inside the same short window.
        Assert.True(Plugin.ShouldRecordImagineCast(ms: 29000, lastMs: null, dedupWindowMs: 1500));   // imagine A, first sighting
        Assert.True(Plugin.ShouldRecordImagineCast(ms: 29400, lastMs: null, dedupWindowMs: 1500));   // imagine B, first sighting (independent key)
    }
}
