using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Pins the spec-to-parent-profession derivation (commit 9083620, owner-reported red/iconless
/// meter): when SocialSync never delivered a party member's profession, the parent class is
/// derived from the cast-inferred sub-profession. A sub id encodes its parent as
/// &lt;ProfessionId&gt;_00_&lt;SpecIndex&gt; (ProfessionSpecs), i.e. parent = sub / 10000.
/// 0 = unknown: the row stays unstyled rather than guessing.
/// </summary>
public sealed class RoleClassifierParentProfessionTests
{
    [Theory]
    [InlineData(40001, 4)]    // spec of profession 4
    [InlineData(110002, 11)]  // spec of profession 11 (six-digit sub id)
    [InlineData(50001, 5)]    // spec of profession 5 (healer -> green via Classify)
    [InlineData(0, 0)]        // no cast-inferred spec -> unknown
    [InlineData(-1, 0)]       // defensive: negative never derives
    [InlineData(9999, 0)]     // below the encoding floor -> unknown, not profession 0-rounding
    public void ParentProfession_derives_from_sub_id(int sub, int expected)
        => Assert.Equal(expected, RoleClassifier.ParentProfession(sub));

    [Fact]
    public void Derived_healer_classifies_green()
    {
        // End-to-end intent of the owner fix: a healer known only by casts colours green.
        var parent = RoleClassifier.ParentProfession(50001);
        Assert.Equal(Role.Healer, RoleClassifier.Classify(parent));
    }
}
