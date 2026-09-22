// Pins for CombatMeter 2.12.0 — event chunks go on the wire brotli-compressed (owner 2026-09-22:
// "why gzip when we already use brotli?"). Step 1 (the api accepting `Content-Encoding: br` on the chunk
// routes, storing the client's bytes as received; plain JSON unchanged; gzip/other -> 415) is live on
// api.stellarresonance.app as StellarLogs main d85d194c.
//
// What is pinned here, and why each one is load-bearing:
//  (b1) the wire shape: `Content-Encoding: br`, `Content-Type: application/json`, and the body decodes
//       back to the EXACT envelope JSON the plain path would have sent — a chunk whose bytes change
//       meaning on the wire is a data-loss bug, not a performance regression.
//  (b2) NEVER LOSE AN UPLOAD, switch 2: a 415 (an api that predates `br`) resends that same chunk PLAIN
//       immediately — not as one of its retries, so no backoff is burned — and every later chunk in the
//       process goes plain. This is the whole compatibility story; there is no capability endpoint.
//  (b3) NEVER LOSE AN UPLOAD, switch 1: the encoder throwing (the native compression library missing or
//       failing under BepInEx's .NET 6 runtime on Wine) sends plain and logs exactly ONE line per
//       process. Provoked through ChunkCompressor.Encoder, the sanctioned seam - never by breaking the BCL.
//  (b4) the ratio the whole change exists for, on a realistic event envelope.
//
// The existing PR #46 resilience pins (ChunkUploaderResilienceTests) and the 404-semantics pin
// (ChunkUploaderSheetTests) are unchanged by this file and must stay green.
//
// Collection: ChunkUploaderHttpCollection (DisableParallelization) — these swap ChunkUploader.HttpClient
// AND the process-wide ChunkCompressor latches; both are restored in `finally`.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stellar.CombatMeter.LogUpload;
using Stellar.CombatMeter.Tests.Fakes;
using Xunit;

namespace Stellar.CombatMeter.Tests;

[Collection(ChunkUploaderHttpCollection.Name)]
public sealed class ChunkUploaderBrotliTests
{
    internal sealed record Captured(string? ContentEncoding, string? MediaType, byte[] Body);

    /// <summary>Records every request's transfer encoding, media type and RAW body bytes, and answers
    /// with a scripted status per call (the last entry repeats).</summary>
    internal sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode[] _statuses;
        private readonly List<Captured> _seen = new();

        public CapturingHandler(params HttpStatusCode[] statuses) => _statuses = statuses;

        public IReadOnlyList<Captured> Seen { get { lock (_seen) return _seen.ToArray(); } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content!;
            var body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            int n;
            lock (_seen)
            {
                n = _seen.Count;
                _seen.Add(new Captured(
                    content.Headers.ContentEncoding.Count == 0 ? null : string.Join(",", content.Headers.ContentEncoding),
                    content.Headers.ContentType?.MediaType,
                    body));
            }
            var status = _statuses.Length == 0 ? HttpStatusCode.OK : _statuses[Math.Min(n, _statuses.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent("{\"ok\":true}") };
        }
    }

    /// <summary>A realistic damage-chunk events array — the same field set and value shapes as a real
    /// uploaded chunk (see the 759,194 B dev sample the report measures), just fewer rows.</summary>
    internal static string EventsJson(int count)
    {
        var sb = new StringBuilder(count * 180);
        sb.Append('[');
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"t\":\"dmg\",\"ms\":").Append(1789049062050L + i * 37)
              .Append(",\"src\":\"54775054976\",\"tgt\":\"7179272256\",\"skill\":").Append(1262 + (i % 23))
              .Append(",\"amt\":").Append(51553 + i * 7)
              .Append(",\"act\":0,\"shield\":0,\"crit\":").Append(i % 3 == 0 ? "true" : "false")
              .Append(",\"lucky\":").Append(i % 5 == 0 ? "true" : "false")
              .Append(",\"heal\":false,\"dead\":false,\"elem\":").Append(i % 4)
              .Append(",\"kind\":0,\"source\":0}");
        }
        sb.Append(']');
        return sb.ToString();
    }

    internal static byte[] Inflate(byte[] br)
    {
        using var src = new System.IO.MemoryStream(br);
        using var dec = new BrotliStream(src, CompressionMode.Decompress);
        using var dst = new System.IO.MemoryStream();
        dec.CopyTo(dst);
        return dst.ToArray();
    }

    internal static (FakeDataStore Store, SpoolChunkRef[] Refs, string[] Envelopes) MakeTrack(string logId, params string[] eventsJsons)
    {
        var store = new FakeDataStore();
        var refs = new SpoolChunkRef[eventsJsons.Length];
        var envelopes = new string[eventsJsons.Length];
        for (var i = 0; i < eventsJsons.Length; i++)
        {
            var name = SpoolCodec.BlobName("seg", SpoolCodec.TrackDmg, i);
            store.Write(name, SpoolCodec.Gzip(eventsJsons[i]));
            refs[i] = new SpoolChunkRef(SpoolCodec.TrackDmg, i, 1000L + i, 2000L + i, 5 + i, name);
            envelopes[i] = ChunkUploader.BuildEnvelope(logId, refs[i], eventsJsons.Length, eventsJsons[i]);
        }
        return (store, refs, envelopes);
    }

    internal static async Task<(IReadOnlyList<Captured> Seen, List<string> Warnings, TimeSpan Elapsed)> PostTrackAsync(
        CapturingHandler handler, string logId, FakeDataStore store, IReadOnlyList<SpoolChunkRef> refs)
    {
        var warnings = new List<string>();
        var original = ChunkUploader.HttpClient;
        ChunkUploader.HttpClient = new HttpClient(handler);
        var sw = Stopwatch.StartNew();
        try
        {
            await ChunkUploader.PostRefsAsync(
                ChunkUploader.DmgEndpoint("https://x", "sea", 42, "chunk"),
                logId, refs, store,
                w => { lock (warnings) warnings.Add(w); }).ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            ChunkUploader.HttpClient = original;
        }
        return (handler.Seen, warnings, sw.Elapsed);
    }

    // (b1) --------------------------------------------------------------------------------------
    [Fact]
    public async Task Chunk_is_sent_brotli_as_application_json_and_inflates_to_the_exact_envelope()
    {
        ChunkCompressor.ResetForTests();
        try
        {
            var (store, refs, envelopes) = MakeTrack("log-br1", EventsJson(400));
            var handler = new CapturingHandler(HttpStatusCode.OK);

            var (seen, warnings, _) = await PostTrackAsync(handler, "log-br1", store, refs);

            // I2: the FIRST accepted br chunk logs the one positive line (and nothing else).
            var onLine = Assert.Single(warnings);
            Assert.StartsWith("[CombatMeter.SP1] chunk brotli ON: ", onLine, StringComparison.Ordinal);
            var sent = Assert.Single(seen);
            Assert.Equal("br", sent.ContentEncoding);
            Assert.Equal("application/json", sent.MediaType);

            var expected = Encoding.UTF8.GetBytes(envelopes[0]);
            Assert.True(sent.Body.Length < expected.Length / 8,
                $"brotli body {sent.Body.Length} B is not < 1/8 of the raw {expected.Length} B");
            Assert.Equal(expected, Inflate(sent.Body));
        }
        finally { ChunkCompressor.ResetForTests(); }
    }

    // (b2) --------------------------------------------------------------------------------------
    [Fact]
    public async Task Server_415_resends_that_chunk_plain_at_once_and_every_later_chunk_goes_plain()
    {
        ChunkCompressor.ResetForTests();
        try
        {
            var (store, refs, envelopes) = MakeTrack("log-br2", EventsJson(120), EventsJson(90));
            // chunk 0: 415 (br) -> plain resend 200; chunk 1: 200 first try, already plain.
            var handler = new CapturingHandler(HttpStatusCode.UnsupportedMediaType, HttpStatusCode.OK);

            var (seen, warnings, elapsed) = await PostTrackAsync(handler, "log-br2", store, refs);

            Assert.Equal(3, seen.Count);
            Assert.Equal("br", seen[0].ContentEncoding);

            // The compat resend is the SAME chunk, PLAIN, byte-for-byte the JSON the plain path sends.
            Assert.Null(seen[1].ContentEncoding);
            Assert.Equal("application/json", seen[1].MediaType);
            Assert.Equal(Encoding.UTF8.GetBytes(envelopes[0]), seen[1].Body);

            // ...and it is NOT one of the chunk's retries: no 1s backoff was burned.
            Assert.True(elapsed < TimeSpan.FromMilliseconds(900), $"415 resend waited {elapsed.TotalMilliseconds:0} ms — it must be immediate");

            // Brotli stays off for the rest of the process.
            Assert.Null(seen[2].ContentEncoding);
            Assert.Equal(Encoding.UTF8.GetBytes(envelopes[1]), seen[2].Body);
            Assert.False(ChunkCompressor.Enabled);

            var w = Assert.Single(warnings);
            Assert.Contains("415", w, StringComparison.Ordinal);
            Assert.Contains("[CombatMeter.SP1] chunk brotli", w, StringComparison.Ordinal);
        }
        finally { ChunkCompressor.ResetForTests(); }
    }

    // (b3) --------------------------------------------------------------------------------------
    [Fact]
    public async Task Encoder_fault_sends_plain_and_logs_exactly_one_line_per_process()
    {
        ChunkCompressor.ResetForTests();
        try
        {
            ChunkCompressor.Encoder = _ => throw new DllNotFoundException("System.IO.Compression.Native");
            var (store, refs, envelopes) = MakeTrack("log-br3", EventsJson(60), EventsJson(60));
            var handler = new CapturingHandler(HttpStatusCode.OK);

            var (seen, warnings, _) = await PostTrackAsync(handler, "log-br3", store, refs);

            Assert.Equal(2, seen.Count);
            Assert.All(seen, s => Assert.Null(s.ContentEncoding));
            Assert.Equal(Encoding.UTF8.GetBytes(envelopes[0]), seen[0].Body);
            Assert.Equal(Encoding.UTF8.GetBytes(envelopes[1]), seen[1].Body);

            var w = Assert.Single(warnings);
            Assert.Equal("[CombatMeter.SP1] chunk brotli unavailable (DllNotFoundException): sending plain", w);
            Assert.False(ChunkCompressor.Enabled);
        }
        finally { ChunkCompressor.ResetForTests(); }
    }

    // (b4) --------------------------------------------------------------------------------------
    [Fact]
    public void Brotli_q9_w22_round_trips_and_shrinks_a_realistic_chunk_by_more_than_8x()
    {
        ChunkCompressor.ResetForTests();
        try
        {
            Assert.Equal(9, ChunkCompressor.Quality);
            Assert.Equal(22, ChunkCompressor.Window);

            var raw = Encoding.UTF8.GetBytes(ChunkUploader.BuildEnvelope(
                "cm-20260922-ratio", new SpoolChunkRef(SpoolCodec.TrackDmg, 0, 1L, 2L, 4000, "spool/x-dmg-000.gz"),
                1, EventsJson(4000)));

            var br = ChunkCompressor.Compress(raw);

            Assert.Equal(raw, Inflate(br));
            Assert.True(raw.Length > br.Length * 8, $"ratio only {(double)raw.Length / br.Length:0.0}x (raw {raw.Length} B, br {br.Length} B)");
        }
        finally { ChunkCompressor.ResetForTests(); }
    }

    // (b5) --------------------------------------------------------------------------------------
    [Fact]
    public async Task A_disabled_compressor_sends_exactly_what_the_plain_path_sent_before_this_change()
    {
        ChunkCompressor.ResetForTests();
        try
        {
            ChunkCompressor.Encoder = _ => throw new NotSupportedException("off");
            var (store, refs, envelopes) = MakeTrack("log-br5", EventsJson(30));
            var handler = new CapturingHandler(HttpStatusCode.OK);

            var (seen, _, _) = await PostTrackAsync(handler, "log-br5", store, refs);

            var sent = Assert.Single(seen);
            using var reference = new StringContent(envelopes[0], Encoding.UTF8, "application/json");
            Assert.Equal("application/json; charset=utf-8", reference.Headers.ContentType!.ToString());
            Assert.Equal(await reference.ReadAsByteArrayAsync(), sent.Body);
            // m1: closes the byte chain b1 leaves open — StringContent's bytes ARE UTF8.GetBytes (no BOM),
            // so b1's "inflated == UTF8.GetBytes(envelope)" and this pin together cover the whole path.
            Assert.Equal(Encoding.UTF8.GetBytes(envelopes[0]), sent.Body);
            Assert.Equal("application/json", sent.MediaType);
            Assert.Null(sent.ContentEncoding);
        }
        finally { ChunkCompressor.ResetForTests(); }
    }
}

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
