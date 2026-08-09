using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class PortraitResultParserTests
{
    [Fact] public void AllStored_Included()
        => Assert.Contains(1L, PortraitResultParser.FullyStoredUids(
            "{\"results\":[{\"uid\":1,\"identity\":\"merged\",\"media\":[\"profile:stored\",\"halfbody:stored\"]}]}"));

    [Fact] public void UnchangedCountsAsStored()
        => Assert.Contains(1L, PortraitResultParser.FullyStoredUids(
            "{\"results\":[{\"uid\":1,\"identity\":\"merged\",\"media\":[\"profile:unchanged\",\"halfbody:stored\"]}]}"));

    [Fact] public void FailedMedia_Excluded()
        => Assert.DoesNotContain(2L, PortraitResultParser.FullyStoredUids(
            "{\"results\":[{\"uid\":2,\"identity\":\"merged\",\"media\":[\"profile:stored\",\"halfbody:failed:size\"]}]}"));

    [Fact] public void FailedIdentity_Excluded()
        => Assert.DoesNotContain(3L, PortraitResultParser.FullyStoredUids(
            "{\"results\":[{\"uid\":3,\"identity\":\"failed\",\"media\":[\"profile:stored\"]}]}"));

    [Fact] public void NoMedia_Excluded()
        => Assert.DoesNotContain(4L, PortraitResultParser.FullyStoredUids(
            "{\"results\":[{\"uid\":4,\"identity\":\"merged\",\"media\":[]}]}"));

    [Fact] public void MalformedOrEmpty_EmptySet()
    {
        Assert.Empty(PortraitResultParser.FullyStoredUids(null));
        Assert.Empty(PortraitResultParser.FullyStoredUids("not json"));
    }
}
