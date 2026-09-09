using System.Collections.Generic;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class ChunkUploaderSheetTests
{
    [Fact]
    public void Sheet_url_is_its_own_route()
    {
        Assert.Equal("https://x/run/sea/42/sheet-events", ChunkUploader.BuildSheetUrl("https://x", "sea", 42));
    }

    [Fact]
    public void SplitUploadable_routes_three_tracks_and_drops_buffx()
    {
        var refs = new List<SpoolChunkRef>
        {
            new(SpoolCodec.TrackDmg, 0, 1, 2, 10, "spool/a-dmg-000.gz"),
            new(SpoolCodec.TrackBuff, 0, 1, 2, 10, "spool/a-buff-000.gz"),
            new(SpoolCodec.TrackSheet, 0, 1, 2, 10, "spool/a-sheet-000.gz"),
            new(SpoolCodec.TrackBuffRejected, 0, 1, 2, 10, "spool/a-buffx-000.gz"),
        };
        var (dmg, buff, sheet) = ChunkUploader.SplitUploadable(refs);
        Assert.Single(dmg); Assert.Single(buff); Assert.Single(sheet);
        Assert.Equal("spool/a-sheet-000.gz", sheet[0].BlobName);
    }

    // A 404 means different things per endpoint, and getting it wrong loses data. /events has existed all
    // along, so a 404 there is an ordinary per-chunk failure that must keep retrying. /buff-events and
    // /sheet-events ship with this release, so a 404 there is an OLD WORKER: terminal for the whole track,
    // never retried, blobs kept on disk for a later re-upload.
    [Fact]
    public void Each_tracks_404_semantics_match_its_endpoints_age()
    {
        Assert.False(ChunkUploader.DmgEndpoint("https://x", "sea", 42, "chunk").TerminalOn404);
        Assert.True(ChunkUploader.BuffEndpoint("https://x", "sea", 42, "buff chunk").TerminalOn404);
        var sheetEp = ChunkUploader.SheetEndpoint("https://x", "sea", 42, "sheet chunk");
        Assert.True(sheetEp.TerminalOn404);
        Assert.Equal("https://x/run/sea/42/sheet-events", sheetEp.Url);
    }
}
