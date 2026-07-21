using System.Collections.Generic;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class ReUploadCaptureTests
{
    [Fact]
    public void BuildReUploadPayload_matches_the_uploaders_serialization()
    {
        // Arrange a minimal CombatLog + one chunk + a positions doc using existing test builders.
        var log = ReUploadTestFixtures.MinimalLog(logId: "cm-fix-1", region: "sea", levelUuid: 77);
        var chunks = ReUploadTestFixtures.OneChunk(count: 3);
        var positions = ReUploadTestFixtures.MinimalPositions(levelUuid: 77);

        var payload = Plugin.BuildReUploadPayload(log, chunks, positions);

        Assert.Equal(CombatLogWriter.Write(log), payload.Summary);
        Assert.Single(payload.Chunks);
        Assert.Equal(ChunkUploader.BuildEnvelope(log.Header.LogId, chunks[0]), payload.Chunks[0]);
        Assert.Equal(Stellar.CombatMeter.Replay.PositionJsonWriter.Write(positions), payload.Positions);
        Assert.Equal(log.Header.Region, payload.Region);
        Assert.Equal(log.Header.Encounter.LevelUuid, payload.LevelUuid);
        Assert.Equal(log.Header.LogId, payload.LogId);
    }

    [Fact]
    public void BuildReUploadPayload_null_positions_yields_null()
    {
        var log = ReUploadTestFixtures.MinimalLog("cm-fix-2", "jp", 5);
        var payload = Plugin.BuildReUploadPayload(log, new List<EventChunk>(), replayDoc: null);
        Assert.Null(payload.Positions);
        Assert.Empty(payload.Chunks);
    }
}
