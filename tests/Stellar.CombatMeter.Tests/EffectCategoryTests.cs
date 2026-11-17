using Xunit;

namespace Stellar.CombatMeter.Tests;

// Pins EffectCategoryClassifier — the keyword guess behind an icon-less effect's placeholder glyph (approved option B,
// 2026-11-17). Labels are real BuffTable NameDesign rows and their framework English design labels.
public class EffectCategoryTests
{
    [Theory]
    [InlineData("物理免疫BUFF", null, EffectCategory.Immunity)]                 // 55226 Physical Immunity
    [InlineData(null, "Physical immunity BUFF", EffectCategory.Immunity)]
    [InlineData("法杖冰盾BUFF", "Staff Ice Shield", EffectCategory.Shield)]     // English hit (bare 盾 is not a keyword)
    [InlineData(null, "Sword and Shield Initial Buff", EffectCategory.Default)] // weapon class, not a shield
    [InlineData("眩晕", null, EffectCategory.CrowdControl)]
    [InlineData(null, "Downed State", EffectCategory.CrowdControl)]
    [InlineData(null, "Vehicle Koi Slow Fall", EffectCategory.Default)]
    [InlineData("种子命中回复生命值", "Seed Hit Restores HP", EffectCategory.Heal)]
    [InlineData(null, "Healing Ring Self-Heal", EffectCategory.Heal)]
    [InlineData(null, "Staff Innate Energy Recovery", EffectCategory.Default)]
    [InlineData("无限掉血debuff", "Endless Bleed Debuff", EffectCategory.DamageOverTime)]
    [InlineData(null, "Mob Void Dot", EffectCategory.DamageOverTime)]
    [InlineData("15%元素减免", "15% Element Reduction", EffectCategory.StatDown)]
    [InlineData("护佑之风被击加伤", "Wind Protection Hit Damage Up", EffectCategory.StatUp)]
    [InlineData("气刃突刺计数", "Qi Blade Thrust Counter", EffectCategory.Default)]
    [InlineData(null, null, EffectCategory.Default)]
    public void Classifies_by_design_label(string? label, string? english, EffectCategory expected)
        => Assert.Equal(expected, EffectCategoryClassifier.Classify(label, english));

    [Fact]
    public void Rule_order_puts_crowd_control_before_stat_down()
        => Assert.Equal(EffectCategory.CrowdControl, EffectCategoryClassifier.Classify("减速降低", null));
}
