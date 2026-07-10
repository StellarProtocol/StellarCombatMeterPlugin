// Tests for Task 10's supplement path: SupplementWriter.Write (the tiny own-detail-only
// payload sent instead of the full blob) and LogUploader.BuildPrecheckHeader (the
// X-Stellar-Precheck header sent on every upload, mirroring the server's precheck parser).

using System.Collections.Generic;
using System.Linq;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class SupplementWriterTests
{
    private static Actor MakeActor(string name, bool isLocal, long uid) =>
        new(Name: name, Kind: "player", TeamId: 1, IsLocal: isLocal, Uid: uid,
            ProfessionId: 1, Level: 60, AbilityScore: 0, MaxHp: 1,
            Attributes: new List<long[]>(), Gear: new List<int[]>(), Skills: new List<int[]>(),
            Fashion: new List<Fashion>(),
            GearDetail: isLocal
                ? new List<GearDetail> { new(201, 1, 0, 0, 100, 0, 0, new List<int[]>()) }
                : null);

    private static CombatLog MakeLog() =>
        new(
            V: 5,
            Header: new LogHeader(
                LogId: "cm-test-0001", CapturedAtMs: 0, GameVersion: "1.0", Region: "sea",
                FrameworkVer: null, PluginVer: null, Privacy: "public",
                Encounter: new Encounter(
                    Kind: "dungeon", LevelUuid: 555000111, DungeonGuid: null, MapId: 1, LineId: 0,
                    Name: null, BossId: 0, BossName: null, Difficulty: null, MasterModeScore: 0,
                    Result: "kill", StartMs: 1000, EndMs: 99000, DurationMs: 98000, PassTime: 98),
                Uploader: new Uploader(LocalUid: 42, Sig: "s", Nonce: "n", MasterScore: 4070)),
            Actors: new Dictionary<string, Actor>
            {
                ["100"] = MakeActor("Me", isLocal: true, uid: 42),
                ["200"] = MakeActor("Other", isLocal: false, uid: 43),
            },
            Events: new List<CombatLogEvent>(),
            Derived: null);

    [Fact]
    public void Write_EmitsWindowUploaderAndOnlyLocalActors()
    {
        var log = MakeLog();
        var json = SupplementWriter.Write(log);

        Assert.Contains("\"startMs\":1000", json);
        Assert.Contains("\"endMs\":99000", json);
        Assert.Contains("\"localUid\":42", json);
        Assert.Contains("\"masterScore\":4070", json);
        Assert.Contains("\"100\"", json);         // local actor rides the supplement
        Assert.DoesNotContain("\"200\"", json);   // non-local roster members do not
    }

    [Fact]
    public void BuildPrecheckHeader_MirrorsServerParser()
    {
        var h = LogUploader.BuildPrecheckHeader(MakeLog());
        Assert.Contains("levelUuid=555000111", h);
        Assert.Contains("startMs=1000", h);
        Assert.Contains("endMs=99000", h);
        Assert.Contains("eventCount=0", h);   // serialized events count (chunked auto path), NOT capture count
        Assert.Contains("truncated=0", h);    // Derived null -> not truncated
        Assert.Contains("result=kill", h);
    }
}
