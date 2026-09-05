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
            Positions: "{\"hz\":2,\"mapId\":4201,\"tracks\":{}}",
            ChunkRefs: new SpoolChunkRef[0]);

        var bytes = ReUploadContainer.Serialize(payload);
        Assert.True(ReUploadContainer.TryDeserialize(bytes, out var back));

        Assert.Equal(payload.V, back.V);
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
            new string[0], null, new SpoolChunkRef[0]);
        var bytes = ReUploadContainer.Serialize(payload);
        Assert.True(ReUploadContainer.TryDeserialize(bytes, out var back));
        Assert.Empty(back.Chunks);
        Assert.Null(back.Positions);
    }

    // V2 (rDPS spool, 2026-09-05): the container stores chunk REFS (pointers to the spool blobs the
    // events already live in) instead of inlined envelope strings. V1 containers written by an older
    // build still read — their `chunks` envelopes stay the re-upload source for those runs.
    [Fact]
    public void V2_round_trips_chunk_refs_and_reads_V1()
    {
        var refs = new[] { new SpoolChunkRef("dmg", 0, 1, 2, 3, "spool/s-dmg-000.gz"), new SpoolChunkRef("buff", 0, 1, 2, 1, "spool/s-buff-000.gz") };
        var p = new ReUploadPayload(ReUploadContainer.Version, "sea", 7, "log-1", "{\"v\":1}", new string[0], null, refs);
        var bytes = ReUploadContainer.Serialize(p);
        Assert.True(ReUploadContainer.TryDeserialize(bytes, out var back));
        Assert.Equal(2, back.ChunkRefs.Count);
        Assert.Equal("spool/s-buff-000.gz", back.ChunkRefs[1].BlobName);
        Assert.Equal(new[] { "spool/s-dmg-000.gz", "spool/s-buff-000.gz" }, ReUploadContainer.ReferencedBlobs(bytes));

        var v1 = new ReUploadPayload(1, "sea", 7, "log-1", "{}", new[] { "{\"index\":0}" }, null, new SpoolChunkRef[0]);
        Assert.True(ReUploadContainer.TryDeserialize(ReUploadContainer.Serialize(v1), out var v1back));
        Assert.Single(v1back.Chunks); Assert.Empty(v1back.ChunkRefs);
    }

    // ReferencedBlobs decides what the startup sweep DELETES, so it must never under-report. It reads a
    // bounded head of the container (chunkRefs is serialized first) — these two pin that the bodies being
    // far larger than that head changes neither answer: a V2 container still yields ALL its refs, and a
    // V1 container (no refs key at all, bodies way past the head) still yields none rather than failing.
    [Fact]
    public void ReferencedBlobs_is_head_read_and_survives_bodies_larger_than_the_head()
    {
        var huge = new string('x', 400_000);
        var refs = new[] { new SpoolChunkRef("dmg", 0, 1, 2, 3, "spool/s-dmg-000.gz"), new SpoolChunkRef("buff", 1, 3, 4, 2, "spool/s-buff-001.gz") };
        var v2 = ReUploadContainer.Serialize(new ReUploadPayload(ReUploadContainer.Version, "sea", 7, "log-1", huge, new string[0], huge, refs));
        Assert.Equal(new[] { "spool/s-dmg-000.gz", "spool/s-buff-001.gz" }, ReUploadContainer.ReferencedBlobs(v2));
        Assert.True(ReUploadContainer.TryDeserialize(v2, out var back));
        Assert.Equal(huge, back.Summary);   // the full read still sees the whole body

        var v1 = ReUploadContainer.Serialize(new ReUploadPayload(1, "sea", 7, "log-1", huge, new[] { "{\"index\":0}" }, huge, new SpoolChunkRef[0]));
        Assert.Empty(ReUploadContainer.ReferencedBlobs(v1));
    }

    [Fact]
    public void ReferencedBlobs_of_garbage_is_empty_never_throws()
    {
        Assert.Empty(ReUploadContainer.ReferencedBlobs(new byte[] { 0, 1, 2, 3 }));
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

    // Pins the duplicate-runid hazard: the game can reuse a levelUuid across genuinely different
    // runs (e.g. re-entering the same instance). ContainerName MUST key on the full (levelUuid,
    // archivedAtMs) composite, not levelUuid alone, or a later run's re-upload container would
    // collide with — and silently mix with — an earlier run that shares the same levelUuid.
    [Fact]
    public void SameLevelUuid_differentArchivedAtMs_yieldDistinctContainerNames()
    {
        const long levelUuid = 244376118654664704L;
        const long t1 = 1784604916545L;
        const long t2 = 1784604940589L;

        var name1 = ReUploadContainer.ContainerName(levelUuid, t1);
        var name2 = ReUploadContainer.ContainerName(levelUuid, t2);

        Assert.NotEqual(name1, name2);
        Assert.Equal("replay/244376118654664704-1784604916545.replaydoc", name1);
        Assert.Equal("replay/244376118654664704-1784604940589.replaydoc", name2);
    }
}
