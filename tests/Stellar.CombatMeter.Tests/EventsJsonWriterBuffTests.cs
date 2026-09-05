using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class EventsJsonWriterBuffTests
{
    [Fact]
    public void Buff_row_carries_src_srcKind_srcId()
    {
        var ev = new BuffEvent(1_000, "1114505856", 80, 2203572, "applied", 1, 1, 5000, "1366688384", 0, 2327);
        var json = EventsJsonWriter.Write(new[] { ev });
        Assert.Contains("\"t\":\"buff\"", json);
        Assert.Contains("\"src\":\"1366688384\"", json);
        Assert.Contains("\"srcKind\":0", json);
        Assert.Contains("\"srcId\":2327", json);
    }

    [Fact]
    public void Buff_row_writes_srcOwner_only_when_present()
    {
        var with = new BuffEvent(1_000, "1", 80, 2110034, "applied", 1, 1, 15000, "557120", 0, 2900340, "1366688384");
        var without = new BuffEvent(1_000, "1", 80, 2110034, "applied", 1, 1, 15000, "1366688384", 0, 2900340);
        Assert.Contains("\"srcOwner\":\"1366688384\"", EventsJsonWriter.Write(new[] { with }));
        Assert.DoesNotContain("srcOwner", EventsJsonWriter.Write(new[] { without }));
    }
}
