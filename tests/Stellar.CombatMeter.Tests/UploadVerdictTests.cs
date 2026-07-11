using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class UploadVerdictTests
{
    [Fact]
    public void Parse_NullOrEmpty_DefaultsToKeptTrue()
    {
        Assert.Equal(new UploadVerdict(true, false), UploadVerdict.Parse(null));
        Assert.Equal(new UploadVerdict(true, false), UploadVerdict.Parse(""));
    }

    [Fact]
    public void Parse_OldServerResponseWithoutFields_DefaultsToKeptTrue()
    {
        var body = "{\"ok\":true,\"levelUuid\":\"123\",\"runUrl\":\"/api/run/123\",\"deduped\":false}";
        Assert.Equal(new UploadVerdict(true, false), UploadVerdict.Parse(body));
    }

    [Fact]
    public void Parse_KeptFalse_HavePositionsTrue()
    {
        var body = "{\"ok\":true,\"deduped\":true,\"kept\":false,\"havePositions\":true}";
        Assert.Equal(new UploadVerdict(false, true), UploadVerdict.Parse(body));
    }

    [Fact]
    public void Parse_ToleratesWhitespace()
    {
        var body = "{ \"kept\" : false , \"havePositions\" : false }";
        Assert.Equal(new UploadVerdict(false, false), UploadVerdict.Parse(body));
    }
}
