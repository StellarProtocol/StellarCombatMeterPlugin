using System.Collections.Generic;
using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class PositionJsonWriterPlayerHpTests
{
    private static PositionUploadDoc Doc(
        string bossId = "", HpTrack? bossHp = null,
        IReadOnlyDictionary<string, HpTrack>? playerHp = null)
        => new(
            Hz: 2, MapId: 1, Origin: (0f, 0f), Scale: 0.1f,
            Tracks: new Dictionary<string, PositionTrackDto>(),
            Meta: new Dictionary<string, PositionMetaDto>(),
            BossEntityId: bossId, BossHp: bossHp, PlayerHp: playerHp);

    [Fact]
    public void WritesPlayerHpAfterBodyFields()
    {
        var doc = Doc(playerHp: new Dictionary<string, HpTrack>
        {
            ["101"] = new HpTrack(0, new[] { 100, 90 }),
            ["102"] = new HpTrack(500, new[] { 80 }),
        });
        var json = PositionJsonWriter.Write(doc);
        Assert.Contains("\"playerHp\":{\"101\":{\"ms0\":0,\"pct\":[100,90]},\"102\":{\"ms0\":500,\"pct\":[80]}}", json);
    }

    [Fact]
    public void OmitsPlayerHpWhenNullOrEmpty()
    {
        Assert.DoesNotContain("playerHp", PositionJsonWriter.Write(Doc()));
        Assert.DoesNotContain("playerHp",
            PositionJsonWriter.Write(Doc(playerHp: new Dictionary<string, HpTrack>())));
    }

    [Fact]
    public void BodyOnlyExcludesBossAndPlayerHp_WorkerSigParity()
    {
        var withExtras = Doc(
            bossId: "33301", bossHp: new HpTrack(0, new[] { 99 }),
            playerHp: new Dictionary<string, HpTrack> { ["101"] = new HpTrack(0, new[] { 1 }) });
        var body = PositionJsonWriter.WriteBodyOnly(withExtras);
        // Worker verify.ts hashes exactly {hz,mapId,origin,scale,tracks,meta}.
        Assert.Equal("{\"hz\":2,\"mapId\":1,\"origin\":[0,0],\"scale\":0.1,\"tracks\":{},\"meta\":{}}", body);
        // Canonical payload therefore identical with/without boss + playerHp.
        Assert.Equal(PositionJsonWriter.WriteBodyOnly(Doc()), body);
    }
}
