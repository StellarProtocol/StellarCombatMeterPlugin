using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// The difficulty axis of the per-content upload grid (owner-approved 2026-07-29).
///
/// <para>PINNED, do not weaken: every fail-open path. An unresolved tier or an unknown master level must
/// ALLOW the upload. The alternative — withholding a run because a classifier had not loaded — silently
/// loses the owner's data, which is the failure mode Spec B § 2.3 exists to prevent.</para>
/// </summary>
public class UploadTierFilterTests
{
    [Fact]
    public void DefaultsAllowEverything()
    {
        // An upgrade must not change what uploads. This is the whole safety property of the feature.
        var f = new UploadTierFilter();
        foreach (var tier in UploadTierFilter.TiersFor[ContentKind.Dungeon])
            Assert.True(f.Allows(ContentKind.Dungeon, tier, masterLevel: 0));
        foreach (var tier in UploadTierFilter.TiersFor[ContentKind.Raid])
            Assert.True(f.Allows(ContentKind.Raid, tier, masterLevel: 0));
        Assert.Equal(1, f.MinMasterLevel);
    }

    [Fact]
    public void DisablingATierBlocksOnlyThatTier()
    {
        var f = new UploadTierFilter();
        f.SetTierEnabled(ContentTier.Normal, false);

        Assert.False(f.Allows(ContentKind.Dungeon, ContentTier.Normal, 0));
        Assert.True(f.Allows(ContentKind.Dungeon, ContentTier.Hard, 0));
        Assert.True(f.Allows(ContentKind.Dungeon, ContentTier.Master, 0));
    }

    [Fact]
    public void UnknownTierAlwaysUploads()
    {
        // No tier map fetched yet, or a map id the site does not classify. Fail OPEN.
        var f = new UploadTierFilter();
        f.SetTierEnabled(ContentTier.Normal, false);
        f.SetTierEnabled(ContentTier.Hard, false);
        f.SetTierEnabled(ContentTier.Master, false);

        Assert.True(f.Allows(ContentKind.Dungeon, ContentTier.Unknown, 0));
    }

    [Fact]
    public void KindsWithNoDifficultyAxisAreUnaffected()
    {
        // A disabled dungeon tier must never leak into world boss / vaults / other.
        var f = new UploadTierFilter();
        foreach (var tier in UploadTierFilter.TiersFor[ContentKind.Dungeon])
            f.SetTierEnabled(tier, false);

        Assert.True(f.Allows(ContentKind.WorldBoss, ContentTier.Unknown, 0));
        Assert.True(f.Allows(ContentKind.Vault, ContentTier.Normal, 0));
        Assert.True(f.Allows(ContentKind.Other, ContentTier.Normal, 0));
    }

    [Theory]
    [InlineData(18, 10, true)]    // Master 18 passes a >=10 floor  (the real run 1133807504275275776)
    [InlineData(10, 10, true)]    // boundary is inclusive
    [InlineData(6, 10, false)]    // Master 6 is below the floor
    [InlineData(0, 10, true)]     // level UNKNOWN -> fail open, never withheld
    [InlineData(1, 1, true)]      // default floor admits every master run
    public void MasterLevelFloorAppliesOnlyWhenTheLevelIsKnown(int runLevel, int floor, bool allowed)
    {
        var f = new UploadTierFilter { MinMasterLevel = floor };
        Assert.Equal(allowed, f.Allows(ContentKind.Dungeon, ContentTier.Master, runLevel));
    }

    [Fact]
    public void MasterLevelFloorNeverAffectsNonMasterTiers()
    {
        // A high floor must not suppress hard/normal runs, whose level is legitimately 0.
        var f = new UploadTierFilter { MinMasterLevel = 20 };
        Assert.True(f.Allows(ContentKind.Dungeon, ContentTier.Normal, 0));
        Assert.True(f.Allows(ContentKind.Dungeon, ContentTier.Hard, 0));
        Assert.True(f.Allows(ContentKind.Raid, ContentTier.Purge, 0));
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(21, 20)]
    [InlineData(999, 20)]
    [InlineData(13, 13)]
    public void MasterLevelIsClampedToTheGamesLadder(int set, int expected)
    {
        var f = new UploadTierFilter { MinMasterLevel = set };
        Assert.Equal(expected, f.MinMasterLevel);
    }

    [Fact]
    public void UnknownTierCannotBeDisabled()
    {
        // Unknown is the fail-open sentinel, not a chip; disabling it would defeat the whole guard.
        var f = new UploadTierFilter();
        f.SetTierEnabled(ContentTier.Unknown, false);
        Assert.True(f.IsTierEnabled(ContentTier.Unknown));
        Assert.True(f.Allows(ContentKind.Dungeon, ContentTier.Unknown, 0));
    }

    // Asserted through the string key rather than [InlineData] over the enum: xUnit requires public test
    // methods and a public method may not take an internal parameter type (CS0051). Internal types appear
    // only in method BODIES, matching how UploadPolicyTableTests already handles internal enums.
    // TierKey(Unknown) is "", so the empty string is the expectation for every unresolved input.
    [Theory]
    [InlineData("normal", "normal")]
    [InlineData("hard", "hard")]
    [InlineData("master", "master")]
    [InlineData("clash", "clash")]
    [InlineData("brutal", "brutal")]
    [InlineData("purge", "purge")]
    [InlineData("backtrack", "backtrack")]
    // "nightmare" is the prose name for the tier the map data tags "purge" — both must resolve, or the
    // two vocabularies drifting would turn into a silent all-block.
    [InlineData("nightmare", "purge")]
    [InlineData("Hard", "")]                 // case-sensitive by design: the wire is lowercase
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("some-future-tier", "")]
    public void ParseTierIsPermissive(string? raw, string expectedKey)
        => Assert.Equal(expectedKey, UploadTierFilter.TierKey(UploadTierFilter.ParseTier(raw)));

    [Fact]
    public void EveryUserFacingTierRoundTripsItsPrefKey()
    {
        foreach (var kind in UploadTierFilter.TiersFor.Keys)
        foreach (var tier in UploadTierFilter.TiersFor[kind])
        {
            var key = UploadTierFilter.TierKey(tier);
            Assert.False(string.IsNullOrEmpty(key));
            Assert.Equal(tier, UploadTierFilter.ParseTier(key));
            Assert.Equal("logUpload.tier." + key, UploadTierFilter.TierPrefKey(tier));
        }
    }
}
