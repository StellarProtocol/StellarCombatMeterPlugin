// Splits a chronological CombatLogEvent stream into fixed-size chunks for chunked upload
// (Task 8 wires this into the uploader — this file is pure and has no I/O of its own).

using System;
using System.Collections.Generic;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>One upload-sized slice of a chronological event stream.</summary>
internal sealed record EventChunk(int Index, int Total, long StartMs, long EndMs, IReadOnlyList<CombatLogEvent> Events);

/// <summary>Pure splitter: breaks a chronological event list into ~<see cref="ChunkEvents"/>-sized chunks.</summary>
internal static class EventChunker
{
    /// <summary>Target events per chunk (matches the 32 x 4,000 = 128,000 ring bound).</summary>
    internal const int ChunkEvents = 4_000;

    /// <summary>
    /// Splits <paramref name="events"/> (assumed already chronological) into chunks of at most
    /// <paramref name="size"/> events. The final chunk may be shorter. Empty input yields no chunks.
    /// </summary>
    internal static List<EventChunk> Chunk(IReadOnlyList<CombatLogEvent> events, int size = ChunkEvents)
    {
        var chunks = new List<EventChunk>();
        if (events.Count == 0) return chunks;

        var total = (events.Count + size - 1) / size;
        for (var i = 0; i < total; i++)
        {
            var start = i * size;
            var count = Math.Min(size, events.Count - start);
            var slice = new List<CombatLogEvent>(count);
            for (var j = 0; j < count; j++) slice.Add(events[start + j]);
            chunks.Add(new EventChunk(i, total, slice[0].Ms, slice[^1].Ms, slice));
        }
        return chunks;
    }
}
