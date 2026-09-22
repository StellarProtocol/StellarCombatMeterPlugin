// Review-fix pins for PR #47 (`.superpowers/sdd/combatmeter-chunk-br-review.md`) — the FALLBACK half of
// the brotli chunk upload. Split out of ChunkUploaderBrotliTests.cs to keep both files under the 500-LoC
// guardrail; it reuses that class's HTTP harness (`CapturingHandler`, `MakeTrack`, `PostTrackAsync`).
//
// Same collection (ChunkUploaderHttpCollection, DisableParallelization): these swap
// ChunkUploader.HttpClient AND the process-wide ChunkCompressor latches, restoring both in `finally`.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// Review fixes for PR #47 (combatmeter-chunk-br-review.md): C1, I1, I2 + minors m1/m2.
//
//  (c1)  CRITICAL. Only a 415 used to trigger the plain resend, so a 400 "bad content-encoding body",
//        a 413, or a WAF/edge answering 400/501/502 to an unknown request encoding left the chunk
//        re-sent BROTLI twice more and then ABANDONED — with brotli still on, so every later chunk of
//        every later segment failed identically and the run landed `incomplete-*` with no self-healing.
//        Now: while a chunk went out as `br`, ANY non-2xx that is not the terminal 404 buys ONE
//        immediate plain resend of the same chunk (no retry slot consumed); only a 415 latches brotli
//        off for the process, so a transient 500 does not cost the session its compression.
//  (i1)  The once-per-process fallback line could be swallowed for ever by a null sink burning the
//        latch. The latch is now only consumed when a sink exists.
//  (i2)  One POSITIVE line per process on the first `br` chunk the server accepts — the in-game proof
//        must not rest on the ABSENCE of a warning.
//  (m2)  The `attempt--` claim: a genuinely failing chunk still gets its full 3-attempt ladder after a
//        compat resend. Invariance pin (green before the fix too) — it exists so C1's widening cannot
//        quietly eat a retry slot.

[Collection(ChunkUploaderHttpCollection.Name)]
public sealed class ChunkUploaderBrotliFallbackTests
{
    internal static string EventsJson(int count) => ChunkUploaderBrotliTests.EventsJson(count);

    // (c1a) ---------------------------------------------------------------------------------------
    [Fact]
    public async Task A_400_on_the_compressed_body_resends_that_chunk_plain_at_once_and_keeps_brotli_on()
        => await NonFatalRefusalResendsPlain(HttpStatusCode.BadRequest);

    // (c1b) ---------------------------------------------------------------------------------------
    [Fact]
    public async Task A_413_on_the_compressed_body_resends_that_chunk_plain_at_once_and_keeps_brotli_on()
        => await NonFatalRefusalResendsPlain(HttpStatusCode.RequestEntityTooLarge);

    // (c1c) ---------------------------------------------------------------------------------------
    [Fact]
    public async Task A_502_from_an_edge_that_dislikes_the_request_encoding_also_resends_plain()
        => await NonFatalRefusalResendsPlain(HttpStatusCode.BadGateway);

    private static async Task NonFatalRefusalResendsPlain(HttpStatusCode refusal)
    {
        ChunkCompressor.ResetForTests();
        try
        {
            var (store, refs, envelopes) = ChunkUploaderBrotliTests.MakeTrack("log-c1", EventsJson(120), EventsJson(90));
            // chunk 0: refusal on the br body -> immediate plain resend 200; chunk 1: still br, 200.
            var handler = new ChunkUploaderBrotliTests.CapturingHandler(refusal, HttpStatusCode.OK);

            var (seen, warnings, elapsed) = await ChunkUploaderBrotliTests.PostTrackAsync(handler, "log-c1", store, refs);

            Assert.Equal(3, seen.Count);
            Assert.Equal("br", seen[0].ContentEncoding);

            // The resend is the SAME chunk, PLAIN, byte-for-byte what the pre-2.12.0 path would have sent.
            Assert.Null(seen[1].ContentEncoding);
            Assert.Equal(Encoding.UTF8.GetBytes(envelopes[0]), seen[1].Body);

            // ...immediately: not one of the chunk's retries, so no 1 s backoff.
            Assert.True(elapsed < TimeSpan.FromMilliseconds(900), $"plain resend waited {elapsed.TotalMilliseconds:0} ms — it must be immediate");

            // The chunk is NOT abandoned: no FAILED line.
            Assert.DoesNotContain(warnings, w => w.Contains("FAILED", StringComparison.Ordinal));

            // ...and brotli is still ON — only a 415 latches it off.
            Assert.True(ChunkCompressor.Enabled);
            Assert.Equal("br", seen[2].ContentEncoding);
        }
        finally { ChunkCompressor.ResetForTests(); }
    }

    // (c1d) ---------------------------------------------------------------------------------------
    [Fact]
    public async Task A_terminal_404_still_stops_the_track_and_never_buys_a_plain_resend()
    {
        ChunkCompressor.ResetForTests();
        try
        {
            // C1 put the plain-resend branch AFTER the terminalOn404 check, so that order is load-bearing:
            // a 404 on /buff-events means the ROUTE is absent on that server, and a plain resend would 404
            // too. One request, track stopped, blobs kept for re-upload, brotli untouched.
            var (store, refs, _) = ChunkUploaderBrotliTests.MakeTrack("log-c1d", EventsJson(40), EventsJson(40));
            var handler = new ChunkUploaderBrotliTests.CapturingHandler(HttpStatusCode.NotFound, HttpStatusCode.OK);
            var warnings = new List<string>();
            var original = ChunkUploader.HttpClient;
            ChunkUploader.HttpClient = new HttpClient(handler);
            try
            {
                await ChunkUploader.PostRefsAsync(
                    ChunkUploader.BuffEndpoint("https://x", "sea", 42, "buff chunk"),
                    "log-c1d", refs, store, w => { lock (warnings) warnings.Add(w); });
            }
            finally { ChunkUploader.HttpClient = original; }

            var sent = Assert.Single(handler.Seen);
            Assert.Equal("br", sent.ContentEncoding);
            Assert.Contains(warnings, w => w.Contains("not accepted by server (404)", StringComparison.Ordinal));
            Assert.True(ChunkCompressor.Enabled);
        }
        finally { ChunkCompressor.ResetForTests(); }
    }

    // (m2) ----------------------------------------------------------------------------------------
    [Fact]
    public async Task After_a_compat_resend_a_genuinely_failing_chunk_still_gets_its_full_retry_ladder()
    {
        ChunkCompressor.ResetForTests();
        try
        {
            var (store, refs, _) = ChunkUploaderBrotliTests.MakeTrack("log-m2", EventsJson(40));
            var handler = new ChunkUploaderBrotliTests.CapturingHandler(
                HttpStatusCode.UnsupportedMediaType, HttpStatusCode.InternalServerError);

            var (seen, warnings, _) = await ChunkUploaderBrotliTests.PostTrackAsync(handler, "log-m2", store, refs);

            // 1 br attempt (415) + the free plain resend + the 2 remaining ladder attempts = 4 requests.
            Assert.Equal(4, seen.Count);
            Assert.Equal("br", seen[0].ContentEncoding);
            Assert.All(seen.Skip(1), s => Assert.Null(s.ContentEncoding));
            Assert.Contains(warnings, w => w.Contains("FAILED after retries", StringComparison.Ordinal));
        }
        finally { ChunkCompressor.ResetForTests(); }
    }

    // (d1) ----------------------------------------------------------------------------------------
    [Fact]
    public async Task A_transport_exception_on_the_compressed_body_sends_the_rest_of_that_chunk_plain()
    {
        ChunkCompressor.ResetForTests();
        try
        {
            // An intermediary that RESETS the connection on a request encoding it dislikes raises an
            // EXCEPTION, not a status, so C1's status-based fallback never sees it and the chunk used to
            // die compressed on all three attempts — with nothing latched, so every later chunk followed.
            // Now the remaining attempts of THAT chunk go plain. Deliberately NOT like the 415/4xx path:
            // no free attempt (the ladder keeps its exact 3-attempt shape and its backoff) and no process
            // latch (a network blip must not cost the session its compression).
            var (store, refs, envelopes) = ChunkUploaderBrotliTests.MakeTrack("log-d1", EventsJson(120), EventsJson(90));
            var handler = new ChunkUploaderBrotliTests.CapturingHandler(HttpStatusCode.OK);
            handler.ThrowOnCalls.Add(0);

            var (seen, warnings, elapsed) = await ChunkUploaderBrotliTests.PostTrackAsync(handler, "log-d1", store, refs);

            Assert.Equal(3, seen.Count);
            Assert.Equal("br", seen[0].ContentEncoding);   // the attempt that blew up

            // The retry of the SAME chunk is plain, byte-for-byte what the pre-2.12.0 path would have sent.
            Assert.Null(seen[1].ContentEncoding);
            Assert.Equal(Encoding.UTF8.GetBytes(envelopes[0]), seen[1].Body);

            // ...and it IS a ladder retry, not a free resend: the 1 s backoff was served.
            Assert.True(elapsed >= TimeSpan.FromMilliseconds(800), $"retry came after only {elapsed.TotalMilliseconds:0} ms — the ladder's backoff must still be served");

            Assert.DoesNotContain(warnings, w => w.Contains("FAILED", StringComparison.Ordinal));

            // No process latch: the NEXT chunk is compressed again.
            Assert.True(ChunkCompressor.Enabled);
            Assert.Equal("br", seen[2].ContentEncoding);
            Assert.DoesNotContain(warnings, w => w.Contains("brotli not accepted", StringComparison.Ordinal));
        }
        finally { ChunkCompressor.ResetForTests(); }
    }

    // (i1) ----------------------------------------------------------------------------------------
    [Fact]
    public void A_null_sink_never_burns_the_once_per_process_fallback_line()
    {
        ChunkCompressor.ResetForTests();
        try
        {
            ChunkCompressor.DisableAfterUnsupported(null);       // no sink: must NOT consume the latch
            Assert.False(ChunkCompressor.Enabled);               // ...but the switch itself still fired

            var lines = new List<string>();
            ChunkCompressor.DisableAfterUnsupported(lines.Add);  // a sink now exists: the line must appear
            var line = Assert.Single(lines);
            Assert.Contains("415", line, StringComparison.Ordinal);

            ChunkCompressor.DisableAfterUnsupported(lines.Add);  // ...and exactly once, for ever
            Assert.Single(lines);
        }
        finally { ChunkCompressor.ResetForTests(); }
    }

    // (i2) ----------------------------------------------------------------------------------------
    [Fact]
    public async Task The_first_accepted_br_chunk_logs_one_positive_line_per_process()
    {
        ChunkCompressor.ResetForTests();
        try
        {
            var (store, refs, envelopes) = ChunkUploaderBrotliTests.MakeTrack("log-i2", EventsJson(400), EventsJson(400));
            var handler = new ChunkUploaderBrotliTests.CapturingHandler(HttpStatusCode.OK);

            var (seen, warnings, _) = await ChunkUploaderBrotliTests.PostTrackAsync(handler, "log-i2", store, refs);

            Assert.Equal(2, seen.Count);
            Assert.All(seen, s => Assert.Equal("br", s.ContentEncoding));

            // ONE line, on the FIRST accepted chunk only — never once per chunk.
            var line = Assert.Single(warnings);
            Assert.StartsWith("[CombatMeter.SP1] chunk brotli ON: ", line, StringComparison.Ordinal);
            Assert.Contains($"/{Encoding.UTF8.GetByteCount(envelopes[0])} B (", line, StringComparison.Ordinal);
            Assert.Contains("x)", line, StringComparison.Ordinal);
        }
        finally { ChunkCompressor.ResetForTests(); }
    }

    // (i2b) ---------------------------------------------------------------------------------------
    [Fact]
    public async Task A_plain_send_never_claims_brotli_is_on()
    {
        ChunkCompressor.ResetForTests();
        try
        {
            ChunkCompressor.Encoder = _ => throw new DllNotFoundException("System.IO.Compression.Native");
            var (store, refs, _) = ChunkUploaderBrotliTests.MakeTrack("log-i2b", EventsJson(30));
            var handler = new ChunkUploaderBrotliTests.CapturingHandler(HttpStatusCode.OK);

            var (_, warnings, _) = await ChunkUploaderBrotliTests.PostTrackAsync(handler, "log-i2b", store, refs);

            Assert.DoesNotContain(warnings, w => w.Contains("brotli ON", StringComparison.Ordinal));
        }
        finally { ChunkCompressor.ResetForTests(); }
    }
}
