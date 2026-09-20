// Task 2 (CombatMeter upload resilience, "chunk-loss fix 2" continuation; design of record:
// docs/superpowers/specs/2026-09-20-combatmeter-upload-resilience-design.md). Pins s1-s3 for
// ChunkUploader.UploadSegmentAsync's guards — see that method's doc comment in ChunkUploader.cs for
// the prod measurement this fixes: 18/294 damage-bearing archives/day uploaded a header that DECLARED
// 1-20 chunks and delivered NONE (no dmg/buff/sheet chunk, no positions ref, no chunk request the api
// ever saw). Root cause: UploadSegmentFireAndForget's Task.Run body had NO try/catch anywhere — a
// faulted seg.Completion (the spool's blob-write task chain) or an exception thrown before a track's
// own per-chunk try/catch silently killed the whole discarded task.
//
// RecordingHandler always returns 200 OK and just records which URL each POST landed on, so these
// tests assert "who got POSTed" and "what got logged" — LogUploaderRetryTests/PositionUploaderRetryTests
// already own retry-TIMING ground; this suite never touches ChunkUploader.RetryDelays or backoff.
//
// ChunkUploader.HttpClient is swapped for the duration of each test (restored in `finally`), same
// pattern as LogUploaderRetryTests' WithFakeTransport over LogUploader.HttpClient, serialized via
// ChunkUploaderHttpCollection (DisableParallelization) so no other test racing a real ChunkUploader
// send can observe the swapped client.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stellar.CombatMeter.LogUpload;
using Stellar.CombatMeter.Tests.Fakes;
using Xunit;

namespace Stellar.CombatMeter.Tests;

[Collection(ChunkUploaderHttpCollection.Name)]
public sealed class ChunkUploaderResilienceTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<string> _urls = new();
        public IReadOnlyList<string> Urls { get { lock (_urls) return _urls.ToArray(); } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_urls) _urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });
        }
    }

    private static SpoolChunkRef Ref(string track, string blobName)
        => new(track, 0, 1000L, 2000L, 5, blobName);

    private static SpoolSegment MakeSegment(FakeDataStore store, Task completion)
    {
        var dmg = Ref(SpoolCodec.TrackDmg, "spool/seg-dmg-000.gz");
        var buff = Ref(SpoolCodec.TrackBuff, "spool/seg-buff-000.gz");
        var sheet = Ref(SpoolCodec.TrackSheet, "spool/seg-sheet-000.gz");
        store.Write(dmg.BlobName, SpoolCodec.Gzip("[]"));
        store.Write(buff.BlobName, SpoolCodec.Gzip("[]"));
        store.Write(sheet.BlobName, SpoolCodec.Gzip("[]"));
        return new SpoolSegment("seg", new[] { dmg }, new[] { buff }, new[] { sheet }, Array.Empty<SpoolChunkRef>(),
            false, false, false, false, completion, new SpoolCounts(0, 0, 3));
    }

    private static async Task<(IReadOnlyList<string> Urls, List<string> Warnings, List<string> Infos)> RunAsync(
        SpoolSegment seg, FakeDataStore store, string logId = "log-1")
    {
        var warnings = new List<string>();
        var infos = new List<string>();
        var handler = new RecordingHandler();
        var original = ChunkUploader.HttpClient;
        ChunkUploader.HttpClient = new HttpClient(handler);
        try
        {
            await ChunkUploader.UploadSegmentAsync("https://x", "sea", 42, logId, seg, store,
                new ChunkUploader.SendLog(
                    w => { lock (warnings) warnings.Add(w); },
                    i => { lock (infos) infos.Add(i); }));
        }
        finally { ChunkUploader.HttpClient = original; }
        return (handler.Urls, warnings, infos);
    }

    // s1: a faulted Completion (the spool's blob-write chain) must not abort the whole segment send —
    // all three tracks still POST (their blobs are on disk regardless of what Completion reports), and
    // the fault is logged exactly once. RED on the pre-fix code: `await seg.Completion` was unguarded,
    // so the Task.Run body threw right there and NONE of the three PostRefsAsync calls ever ran — see
    // the fix report for the quoted failure captured by temporarily removing the inner try/catch.
    [Fact]
    public async Task Faulted_completion_still_posts_all_three_tracks_and_logs_once()
    {
        var store = new FakeDataStore();
        var seg = MakeSegment(store, Task.FromException(new InvalidOperationException("blob write chain faulted")));

        var (urls, warnings, _) = await RunAsync(seg, store, "log-s1");

        Assert.Contains(urls, u => u == "https://x/run/sea/42/events");
        Assert.Contains(urls, u => u == "https://x/run/sea/42/buff-events");
        Assert.Contains(urls, u => u == "https://x/run/sea/42/sheet-events");
        var w = Assert.Single(warnings);
        Assert.Contains("FAULTED", w);
        Assert.Contains("log-s1", w);
    }

    // s2: a store whose blob read throws for the dmg track only must not stop the buff/sheet tracks
    // from posting, and — the point of exercising this through the extracted Task-returning
    // UploadSegmentAsync rather than the fire-and-forget wrapper — awaiting it here completes normally,
    // proving no exception escapes to become an unobserved TaskScheduler exception on the discarded
    // fire-and-forget task. Asserts the buff-then-sheet ORDER by index (review F3), not just presence:
    // dmg posts nothing (its one blob is unreadable), so exactly two POSTs land, buff first.
    [Fact]
    public async Task Dmg_blob_read_fault_still_posts_buff_and_sheet_with_no_unobserved_exception()
    {
        var store = new FakeDataStore();
        var seg = MakeSegment(store, Task.CompletedTask);
        store.ThrowOnRead = name => name.Contains("-dmg-", StringComparison.Ordinal);

        var (urls, warnings, _) = await RunAsync(seg, store, "log-s2");   // must not throw

        Assert.Equal(2, urls.Count);   // dmg blob unreadable: nothing to POST for that track
        Assert.Equal("https://x/run/sea/42/buff-events", urls[0]);
        Assert.Equal("https://x/run/sea/42/sheet-events", urls[1]);
        Assert.Contains(warnings, w => w.Contains("threw") && w.Contains("log-s2"));
    }

    // s3: happy path — the start line carries the right per-track counts, the three POSTs still fire in
    // dmg -> buff -> sheet order (unchanged), and nothing is warned.
    [Fact]
    public async Task Happy_path_logs_start_line_and_posts_in_dmg_buff_sheet_order_with_no_warnings()
    {
        var store = new FakeDataStore();
        var seg = MakeSegment(store, Task.CompletedTask);

        var (urls, warnings, infos) = await RunAsync(seg, store, "log-s3");

        Assert.Empty(warnings);
        var info = Assert.Single(infos);
        Assert.Equal("[CombatMeter.SP1] Sending segment chunks for log-s3: dmg=1 buff=1 sheet=1", info);
        Assert.Equal(3, urls.Count);
        Assert.Equal("https://x/run/sea/42/events", urls[0]);
        Assert.Equal("https://x/run/sea/42/buff-events", urls[1]);
        Assert.Equal("https://x/run/sea/42/sheet-events", urls[2]);
    }

    // s4 (review F1 pin — the HANG this fix closes): a segment whose Completion NEVER completes (a
    // stalled store.Write — the exact prod-measured symptom, 18/294 damage-bearing archives/day arriving
    // with a header declaring 1-20 chunks and delivering NONE) must not park UploadSegmentAsync forever.
    // RED on the pre-fix code: `await seg.Completion` was unbounded, so this test hangs / times out with
    // NO POSTs ever recorded — quoted in the fix report. GREEN: CompletionWait (lowered here so the test
    // runs in well under 5s; restored in `finally`) bounds the wait, the "still pending" warning fires
    // exactly once, and all three tracks still POST (their blobs are on disk regardless of what
    // Completion reports).
    [Fact]
    public async Task Never_completing_completion_still_posts_all_three_tracks_after_the_bounded_wait()
    {
        var store = new FakeDataStore();
        var hang = new TaskCompletionSource<bool>().Task;   // never completes, never faults
        var seg = MakeSegment(store, hang);
        var originalWait = ChunkUploader.CompletionWait;
        ChunkUploader.CompletionWait = TimeSpan.FromMilliseconds(200);
        try
        {
            var (urls, warnings, _) = await RunAsync(seg, store, "log-s4");

            Assert.Contains(urls, u => u == "https://x/run/sea/42/events");
            Assert.Contains(urls, u => u == "https://x/run/sea/42/buff-events");
            Assert.Contains(urls, u => u == "https://x/run/sea/42/sheet-events");
            var w = Assert.Single(warnings);
            Assert.Contains("still pending after", w);
            Assert.Contains("log-s4", w);
        }
        finally { ChunkUploader.CompletionWait = originalWait; }
    }
}

/// <summary>Serializes tests that swap ChunkUploader.HttpClient — same DisableParallelization pattern
/// as LogUploaderHttpCollection (LogUploaderRetryTests.cs) so no other test's chunk send can race the
/// swap.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ChunkUploaderHttpCollection
{
    public const string Name = "ChunkUploader.HttpClient";
}
