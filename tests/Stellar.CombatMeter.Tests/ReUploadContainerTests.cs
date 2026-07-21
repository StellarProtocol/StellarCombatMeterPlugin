using System.Collections.Generic;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class ReUploadContainerTests
{
    [Fact]
    public void Roundtrip_preserves_every_body_byte_identically()
    {
        var payload = new ReUploadPayload(
            V: 1, Region: "sea", LevelUuid: 123456789L, LogId: "cm-20260721-abcd",
            Summary: "{\"header\":{\"logId\":\"cm-20260721-abcd\"},\"events\":[]}",
            Chunks: new[] { "{\"logId\":\"cm-20260721-abcd\",\"index\":0,\"events\":[1,2,3]}",
                            "{\"logId\":\"cm-20260721-abcd\",\"index\":1,\"events\":[4,5]}" },
            Positions: "{\"hz\":2,\"mapId\":4201,\"tracks\":{}}");

        var bytes = ReUploadContainer.Serialize(payload);
        Assert.True(ReUploadContainer.TryDeserialize(bytes, out var back));

        Assert.Equal(payload.Region, back.Region);
        Assert.Equal(payload.LevelUuid, back.LevelUuid);
        Assert.Equal(payload.LogId, back.LogId);
        Assert.Equal(payload.Summary, back.Summary);
        Assert.Equal(payload.Positions, back.Positions);
        Assert.Equal(payload.Chunks, back.Chunks);      // order + exact strings preserved
    }

    [Fact]
    public void Null_positions_and_empty_chunks_roundtrip()
    {
        var payload = new ReUploadPayload(1, "jp", 9L, "cm-x", "{\"a\":1}",
            new string[0], null);
        var bytes = ReUploadContainer.Serialize(payload);
        Assert.True(ReUploadContainer.TryDeserialize(bytes, out var back));
        Assert.Empty(back.Chunks);
        Assert.Null(back.Positions);
    }

    [Fact]
    public void TryDeserialize_of_garbage_returns_false_never_throws()
    {
        Assert.False(ReUploadContainer.TryDeserialize(new byte[] { 0, 1, 2, 3 }, out _));
    }

    [Fact]
    public void ContainerName_is_stable_and_prefixed()
    {
        Assert.Equal("replay/123-456.replaydoc", ReUploadContainer.ContainerName(123, 456));
    }
}
