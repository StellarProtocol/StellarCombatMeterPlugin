using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Spec test 1 — the full (state × trigger) matrix. Pure; no Unity host.
///
/// Internal types appear only in method BODIES, never in signatures: xUnit requires public test
/// methods, and a public method may not take an internal parameter type (CS0051). This is the same
/// convention the rest of this suite uses for internal enums such as ArchiveReason.
/// </summary>
public class UploadPolicyTests
{
    [Fact]
    public void Auto_AllowsBothTriggers()
    {
        Assert.True(UploadPolicy.Allows(UploadPolicyState.Auto, UploadTrigger.Auto));
        Assert.True(UploadPolicy.Allows(UploadPolicyState.Auto, UploadTrigger.Manual));
    }

    [Fact]
    public void Manual_BlocksTheAutomaticPath_ButPermitsAnExplicitPush()
    {
        Assert.False(UploadPolicy.Allows(UploadPolicyState.Manual, UploadTrigger.Auto));
        Assert.True(UploadPolicy.Allows(UploadPolicyState.Manual, UploadTrigger.Manual));
    }

    [Fact]
    public void Off_BlocksBothTriggers()
    {
        Assert.False(UploadPolicy.Allows(UploadPolicyState.Off, UploadTrigger.Auto));
        Assert.False(UploadPolicy.Allows(UploadPolicyState.Off, UploadTrigger.Manual));
    }

    [Fact]
    public void FormatAndParse_RoundTripEveryState()
    {
        Assert.Equal("auto",   UploadPolicy.Format(UploadPolicyState.Auto));
        Assert.Equal("manual", UploadPolicy.Format(UploadPolicyState.Manual));
        Assert.Equal("off",    UploadPolicy.Format(UploadPolicyState.Off));

        Assert.Equal(UploadPolicyState.Auto,   UploadPolicy.Parse("auto"));
        Assert.Equal(UploadPolicyState.Manual, UploadPolicy.Parse("manual"));
        Assert.Equal(UploadPolicyState.Off,    UploadPolicy.Parse("off"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AUTO")]
    [InlineData("nonsense")]
    public void Parse_UnknownOrAbsent_DefaultsToAuto(string? raw)
        => Assert.Equal(UploadPolicyState.Auto, UploadPolicy.Parse(raw));

    [Fact]
    public void PrefKey_MatchesTheSpecKeyScheme()
    {
        Assert.Equal("logUpload.policy.dungeon.stats",   UploadPolicy.PrefKey(ContentKind.Dungeon,   UploadArtifact.Stats));
        Assert.Equal("logUpload.policy.raid.replay",     UploadPolicy.PrefKey(ContentKind.Raid,      UploadArtifact.Replay));
        Assert.Equal("logUpload.policy.worldboss.stats", UploadPolicy.PrefKey(ContentKind.WorldBoss, UploadArtifact.Stats));
        Assert.Equal("logUpload.policy.other.replay",    UploadPolicy.PrefKey(ContentKind.Other,     UploadArtifact.Replay));
    }

    [Fact]
    public void KindKey_MatchesTheWorkerFeedVocabulary()
    {
        // Must equal FEED_KINDS in services/stellar-logs/src/worker/routes/site.ts exactly.
        Assert.Equal("dungeon",   UploadPolicy.KindKey(ContentKind.Dungeon));
        Assert.Equal("raid",      UploadPolicy.KindKey(ContentKind.Raid));
        Assert.Equal("worldboss", UploadPolicy.KindKey(ContentKind.WorldBoss));
        Assert.Equal("vault",     UploadPolicy.KindKey(ContentKind.Vault));
        Assert.Equal("other",     UploadPolicy.KindKey(ContentKind.Other));
    }

    // Settings-pane row labels (Plugin.SettingsArchive.cs's UploadsSection is the consumer). Pinned
    // because a typo here is invisible to every other test yet directly visible to the user, and the
    // names must keep matching the site's own feed tabs so the plugin configures the buckets the site
    // shows. Distinctness guards against two rows rendering the same label.
    [Fact]
    public void Label_MatchesTheSiteFeedTabNames()
    {
        // i18n P1: Label now returns a catalog KEY; the site-aligned display text ("Dungeons"/"Raids"/…)
        // lives in Lang/en.json under these keys.
        Assert.Equal("upload.kind.dungeon",   UploadPolicy.Label(ContentKind.Dungeon));
        Assert.Equal("upload.kind.raid",      UploadPolicy.Label(ContentKind.Raid));
        Assert.Equal("upload.kind.worldBoss", UploadPolicy.Label(ContentKind.WorldBoss));
        Assert.Equal("upload.kind.vault",     UploadPolicy.Label(ContentKind.Vault));
        Assert.Equal("upload.kind.other",     UploadPolicy.Label(ContentKind.Other));
    }

    [Fact]
    public void Label_IsDistinctForEveryKind()
    {
        var seen = new System.Collections.Generic.HashSet<string>();
        foreach (var kind in UploadPolicyTable.Kinds)
            Assert.True(seen.Add(UploadPolicy.Label(kind)), $"duplicate label for {kind}");
        // 4 -> 5 kinds on 2026-07-29 (spec § 8.1): `vault` = "Stimen Vaults".
        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public void ArtifactKey_MatchesTheSpecVocabulary()
    {
        Assert.Equal("stats",  UploadPolicy.ArtifactKey(UploadArtifact.Stats));
        Assert.Equal("replay", UploadPolicy.ArtifactKey(UploadArtifact.Replay));
    }
}
