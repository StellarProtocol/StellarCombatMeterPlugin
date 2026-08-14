using Stellar.CombatMeter.LogUpload;
using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Pins the <c>uploadApiBase</c> config override (owner 2026-08-14). Three invariants:
/// (1) with no override the effective base is the compiled-in const and EVERY artifact URL is
/// byte-identical to what shipped before this feature; (2) an override moves EVERY artifact URL
/// builder — a build must never split its uploads across prod + staging; (3) a malformed value is
/// IGNORED (falls back to production) rather than silently swallowing uploads.
/// </summary>
[Collection(ApiBaseCollection.Name)]
public class UploadApiBaseTests
{
    private const string Prod = "https://api.stellarresonance.app";
    private const string Staging = "https://api-staging.stellarresonance.app";

    // ---- (1) default = the const, everywhere -------------------------------------------------

    [Fact]
    public void Default_effective_base_is_the_production_const()
    {
        Assert.Equal(Prod, LogUploader.DefaultApiBase);
        Assert.Equal(LogUploader.DefaultApiBase, LogUploader.ApiBase);
        Assert.False(LogUploader.IsApiBaseOverridden);
    }

    /// <summary>Every builder at the default base — these are the EXACT URLs that shipped before the
    /// override existed. If one of these changes, a released build started posting somewhere new.</summary>
    [Fact]
    public void Every_artifact_url_at_the_default_base_is_unchanged()
    {
        var b = LogUploader.DefaultApiBase;
        Assert.Equal("https://api.stellarresonance.app/upload", LogUploader.BuildUploadUrl(b));
        Assert.Equal("https://api.stellarresonance.app/run/sea/42/events", ChunkUploader.BuildUrl(b, "sea", 42));
        Assert.Equal("https://api.stellarresonance.app/run/sea/42/positions", PositionUploader.BuildUrl(b, "sea", 42));
        Assert.Equal("https://api.stellarresonance.app/run/sea/42/supplement", LogUploader.BuildSupplementUrl(b, "sea", 42));
        Assert.Equal("https://api.stellarresonance.app/char/portraits", PortraitUploader.BuildUrl(b));
    }

    // ---- (2) an override moves EVERY builder --------------------------------------------------

    /// <summary>The whole point of the feature: no artifact may keep a hardcoded prod host. Positions and
    /// portraits are the two that DID hardcode one before this change — a replay or portrait landing on a
    /// different backend than its own summary corrupts both datasets.</summary>
    [Fact]
    public void Override_moves_every_artifact_url_off_production()
    {
        var b = LogUploader.ResolveApiBase(Staging);
        Assert.Equal(Staging, b);

        var urls = new[]
        {
            LogUploader.BuildUploadUrl(b),
            ChunkUploader.BuildUrl(b, "sea", 42),
            PositionUploader.BuildUrl(b, "sea", 42),
            LogUploader.BuildSupplementUrl(b, "sea", 42),
            PortraitUploader.BuildUrl(b),
        };

        Assert.Equal(5, urls.Length);
        foreach (var u in urls)
        {
            Assert.StartsWith(Staging + "/", u);
            Assert.DoesNotContain("//api.stellarresonance.app", u);   // no residual prod host anywhere
        }
    }

    [Fact]
    public void Override_preserves_each_artifacts_path_suffix()
    {
        var b = LogUploader.ResolveApiBase(Staging);
        Assert.Equal(Staging + "/upload", LogUploader.BuildUploadUrl(b));
        Assert.Equal(Staging + "/run/jp/7/events", ChunkUploader.BuildUrl(b, "jp", 7));
        Assert.Equal(Staging + "/run/jp/7/positions", PositionUploader.BuildUrl(b, "jp", 7));
        Assert.Equal(Staging + "/run/jp/7/supplement", LogUploader.BuildSupplementUrl(b, "jp", 7));
        Assert.Equal(Staging + "/char/portraits", PortraitUploader.BuildUrl(b));
    }

    // ---- (3) normalization + malformed-is-ignored ---------------------------------------------

    [Theory]
    [InlineData("https://api-staging.stellarresonance.app", Staging)]
    [InlineData("https://api-staging.stellarresonance.app/", Staging)]      // trailing slash trimmed
    [InlineData("https://api-staging.stellarresonance.app///", Staging)]    // ...however many
    [InlineData("  https://api-staging.stellarresonance.app  ", Staging)]   // whitespace trimmed
    [InlineData("https://localhost:8787", "https://localhost:8787")]        // a local wrangler dev server
    public void Valid_values_are_normalized_and_applied(string raw, string expected)
    {
        Assert.Equal(expected, LogUploader.NormalizeApiBase(raw));
        Assert.Equal(expected, LogUploader.ResolveApiBase(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://api-staging.stellarresonance.app")]   // plaintext — never
    [InlineData("api-staging.stellarresonance.app")]          // schemeless
    [InlineData("https://")]                                  // scheme only, no host
    [InlineData("https:///")]                                 // ...and after slash-trimming, still none
    [InlineData("ftp://example.com")]
    [InlineData("garbage")]
    public void Malformed_or_absent_values_are_ignored_and_fall_back_to_production(string? raw)
    {
        Assert.Null(LogUploader.NormalizeApiBase(raw));
        Assert.Equal(LogUploader.DefaultApiBase, LogUploader.ResolveApiBase(raw));
    }

    // ---- the runtime apply path (mutates the static; restored in finally) ----------------------

    /// <summary>SetApiBase is what the plugin constructor calls. Proves the resolved value actually
    /// reaches <see cref="LogUploader.ApiBase"/>, which every production call site reads.</summary>
    [Fact]
    public void SetApiBase_applies_the_override_and_a_malformed_value_restores_production()
    {
        try
        {
            LogUploader.SetApiBase(Staging + "/");
            Assert.Equal(Staging, LogUploader.ApiBase);
            Assert.True(LogUploader.IsApiBaseOverridden);
            Assert.Equal(Staging + "/upload", LogUploader.BuildUploadUrl(LogUploader.ApiBase));

            LogUploader.SetApiBase("http://nope.example.com");   // malformed → ignored, back to prod
            Assert.Equal(LogUploader.DefaultApiBase, LogUploader.ApiBase);
            Assert.False(LogUploader.IsApiBaseOverridden);

            LogUploader.SetApiBase("");                          // empty → production
            Assert.Equal(LogUploader.DefaultApiBase, LogUploader.ApiBase);
        }
        finally
        {
            LogUploader.SetApiBase(null);   // never leak an override into another test
        }
    }
}

/// <summary>Serializes the classes that read or mutate the process-wide <see cref="LogUploader.ApiBase"/>
/// so the mutation test can never race a reader (xUnit parallelizes across collections).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiBaseCollection
{
    public const string Name = "LogUploader.ApiBase";
}
