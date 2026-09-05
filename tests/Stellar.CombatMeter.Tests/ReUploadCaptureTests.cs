using System.Threading.Tasks;
using Stellar.CombatMeter.LogUpload;
using Stellar.CombatMeter.Tests.Fakes;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class ReUploadCaptureTests
{
    // Container V2 (rDPS spool, 2026-09-05): the retained payload stores chunk REFS, not inlined
    // envelope strings — the events already live in the segment's spool blobs, so re-inlining them
    // would double the bytes on disk. The summary/positions bodies are still captured byte-identically
    // to what the send transmits.
    [Fact]
    public async Task BuildReUploadPayload_matches_the_uploaders_serialization()
    {
        var log = ReUploadTestFixtures.MinimalLog(logId: "cm-fix-1", region: "sea", levelUuid: 77);
        var positions = ReUploadTestFixtures.MinimalPositions(levelUuid: 77);

        var store = new FakeDataStore();
        var spool = new EventSpool(store);
        for (var i = 0; i < 3; i++) spool.Add(ReUploadTestFixtures.Damage(1000L + i), ReUploadTestFixtures.Self);
        var seg = spool.Rotate();
        await seg.Completion;

        var payload = Plugin.BuildReUploadPayload(log, seg, positions);

        Assert.Equal(CombatLogWriter.Write(log), payload.Summary);
        Assert.Empty(payload.Chunks);                                   // V2: nothing inlined
        var r = Assert.Single(payload.ChunkRefs);
        Assert.Equal("dmg", r.Track);
        Assert.Equal(3, r.Count);
        Assert.Equal(seg.Dmg[0].BlobName, r.BlobName);
        Assert.NotNull(store.Read(r.BlobName));                         // the ref resolves to a real blob
        Assert.Equal(Stellar.CombatMeter.Replay.PositionJsonWriter.Write(positions), payload.Positions);
        Assert.Equal(log.Header.Region, payload.Region);
        Assert.Equal(log.Header.Encounter.LevelUuid, payload.LevelUuid);
        Assert.Equal(log.Header.LogId, payload.LogId);
        Assert.Equal(ReUploadContainer.Version, payload.V);
    }

    [Fact]
    public void BuildReUploadPayload_null_positions_yields_null()
    {
        var log = ReUploadTestFixtures.MinimalLog("cm-fix-2", "jp", 5);
        var payload = Plugin.BuildReUploadPayload(log, SpoolSegment.Empty, replayDoc: null);
        Assert.Null(payload.Positions);
        Assert.Empty(payload.Chunks);
        Assert.Empty(payload.ChunkRefs);
    }
}
