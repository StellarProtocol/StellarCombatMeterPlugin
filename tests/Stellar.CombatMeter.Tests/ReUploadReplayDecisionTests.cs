using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class ReUploadReplayDecisionTests
{
    [Fact]
    public void Container_present_deserializes_the_stored_bodies()
    {
        var payload = new ReUploadPayload(1, "sea", 88, "cm-r", "{\"s\":1}",
            new[] { "{\"c\":0}" }, "{\"p\":1}");
        var bytes = ReUploadContainer.Serialize(payload);

        Assert.True(ReUploadContainer.TryDeserialize(bytes, out var back));
        Assert.Equal("{\"s\":1}", back.Summary);
        Assert.Equal("{\"p\":1}", back.Positions);
        Assert.Equal(new[] { "{\"c\":0}" }, back.Chunks);
    }
}
