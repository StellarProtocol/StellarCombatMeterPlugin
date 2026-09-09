using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class EventsJsonWriterSheetTests
{
    [Fact]
    public void Sheet_row_json_shape()
    {
        var delta = new SheetEvent(1_000, false, new[] { new long[] { 11710, 3350 }, new long[] { 12670, 1200 } });
        var kf = new SheetEvent(900, true, new[] { new long[] { 11710, 3300 } });
        Assert.Equal("[{\"t\":\"sheet\",\"ms\":1000,\"a\":[[11710,3350],[12670,1200]]}]", EventsJsonWriter.Write(new[] { delta }));
        Assert.Equal("[{\"t\":\"sheet\",\"ms\":900,\"k\":1,\"a\":[[11710,3300]]}]", EventsJsonWriter.Write(new[] { kf }));
    }
}
