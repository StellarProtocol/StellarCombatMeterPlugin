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
}
