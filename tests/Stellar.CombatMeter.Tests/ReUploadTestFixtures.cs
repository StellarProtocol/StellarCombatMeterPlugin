// Minimal fixture builders for the real CombatLog / EventChunk / PositionUploadDoc shapes,
// mirrored from LogUploadTests.cs / PositionCanonicalPayloadTests.cs so ReUploadCaptureTests
// exercises the SAME record constructors the production uploaders use.

using System;
using System.Collections.Generic;
using Stellar.CombatMeter.LogUpload;
using Stellar.CombatMeter.Replay;

namespace Stellar.CombatMeter.Tests;

internal static class ReUploadTestFixtures
{
    /// <summary>A minimal but structurally real CombatLog: one actor, one damage event.</summary>
    internal static CombatLog MinimalLog(string logId, string region, long levelUuid)
    {
        var enc = new Encounter("dungeon", levelUuid, null, 100, 0, null, 0, null, null, 0,
            "kill", 1000L, 2000L, 1000L, 0);
        var upl = new Uploader(42L, "sig", "nonce");
        var hdr = new LogHeader(logId, 2000L, "2.11", region, "1.9.0", "1.1.0", "unlisted", enc, upl);
        var actors = new Dictionary<string, Actor>
        {
            ["1"] = new Actor("Tester", "player", 1L, true, 42L, 1, 60, 100_000L, 200_000L,
                Array.Empty<long[]>(), Array.Empty<int[]>(), Array.Empty<int[]>(), Array.Empty<Fashion>()),
        };
        var events = new List<CombatLogEvent>
        {
            new DamageEvent(1500L, "1", "2", 5, 999, 990, 0, false, false, false, false, 0, 0, 0),
        };
        return new CombatLog(1, hdr, actors, events);
    }

    /// <summary>Exactly one <see cref="EventChunk"/> (default chunk size comfortably exceeds <paramref name="count"/>).</summary>
    internal static List<EventChunk> OneChunk(int count)
    {
        var events = new List<CombatLogEvent>(count);
        for (var i = 0; i < count; i++)
            events.Add(new DamageEvent(1000L + i, "1", "2", 5, 100, 100, 0, false, false, false, false, 0, 0, 0));
        return EventChunker.Chunk(events);
    }

    /// <summary>A minimal PositionUploadDoc — empty tracks/meta, just the header identity fields.</summary>
    internal static PositionUploadDoc MinimalPositions(long levelUuid) =>
        new PositionUploadDoc(
            Hz: 2,
            MapId: 4201,
            Origin: (0f, 0f),
            Scale: 0.1f,
            Tracks: new Dictionary<string, PositionTrackDto>(),
            Meta: new Dictionary<string, PositionMetaDto>(),
            LogId: "cm-x",
            LevelUuid: levelUuid,
            LocalUid: 7,
            StartMs: 100,
            EndMs: 2000);
}
