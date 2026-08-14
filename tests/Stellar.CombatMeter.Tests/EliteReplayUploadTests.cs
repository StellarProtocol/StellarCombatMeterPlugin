using System.Collections.Generic;
using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// ELITE CAPTURE channel (owner ruling 2026-08-13): PositionUploadDoc.Elites is an additive, CAPTURE-ONLY
// list carried outside the canonical (signed) body — same BossTrackDto shape as Bosses, own JSON key
// ("elites"). These tests mirror the position-doc-side conventions PositionJsonWriterPlayerHpTests.cs
// pins for Bosses/PlayerHp/BossHp.
public sealed class EliteReplayUploadTests
{
    private static PositionUploadDoc Doc(IReadOnlyList<BossTrackDto>? elites = null, IReadOnlyList<BossTrackDto>? bosses = null)
        => new(
            Hz: 2, MapId: 1, Origin: (0f, 0f), Scale: 0.1f,
            Tracks: new Dictionary<string, PositionTrackDto>(),
            Meta: new Dictionary<string, PositionMetaDto>(),
            Bosses: bosses, Elites: elites);

    [Fact]
    public void Writer_emits_elites_array_when_present()
    {
        var doc = Doc(elites: new[]
        {
            new BossTrackDto("20", 200100, new HpTrack(0, new[] { 100, 50, 0 })),
            new BossTrackDto("21", 200101, null),
        });

        var json = PositionJsonWriter.Write(doc);

        Assert.Contains("\"elites\"", json);
        Assert.Contains("\"entityId\":\"20\"", json);
        Assert.Contains("\"configId\":200100", json);
        Assert.Contains("\"entityId\":\"21\"", json);
    }

    [Fact]
    public void Writer_omits_elites_when_null_or_empty()
    {
        Assert.DoesNotContain("elites", PositionJsonWriter.Write(Doc()));
        Assert.DoesNotContain("elites", PositionJsonWriter.Write(Doc(elites: System.Array.Empty<BossTrackDto>())));
    }

    // Elites and Bosses are independent arrays — a window can carry both without conflation.
    [Fact]
    public void Writer_emits_both_bosses_and_elites_independently()
    {
        var doc = Doc(
            bosses: new[] { new BossTrackDto("10", 102800, null) },
            elites: new[] { new BossTrackDto("20", 200100, null) });

        var json = PositionJsonWriter.Write(doc);

        Assert.Contains("\"bosses\"", json);
        Assert.Contains("\"elites\"", json);
        Assert.Contains("102800", json);
        Assert.Contains("200100", json);
    }

    // CAPTURE ONLY / signature safety: Elites (like Bosses/BossHp/PlayerHp) is excluded from the
    // canonical body the worker hashes for signing.
    [Fact]
    public void BodyOnlyExcludesElites_WorkerSigParity()
    {
        var withElites = Doc(elites: new[] { new BossTrackDto("20", 200100, new HpTrack(0, new[] { 99 })) });
        var body = PositionJsonWriter.WriteBodyOnly(withElites);

        Assert.Equal("{\"hz\":2,\"mapId\":1,\"origin\":[0,0],\"scale\":0.1,\"tracks\":{},\"meta\":{}}", body);
        Assert.Equal(PositionJsonWriter.WriteBodyOnly(Doc()), body);
    }

    [Fact]
    public void CanonicalPayload_is_invariant_to_elites()
    {
        var withElites = Doc(elites: new[] { new BossTrackDto("20", 200100, null) }) with
        {
            LogId = "pos-1", LevelUuid = 77, LocalUid = 55, StartMs = 1000, EndMs = 2000, Nonce = "abc",
        };
        var without = withElites with { Elites = null };

        Assert.Equal(PositionCanonicalPayload.Build(without), PositionCanonicalPayload.Build(withElites));
    }
}
