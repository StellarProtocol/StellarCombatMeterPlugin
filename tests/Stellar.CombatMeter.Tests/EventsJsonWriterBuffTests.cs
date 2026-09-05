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
}
