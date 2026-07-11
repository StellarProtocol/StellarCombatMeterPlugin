// Task 8: chunk-upload envelope serialization. The HTTP posture (fire-and-forget, retries,
// backoff) is plumbing copied from LogUploader and is not unit-testable without a live/mocked
// endpoint; this file covers the testable core — the JSON envelope shape POSTed per chunk to
// {base}/run/{levelUuid}/events, and that its `events` array is byte-identical to the same
// event serialization the summary blob used to carry (EventsJsonWriter — shared with CanonicalPayload).

using System.Collections.Generic;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class ChunkEnvelopeWriterTests
{
    [Fact]
    public void BuildEnvelope_serializes_index_total_count_and_events()
    {
        var events = new List<CombatLogEvent>
        {
            new SkillEvent(100L, "1", 10, 101),
            new DamageEvent(200L, "1", "2", 10, 500, 490, 0, true, false, false, false, 1, 0, 0),
        };
        var chunk = new EventChunk(0, 3, 100L, 200L, events);

        var json = ChunkUploader.BuildEnvelope("cm-test-log", chunk);

        Assert.Contains("\"logId\":\"cm-test-log\"", json);
        Assert.Contains("\"index\":0", json);
        Assert.Contains("\"total\":3", json);
        Assert.Contains("\"startMs\":100", json);
        Assert.Contains("\"endMs\":200", json);
        Assert.Contains("\"count\":2", json);
        Assert.Contains("\"events\":[", json);
        // Field-shape parity with the blob's event serialization (both ride EventsJsonWriter).
        Assert.Contains("\"t\":\"skill\"", json);
        Assert.Contains("\"t\":\"dmg\"", json);
        Assert.Equal(EventsJsonWriter.Write(events), ExtractEventsArray(json));
    }

    [Fact]
    public void BuildEnvelope_empty_chunk_events_serializes_as_empty_array()
    {
        var chunk = new EventChunk(2, 3, 500L, 500L, new List<CombatLogEvent>());
        var json = ChunkUploader.BuildEnvelope("cm-test-log", chunk);

        Assert.Contains("\"index\":2", json);
        Assert.Contains("\"count\":0", json);
        Assert.Contains("\"events\":[]", json);
    }

    // -------------------------------------------------------------------------
    // Task 12: region-scoped chunk-upload URL (POST {base}/run/{region}/{levelUuid}/events).
    // -------------------------------------------------------------------------

    [Fact]
    public void ChunkUploader_BuildsRegionScopedUrl()
    {
        Assert.Equal("https://x/run/jp/42/events", ChunkUploader.BuildUrl("https://x", "jp", 42));
    }

    // Pulls the raw `events` array substring out of the envelope so it can be compared
    // byte-for-byte against a standalone EventsJsonWriter.Write call.
    private static string ExtractEventsArray(string envelopeJson)
    {
        const string marker = "\"events\":";
        var start = envelopeJson.IndexOf(marker, System.StringComparison.Ordinal) + marker.Length;
        // The events array is always the last key written before the closing '}'.
        var end = envelopeJson.LastIndexOf('}');
        return envelopeJson[start..end];
    }
}
