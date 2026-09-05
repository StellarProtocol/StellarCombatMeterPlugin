using System.Linq;
using System.Threading.Tasks;
using Stellar.CombatMeter.LogUpload;
using Stellar.CombatMeter.Tests.Fakes;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class SpoolTrackTests
{
    static SkillEvent Ev(int i) => new(i * 100L, "640", 1, 101);

    [Fact]
    public async Task Seals_a_full_batch_as_one_gzipped_blob_and_ref()
    {
        var store = new FakeDataStore();
        var t = new SpoolTrack("dmg", "seg1", store, chunkEvents: 4);
        for (int i = 0; i < 4; i++) t.Add(Ev(i));
        var (refs, truncated, done, _) = t.Seal();
        await done;

        Assert.False(truncated);
        var r = Assert.Single(refs);
        Assert.Equal(("dmg", 0, 0L, 300L, 4), (r.Track, r.Index, r.StartMs, r.EndMs, r.Count));
        Assert.Equal("spool/seg1-dmg-000.gz", r.BlobName);
        var json = SpoolCodec.Gunzip(store.Read(r.BlobName)!);
        Assert.StartsWith("[", json);
        Assert.Equal(4, json.Split("\"t\":\"skill\"").Length - 1);
    }

    [Fact]
    public async Task Short_tail_is_sealed_on_Seal_with_correct_bounds()
    {
        var store = new FakeDataStore();
        var t = new SpoolTrack("dmg", "seg1", store, chunkEvents: 4);
        for (int i = 0; i < 9; i++) t.Add(Ev(i));
        var (refs, _, done, _) = t.Seal();
        await done;
        Assert.Equal(3, refs.Count);
        Assert.Equal(new[] { 0, 1, 2 }, refs.Select(r => r.Index));
        Assert.Equal(1, refs[2].Count);
        Assert.Equal(800L, refs[2].StartMs);
        Assert.Equal(3, store.Writes);
    }

    [Fact]
    public async Task Exceeding_max_chunks_drops_and_flags()
    {
        var store = new FakeDataStore();
        var t = new SpoolTrack("buff", "seg1", store, chunkEvents: 2, maxChunks: 2);
        for (int i = 0; i < 7; i++) t.Add(Ev(i));
        var (refs, truncated, done, _) = t.Seal();
        await done;
        Assert.True(truncated);
        Assert.Equal(2, refs.Count);
        Assert.Equal(2, store.Writes);
    }

    [Fact]
    public async Task Empty_track_seals_to_nothing()
    {
        var t = new SpoolTrack("buff", "seg1", new FakeDataStore(), chunkEvents: 4);
        var (refs, truncated, done, _) = t.Seal();
        await done;
        Assert.Empty(refs);
        Assert.False(truncated);
    }

    // A serialization/gzip/write fault must NEVER fault the segment's Completion: the uploader AWAITS it
    // (ChunkUploader.UploadSegmentFireAndForget), so a faulted task would abort the whole segment's upload
    // — including the chunks that DID land. A blob that failed to land is detected downstream as
    // store.Read(name) == null and warned + skipped there, per chunk.
    [Fact]
    public async Task A_failed_blob_write_never_faults_the_completion()
    {
        var store = new FakeDataStore { ThrowOnWrite = true };
        var t = new SpoolTrack("dmg", "seg1", store, chunkEvents: 2);
        t.Add(Ev(0)); t.Add(Ev(1));
        var (refs, _, done, _) = t.Seal();

        await done;                                   // must not throw
        Assert.True(done.IsCompletedSuccessfully);
        Assert.Single(refs);
        Assert.Null(store.Read(refs[0].BlobName));    // blob absent — the uploader skips it
    }

    // A write fault must never be SILENT, even though (above) it must never fault the completion task —
    // WriteFaults is the surviving signal a caller can warn on. Read AFTER awaiting Completion: at the
    // moment Seal() returns, the background write may not have run yet, so the tuple's own faultsSoFar
    // can still read 0 — this asserts the live property instead.
    [Fact]
    public async Task A_failed_blob_write_is_counted_via_WriteFaults()
    {
        var store = new FakeDataStore { ThrowOnWrite = true };
        var t = new SpoolTrack("dmg", "seg1", store, chunkEvents: 2);
        t.Add(Ev(0)); t.Add(Ev(1));
        var (_, _, done, _) = t.Seal();

        await done;
        Assert.Equal(1, t.WriteFaults);
    }

    [Fact]
    public void Codec_round_trips()
    {
        const string s = "[{\"t\":\"skill\"}]";
        Assert.Equal(s, SpoolCodec.Gunzip(SpoolCodec.Gzip(s)));
        Assert.Equal("spool/abc-buff-007.gz", SpoolCodec.BlobName("abc", "buff", 7));
    }
}
