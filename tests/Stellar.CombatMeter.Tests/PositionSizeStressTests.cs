using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// HARD SHIP GATE (R1): proves the worst-case position upload payload stays
/// well under the Cloudflare worker decompressed JSON limit (4 MiB guard) and
/// compresses at least 3x.
/// </summary>
public class PositionSizeStressTests
{
    [Fact]
    public void WorstCase_1hr_20players_plus_addChurn_StaysWellUnderWall()
    {
        var tracks = new Dictionary<EntityId, PositionTrack>();
        var meta   = new Dictionary<EntityId, PositionMetaDto>();

        // 20 party members — full 1-hour fight at 2 Hz.
        // PositionTrack(3600) coalesces when full, so the buffer stays at most 3600 samples
        // regardless of how many Add calls we make, keeping memory bounded.
        for (var p = 1; p <= 20; p++)
        {
            var t = new PositionTrack(3600);
            for (var ms = 0; ms < 3_600_000; ms += 500)
                t.Add(new PositionSample(ms, 100f + (ms % 50) * 0.1f, 5f, 200f - (ms % 40) * 0.1f, ms % 360));
            tracks[new EntityId(p)] = t;
            meta[new EntityId(p)]   = new PositionMetaDto("add", "E", 0);
        }

        // Heavy add churn: 300 short-lived entities (~60 s each).
        for (var a = 0; a < 300; a++)
        {
            var t = new PositionTrack(3600);
            for (var ms = 0; ms < 60_000; ms += 500)
                t.Add(new PositionSample(ms, 120f + a, 5f, 220f, ms % 360));
            tracks[new EntityId(10_000 + a)] = t;
            meta[new EntityId(10_000 + a)]   = new PositionMetaDto("add", "E", 0);
        }

        var doc  = PositionTrackAssembler.Assemble(tracks, 2, 4201, (0f, 0f), 0.1f, meta);
        var json = PositionJsonWriter.Write(doc);
        var gzipLen = Gzip(json);

        // Decompressed JSON is what the Cloudflare worker JSON.parses.
        // Hard gate: must stay under 4 MiB (4,194,304 bytes).
        Assert.True(
            json.Length < 4_194_304,
            $"decompressed {json.Length:N0} bytes exceeds 4 MiB guard ({4_194_304:N0})");

        // Gzip compression at Optimal must yield at least 3x ratio on repetitive integer arrays.
        Assert.True(
            gzipLen < json.Length / 3,
            $"gzip {gzipLen:N0} not < 1/3 of {json.Length:N0}");
    }

    private static int Gzip(string s)
    {
        var raw = Encoding.UTF8.GetBytes(s);
        using var ms = new MemoryStream();
        using (var g = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            g.Write(raw, 0, raw.Length);
        return (int)ms.Length;
    }
}
