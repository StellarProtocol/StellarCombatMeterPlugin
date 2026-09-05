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
        var (refs, truncated, done) = t.Seal();
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
        var (refs, _, done) = t.Seal();
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
        var (refs, truncated, done) = t.Seal();
        await done;
        Assert.True(truncated);
        Assert.Equal(2, refs.Count);
        Assert.Equal(2, store.Writes);
    }

    [Fact]
    public async Task Empty_track_seals_to_nothing()
    {
        var t = new SpoolTrack("buff", "seg1", new FakeDataStore(), chunkEvents: 4);
        var (refs, truncated, done) = t.Seal();
        await done;
        Assert.Empty(refs);
        Assert.False(truncated);
    }

    [Fact]
    public void Codec_round_trips()
    {
        const string s = "[{\"t\":\"skill\"}]";
        Assert.Equal(s, SpoolCodec.Gunzip(SpoolCodec.Gzip(s)));
        Assert.Equal("spool/abc-buff-007.gz", SpoolCodec.BlobName("abc", "buff", 7));
    }
}
