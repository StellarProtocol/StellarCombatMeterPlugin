// Tests for EventChunker: splits a chronological CombatLogEvent list into ~4k-event
// upload chunks (Task 8 wires this into the uploader; here we verify the pure split logic).

using System;
using System.Collections.Generic;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class EventChunkerTests
{
    private static List<CombatLogEvent> MakeEvents(int count)
    {
        var events = new List<CombatLogEvent>(count);
        for (var i = 0; i < count; i++)
            events.Add(new SkillEvent(i * 100L, "1", 1, 0));
        return events;
    }

    [Fact]
    public void Chunks_split_at_size_with_short_tail()
    {
        var events = MakeEvents(9_000);                    // helper: sequential Ms = i * 100
        var chunks = EventChunker.Chunk(events, 4_000);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(3, c.Total));
        Assert.Equal(4_000, chunks[0].Events.Count);
        Assert.Equal(1_000, chunks[2].Events.Count);
        Assert.Equal(events[0].Ms, chunks[0].StartMs);
        Assert.Equal(events[3_999].Ms, chunks[0].EndMs);
        Assert.Equal(events[8_999].Ms, chunks[2].EndMs);
    }

    [Fact]
    public void Empty_input_yields_no_chunks() => Assert.Empty(EventChunker.Chunk(Array.Empty<CombatLogEvent>()));
}
