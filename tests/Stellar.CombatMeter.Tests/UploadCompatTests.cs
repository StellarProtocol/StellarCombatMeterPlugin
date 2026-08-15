using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Client-side mirror of the server upload floor (services/stellar-logs src/uploadCompat.ts). The plugin
// asks GET /api/upload/compat for the min accepted version and compares its OWN version; when below it
// surfaces an "update" notice and withholds the send. Fail-open by design: an unreachable/garbled
// endpoint (or an unreadable own version) must NEVER nag or withhold. Owner ask 2026-08-16.
public sealed class UploadCompatTests
{
    [Theory]
    [InlineData("2.1.0", "2.2.1", true)]   // a real observed old client — below
    [InlineData("1.7.1", "2.2.1", true)]   // the oldest observed client — below
    [InlineData("2.2.0", "2.2.1", true)]   // one patch below
    [InlineData("2.2.1", "2.2.1", false)]  // the floor itself — inclusive, allowed
    [InlineData("2.2.2", "2.2.1", false)]  // above
    [InlineData("2.3.0", "2.2.1", false)]  // above
    [InlineData("2.2.1.0", "2.2.1", false)] // 4-part assembly form (ToString()) tolerated, == floor
    [InlineData("2.10.0", "2.2.1", false)] // numeric compare: 2.10 > 2.2 (a string compare would say below)
    [InlineData("10.0.0", "2.2.1", false)]
    public void IsBelowFloor_compares_numerically_and_is_inclusive(string current, string min, bool expected)
        => Assert.Equal(expected, UploadCompat.IsBelowFloor(current, min));

    [Theory]
    [InlineData(null, "2.2.1")]     // own version unreadable
    [InlineData("2.1.0", null)]     // endpoint gave no floor
    [InlineData("2.1.0", "")]       // empty
    [InlineData("2.1.0", "garbage")] // unparseable floor
    [InlineData("garbage", "2.2.1")] // unparseable own version
    public void IsBelowFloor_fails_open_on_any_unparseable_input(string? current, string? min)
        => Assert.False(UploadCompat.IsBelowFloor(current, min));

    [Fact]
    public void TryParseCompat_reads_minPluginVer_and_message()
    {
        var json = "{\"minPluginVer\":\"2.2.1\",\"message\":\"Your CombatMeter is out of date. Update it.\"}";
        Assert.True(UploadCompat.TryParseCompat(json, out var min, out var message));
        Assert.Equal("2.2.1", min);
        Assert.Equal("Your CombatMeter is out of date. Update it.", message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"message\":\"no version here\"}")] // missing minPluginVer => cannot decide => fail
    public void TryParseCompat_fails_on_a_response_without_a_usable_floor(string json)
        => Assert.False(UploadCompat.TryParseCompat(json, out _, out _));
}
