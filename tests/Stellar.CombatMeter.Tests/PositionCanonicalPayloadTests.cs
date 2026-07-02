using System.Collections.Generic;
using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class PositionCanonicalPayloadTests
{
    [Fact]
    public void Build_MatchesPipeDelimitedShape_WithBodyHash()
    {
        var doc = new PositionUploadDoc(
            Hz: 2,
            MapId: 4201,
            Origin: (0f, 0f),
            Scale: 0.1f,
            Tracks: new Dictionary<string, PositionTrackDto>(),
            Meta: new Dictionary<string, PositionMetaDto>(),
            Sig: "IGNORED-sig",
            Nonce: "nonce1",
            LogId: "cm-x",
            LevelUuid: 123,
            LocalUid: 7,
            StartMs: 100,
            EndMs: 2000);

        var payload = PositionCanonicalPayload.Build(doc);

        Assert.StartsWith("cm-x|123|7|100|2000|nonce1|", payload);
        Assert.Equal(7, payload.Split('|').Length);       // 6 fields + trailing hash
        Assert.DoesNotContain("IGNORED-sig", payload);    // sig is NOT part of the signed payload
    }

    [Fact]
    public void Build_IsIdenticalWithAndWithoutBossAndPlayerHp_WorkerSigParity()
    {
        var bare = new PositionUploadDoc(
            Hz: 2,
            MapId: 4201,
            Origin: (0f, 0f),
            Scale: 0.1f,
            Tracks: new Dictionary<string, PositionTrackDto>(),
            Meta: new Dictionary<string, PositionMetaDto>(),
            Sig: "IGNORED-sig",
            Nonce: "nonce1",
            LogId: "cm-x",
            LevelUuid: 123,
            LocalUid: 7,
            StartMs: 100,
            EndMs: 2000);

        var withExtras = bare with
        {
            BossEntityId = "33301",
            BossHp = new HpTrack(0, new[] { 99 }),
            PlayerHp = new Dictionary<string, HpTrack> { ["101"] = new HpTrack(0, new[] { 1 }) },
        };

        // The signed payload hashes WriteBodyOnly, which excludes boss + playerHp —
        // so the canonical payload must be identical whether or not they're present.
        Assert.Equal(PositionCanonicalPayload.Build(bare), PositionCanonicalPayload.Build(withExtras));
    }
}
