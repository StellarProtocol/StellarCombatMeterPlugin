using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// The Ability Score / Illusion-Breaking Strength row cell (owner 2026-08-15, spec
/// docs/superpowers/specs/2026-08-15-combatmeter-illusion-breaking-strength-design.md). Ability Score is
/// the game's FightPoint; Illusion-Breaking Strength is attr 11440 (AttrSeasonStrength). Owner-chosen
/// format is <c>51,931 (+2,504)</c> — the season value in a <c>(+…)</c> suffix, matching the
/// EntityInspector plugin. Each has its OWN toggle, and a member whose season value has not broadcast
/// (0/unknown) shows Ability Score alone rather than a bogus <c>(+0)</c>.
/// </summary>
public class AbilityScoreFormatTests
{
    private const long Fp = 51931;   // FightPoint (Ability Score)
    private const long Ss = 2504;    // AttrSeasonStrength (Illusion-Breaking Strength)

    [Fact]
    public void Both_on_with_a_season_value_combines_with_a_plus_suffix()
        => Assert.Equal("51,931 (+2,504)", Plugin.FormatAbilityScore(Fp, Ss, showAbility: true, showIllusion: true));

    [Fact]
    public void Both_on_but_no_season_value_shows_ability_alone_never_plus_zero()
        => Assert.Equal("51,931", Plugin.FormatAbilityScore(Fp, 0, showAbility: true, showIllusion: true));

    [Fact]
    public void Ability_only_shows_ability()
        => Assert.Equal("51,931", Plugin.FormatAbilityScore(Fp, Ss, showAbility: true, showIllusion: false));

    [Fact]
    public void Illusion_only_with_a_value_shows_the_season_value_alone()
        => Assert.Equal("2,504", Plugin.FormatAbilityScore(Fp, Ss, showAbility: false, showIllusion: true));

    [Fact]
    public void Illusion_only_with_no_value_is_empty()
        => Assert.Equal("", Plugin.FormatAbilityScore(Fp, 0, showAbility: false, showIllusion: true));

    [Fact]
    public void Both_off_is_empty()
        => Assert.Equal("", Plugin.FormatAbilityScore(Fp, Ss, showAbility: false, showIllusion: false));

    // FightPoint 0 (unknown ability) keeps today's behavior — Ability Score omitted; a known season value
    // still shows alone so the cell is not wasted.
    [Fact]
    public void Zero_fightpoint_omits_ability_but_a_known_season_still_shows()
    {
        Assert.Equal("", Plugin.FormatAbilityScore(0, 0, showAbility: true, showIllusion: true));
        Assert.Equal("2,504", Plugin.FormatAbilityScore(0, Ss, showAbility: true, showIllusion: true));
        Assert.Equal("", Plugin.FormatAbilityScore(0, Ss, showAbility: true, showIllusion: false));
    }
}
