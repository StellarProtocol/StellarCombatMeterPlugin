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
        f.SetTierEnabled(ContentKind.Dungeon, ContentTier.Normal, false);

        Assert.False(f.Allows(ContentKind.Dungeon, ContentTier.Normal, 0));
        Assert.True(f.Allows(ContentKind.Dungeon, ContentTier.Hard, 0));
        Assert.True(f.Allows(ContentKind.Dungeon, ContentTier.Master, 0));
    }

    // REGRESSION PIN (2026-07-30) — a disabled DUNGEON tier must not block the same tier on a RAID.
    //
    // The filter used to key its disabled set by tier ALONE. Because the site serves raid 13002
    // ("Brutal! Floating Island") as tag "hard" — the same tag a hard dungeon carries — `Hard` was one
    // shared key, so turning off the dungeon Hard chip silently stopped Brutal! raids auto-uploading.
    // Measured before the fix: mapId 13002 autoSendAllowed=False with only the dungeon chip off.
    // Never collapse this back to a tier-only key.
    [Fact]
    public void DisablingADungeonTierDoesNotBlockTheSameTierOnARaid()
    {
        var f = new UploadTierFilter();
        f.SetTierEnabled(ContentKind.Dungeon, ContentTier.Hard, false);

        Assert.False(f.Allows(ContentKind.Dungeon, ContentTier.Hard, 0));
        Assert.True(f.Allows(ContentKind.Raid, ContentTier.Hard, 0));    // Brutal! — a different chip

        // and symmetrically
        var g = new UploadTierFilter();
        g.SetTierEnabled(ContentKind.Raid, ContentTier.Normal, false);   // Clash!
        Assert.False(g.Allows(ContentKind.Raid, ContentTier.Normal, 0));
        Assert.True(g.Allows(ContentKind.Dungeon, ContentTier.Normal, 0));
    }

    // REGRESSION PIN (2026-07-30) — every raid chip must be reachable from a tag the site actually serves.
    // TiersFor[Raid] previously listed Clash/Brutal, which the wire never sends (it sends normal/hard),
    // so two chips matched nothing and raid normal/hard runs could not be filtered at all.
    [Fact]
    public void EveryRaidChipIsReachableFromAServedTierTag()
    {
        // The tags GET /api/site/content-kinds returns for the nine live raid ids.
        foreach (var served in new[] { "backtrack", "hard", "nightmare", "normal" })
            Assert.Contains(UploadTierFilter.ParseTier(served), UploadTierFilter.TiersFor[ContentKind.Raid]);

        // ...and no chip is unreachable: each one is some served tag's parse result.
        foreach (var chip in UploadTierFilter.TiersFor[ContentKind.Raid])
        {
            var reachable = false;
            foreach (var served in new[] { "backtrack", "hard", "nightmare", "normal" })
                if (UploadTierFilter.ParseTier(served) == chip) reachable = true;
            Assert.True(reachable, $"raid chip {chip} matches no served tier tag");
        }
    }

    [Fact]
    public void UnknownTierAlwaysUploads()
    {
        // No tier map fetched yet, or a map id the site does not classify. Fail OPEN.
        var f = new UploadTierFilter();
        f.SetTierEnabled(ContentKind.Dungeon, ContentTier.Normal, false);
        f.SetTierEnabled(ContentKind.Dungeon, ContentTier.Hard, false);
        f.SetTierEnabled(ContentKind.Dungeon, ContentTier.Master, false);

        Assert.True(f.Allows(ContentKind.Dungeon, ContentTier.Unknown, 0));
    }

    [Fact]
    public void KindsWithNoDifficultyAxisAreUnaffected()
    {
        // A disabled dungeon tier must never leak into world boss / vaults / other.
        var f = new UploadTierFilter();
        foreach (var tier in UploadTierFilter.TiersFor[ContentKind.Dungeon])
            f.SetTierEnabled(ContentKind.Dungeon, tier, false);

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
        f.SetTierEnabled(ContentKind.Dungeon, ContentTier.Unknown, false);
        Assert.True(f.IsTierEnabled(ContentKind.Dungeon, ContentTier.Unknown));
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
    // SYNONYMS NORMALIZE (2026-07-30). The raid names are the same rungs under different words — owner:
    // "clash = normal < brutal = hard < purge = nightmare". Resolving them to tiers of their own is what
    // produced chips no served tag could reach, so clash→normal and brutal→hard collapse here.
    [InlineData("clash", "normal")]
    [InlineData("brutal", "hard")]
    [InlineData("purge", "purge")]
    [InlineData("backtrack", "backtrack")]
    // "nightmare" is what the site serves for the tier the game calls "purge" — both must resolve, or the
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
            Assert.Equal($"logUpload.tier.{UploadPolicy.KindKey(kind)}.{key}",
                         UploadTierFilter.TierPrefKey(kind, tier));
        }
    }

    // The kind segment is the whole point of the scoped key: two kinds sharing a tier must not share a
    // pref, or the settings pane writes one chip and moves the other.
    [Fact]
    public void TheSameTierOnTwoKindsGetsTwoDistinctPrefKeys()
    {
        Assert.NotEqual(UploadTierFilter.TierPrefKey(ContentKind.Dungeon, ContentTier.Hard),
                        UploadTierFilter.TierPrefKey(ContentKind.Raid, ContentTier.Hard));
    }

    // Labels are per kind because the game shows one rung under two names; the STORED tier is identical.
    [Fact]
    public void RaidChipsCarryTheGamesNamesWhileStoringTheServedTier()
    {
        Assert.Equal("Clash!", UploadTierFilter.TierLabel(ContentKind.Raid, ContentTier.Normal));
        Assert.Equal("Brutal!", UploadTierFilter.TierLabel(ContentKind.Raid, ContentTier.Hard));
        Assert.Equal("Purge!", UploadTierFilter.TierLabel(ContentKind.Raid, ContentTier.Purge));
        Assert.Equal("Backtrack!", UploadTierFilter.TierLabel(ContentKind.Raid, ContentTier.Backtrack));

        // Dungeons keep the plain served vocabulary.
        Assert.Equal("normal", UploadTierFilter.TierLabel(ContentKind.Dungeon, ContentTier.Normal));
        Assert.Equal("hard", UploadTierFilter.TierLabel(ContentKind.Dungeon, ContentTier.Hard));
        Assert.Equal("master", UploadTierFilter.TierLabel(ContentKind.Dungeon, ContentTier.Master));
    }
}
