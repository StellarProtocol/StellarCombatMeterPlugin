using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class PositionTrackAssemblerTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static PositionTrack MakeTrack(params (int ms, float x, float y, float z, float yaw)[] samples)
    {
        var t = new PositionTrack(maxSamples: 3600);
        foreach (var s in samples)
            t.Add(new PositionSample(s.ms, s.x, s.y, s.z, s.yaw));
        return t;
    }

    // ── Test 1: quantize + delta correctness ─────────────────────────────────

    [Fact]
    public void Assemble_QuantizesAndDeltaEncodes()
    {
        // 3 samples: x=0,1.0,1.2; y=5.0,5.0,5.0; z=0,0,0; yaw=0,90,180
        var tracks = new Dictionary<EntityId, PositionTrack>
        {
            [new EntityId(42)] = MakeTrack(
                (0,    0f,   5f, 0f, 0f),
                (500,  1.0f, 5f, 0f, 90f),
                (1000, 1.2f, 5f, 0f, 180f))
        };

        var doc = PositionTrackAssembler.Assemble(
            tracks: tracks,
            hz: 2,
            mapId: 7,
            origin: (0f, 0f),
            scale: 0.1f);

        Assert.Single(doc.Tracks);
        var dto = doc.Tracks["42"];

        // ms0 = first sample ms
        Assert.Equal(0, dto.Ms0);

        // dx: absolute [0, 10, 12] → delta [0, 10, 2]
        Assert.Equal(new[] { 0, 10, 2 }, dto.Dx);

        // dz: all 0 → delta [0, 0, 0]
        Assert.Equal(new[] { 0, 0, 0 }, dto.Dz);

        // y: all 5.0 → quantized 50, 50, 50 → delta [50, 0, 0]
        Assert.Equal(new[] { 50, 0, 0 }, dto.Y);

        // yaw: 0→0, 90→90, 180→180 → delta [0, 90, 90]
        Assert.Equal(new[] { 0, 90, 90 }, dto.Yaw);
    }

    // ── Test 2: WriteBodyOnly omits sig/nonce ────────────────────────────────

    [Fact]
    public void WriteBodyOnly_OmitsSigAndNonce()
    {
        var tracks = new Dictionary<EntityId, PositionTrack>
        {
            [new EntityId(1)] = MakeTrack((0, 0f, 0f, 0f, 0f))
        };

        var doc = PositionTrackAssembler.Assemble(
            tracks: tracks,
            hz: 2,
            mapId: 1,
            origin: (0f, 0f),
            scale: 0.1f);

        var body = PositionJsonWriter.WriteBodyOnly(doc);

        Assert.DoesNotContain("sig", body);
        Assert.DoesNotContain("nonce", body);
        Assert.Contains("hz", body);
        Assert.Contains("mapId", body);
        Assert.Contains("tracks", body);
        Assert.Contains("meta", body);
    }

    // ── Test 3: golden-string — exact byte match with JS JSON.stringify ───────

    [Fact]
    public void WriteBodyOnly_GoldenString()
    {
        // One track: entity 42, 3 samples
        // x: 0, 1.0, 1.2 → quantize(0.1) → 0, 10, 12 → delta [0, 10, 2]
        // z: 0, 0, 0     → quantize(0.1) → 0,  0,  0 → delta [0,  0, 0]
        // y: 5, 5, 5     → quantize(0.1) → 50, 50, 50 → delta [50, 0, 0]
        // yaw: 0, 90, 180 → quantizeYaw → 0, 90, 180 → delta [0, 90, 90]
        // ms0 = 0

        // One meta entry: entity 42 → kind="player", name="Dax", professionId=12
        var tracks = new Dictionary<EntityId, PositionTrack>
        {
            [new EntityId(42)] = MakeTrack(
                (0,    0f,  5f, 0f, 0f),
                (500,  1.0f, 5f, 0f, 90f),
                (1000, 1.2f, 5f, 0f, 180f))
        };

        var meta = new Dictionary<EntityId, PositionMetaDto>
        {
            [new EntityId(42)] = new PositionMetaDto("player", "Dax", 12)
        };

        var doc = PositionTrackAssembler.Assemble(
            tracks: tracks,
            hz: 2,
            mapId: 7,
            origin: (0f, 0f),
            scale: 0.1f,
            meta: meta);

        var body = PositionJsonWriter.WriteBodyOnly(doc);

        // Expected: compact JSON matching JS JSON.stringify key order:
        // hz, mapId, origin, scale, tracks, meta
        // tracks keys sorted ascending by numeric entity id
        // meta keys sorted ascending by numeric entity id
        const string expected =
            "{\"hz\":2,\"mapId\":7,\"origin\":[0,0],\"scale\":0.1," +
            "\"tracks\":{\"42\":{\"ms0\":0,\"dx\":[0,10,2],\"dz\":[0,0,0],\"y\":[50,0,0],\"yaw\":[0,90,90]}}," +
            "\"meta\":{\"42\":{\"kind\":\"player\",\"name\":\"Dax\",\"professionId\":12}}}";

        Assert.Equal(expected, body);
    }

    // ── Test 4: meta name JSON-escapes special chars ──────────────────────────

    [Fact]
    public void WriteBodyOnly_EscapesMetaName()
    {
        var tracks = new Dictionary<EntityId, PositionTrack>
        {
            [new EntityId(1)] = MakeTrack((0, 0f, 0f, 0f, 0f))
        };
        var meta = new Dictionary<EntityId, PositionMetaDto>
        {
            [new EntityId(1)] = new PositionMetaDto("player", "A\"B\\C\nD", 5)
        };

        var doc = PositionTrackAssembler.Assemble(
            tracks: tracks,
            hz: 1,
            mapId: 0,
            origin: (0f, 0f),
            scale: 0.1f,
            meta: meta);

        var body = PositionJsonWriter.WriteBodyOnly(doc);
        // name should appear JSON-escaped
        Assert.Contains("\"A\\\"B\\\\C\\nD\"", body);
    }

    // ── Test 5: multiple entities sorted ascending by id ─────────────────────

    [Fact]
    public void WriteBodyOnly_TrackKeysAscendingById()
    {
        var tracks = new Dictionary<EntityId, PositionTrack>
        {
            [new EntityId(200)] = MakeTrack((0, 0f, 0f, 0f, 0f)),
            [new EntityId(10)]  = MakeTrack((0, 0f, 0f, 0f, 0f)),
            [new EntityId(50)]  = MakeTrack((0, 0f, 0f, 0f, 0f)),
        };

        var doc = PositionTrackAssembler.Assemble(
            tracks: tracks,
            hz: 1,
            mapId: 0,
            origin: (0f, 0f),
            scale: 0.1f);

        var body = PositionJsonWriter.WriteBodyOnly(doc);
        var idx10  = body.IndexOf("\"10\"",  System.StringComparison.Ordinal);
        var idx50  = body.IndexOf("\"50\"",  System.StringComparison.Ordinal);
        var idx200 = body.IndexOf("\"200\"", System.StringComparison.Ordinal);
        Assert.True(idx10 < idx50 && idx50 < idx200, $"Expected 10 < 50 < 200 in: {body}");
    }

    // ── Test 6 (C1): scale is emitted as exact "0.1" token ───────────────────

    [Fact]
    public void WriteBodyOnly_ScaleEmittedAsExactToken()
    {
        var tracks = new Dictionary<EntityId, PositionTrack>
        {
            [new EntityId(1)] = MakeTrack((0, 0f, 0f, 0f, 0f))
        };

        var doc = PositionTrackAssembler.Assemble(
            tracks: tracks,
            hz: 2,
            mapId: 1,
            origin: (0f, 0f),
            scale: 0.1f);

        var body = PositionJsonWriter.WriteBodyOnly(doc);
        // Must contain the exact canonical token — not "0.1000000015" or any other
        // runtime-dependent float representation.
        Assert.Contains("\"scale\":0.1,", body);
    }

    // ── Test 7 (I1): Write() full-doc includes all worker-schema header fields ─

    [Fact]
    public void Write_FullDoc_ContainsAllHeaderFields()
    {
        var tracks = new Dictionary<EntityId, PositionTrack>
        {
            [new EntityId(1)] = MakeTrack((0, 0f, 0f, 0f, 0f))
        };

        var assembled = PositionTrackAssembler.Assemble(
            tracks: tracks,
            hz: 2,
            mapId: 1,
            origin: (0f, 0f),
            scale: 0.1f);

        // Simulate the upload caller filling header fields via with-expression.
        var doc = assembled with
        {
            LogId    = "log-abc-123",
            LevelUuid = 9876543210L,
            LocalUid  = 1111111111L,
            StartMs   = 1000L,
            EndMs     = 5000L,
            Nonce     = "nonce-xyz",
            Sig       = "sig-aaa",
        };

        var full = PositionJsonWriter.Write(doc);

        // All worker-schema header fields must appear.
        Assert.Contains("\"logId\":\"log-abc-123\"",   full);
        Assert.Contains("\"levelUuid\":9876543210",    full);
        Assert.Contains("\"localUid\":1111111111",     full);
        Assert.Contains("\"startMs\":1000",            full);
        Assert.Contains("\"endMs\":5000",              full);
        Assert.Contains("\"nonce\":\"nonce-xyz\"",     full);
        Assert.Contains("\"sig\":\"sig-aaa\"",         full);

        // Body fields must also be present.
        Assert.Contains("\"hz\":2",    full);
        Assert.Contains("\"scale\":0.1", full);
        Assert.Contains("\"tracks\":", full);
        Assert.Contains("\"meta\":",   full);

        // WriteBodyOnly must NOT include header fields (signed body is header-free).
        var body = PositionJsonWriter.WriteBodyOnly(doc);
        Assert.DoesNotContain("logId",    body);
        Assert.DoesNotContain("levelUuid", body);
        Assert.DoesNotContain("localUid", body);
        Assert.DoesNotContain("startMs",  body);
        Assert.DoesNotContain("endMs",    body);
    }

    // ── Test 4: msOffset rebases the timeline zero point (movement-before-combat fix) ──

    [Fact]
    public void Assemble_WithZeroOffset_LeavesMs0Unchanged()
    {
        var tracks = new Dictionary<EntityId, PositionTrack>
        {
            [new EntityId(1)] = MakeTrack((0, 0f, 0f, 0f, 0f), (500, 1f, 0f, 0f, 0f)),
        };

        var doc = PositionTrackAssembler.Assemble(tracks, hz: 2, mapId: 1, origin: (0f, 0f), scale: 0.1f);

        Assert.Equal(0, doc.Tracks["1"].Ms0);
    }

    [Fact]
    public void Assemble_WithNegativeOffset_ShiftsMs0EarlierAllowingNegativeTimes()
    {
        // Sampling now starts at dungeon-enter, ahead of combat start — MaybeUploadReplay
        // computes a negative msOffset (capture start - combat start) so pre-combat samples land
        // at negative ms relative to the combat clock, extending the pre-fix "first sample is
        // always ms=0 at combat start" contract backward instead of breaking it.
        var tracks = new Dictionary<EntityId, PositionTrack>
        {
            // First sample captured 4000ms before combat started (walking to the pull).
            [new EntityId(1)] = MakeTrack((0, 0f, 0f, 0f, 0f), (500, 1f, 0f, 0f, 0f)),
        };

        var doc = PositionTrackAssembler.Assemble(
            tracks, hz: 2, mapId: 1, origin: (0f, 0f), scale: 0.1f, msOffset: -4000);

        Assert.Equal(-4000, doc.Tracks["1"].Ms0);
        // Only Ms0 shifts — the delta-encoded position/yaw arrays are untouched by the offset.
        Assert.Equal(new[] { 0, 10 }, doc.Tracks["1"].Dx);
    }

    [Fact]
    public void Assemble_OffsetDoesNotApplyToEmptyTrackSentinel()
    {
        // An empty track's Ms0 sentinel (0) is a "no samples" marker, not a real timestamp — the
        // offset must not be applied to it (would otherwise fabricate a bogus negative time for a
        // track that was never actually sampled).
        var tracks = new Dictionary<EntityId, PositionTrack>
        {
            [new EntityId(1)] = new PositionTrack(maxSamples: 8),   // no samples added
        };

        var doc = PositionTrackAssembler.Assemble(
            tracks, hz: 2, mapId: 1, origin: (0f, 0f), scale: 0.1f, msOffset: -4000);

        Assert.Equal(0, doc.Tracks["1"].Ms0);
        Assert.Empty(doc.Tracks["1"].Dx);
    }
}
