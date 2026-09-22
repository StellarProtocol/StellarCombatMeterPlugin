// Task 1 (CombatMeter upload resilience, "chunk-loss fix 2"; design of record:
// docs/superpowers/specs/2026-09-20-combatmeter-upload-resilience-design.md §§ 1-3). Header-retry pins
// h1-h5, plus the 5xx/409 halves of the same retry policy (h6-h8, review follow-up). Every prior upload
// test in this suite is pure-data (URL building, UploadVerdict.Parse, retry-DELAY-VALUE pinning — see
// LogUploadTests.cs's own header comment and PositionUploaderRetryTests, which pins ONLY
// PositionUploader.NextRetryDelay/RetryDelays, never an actual HttpMessageHandler). ScriptedHandler
// below is therefore new machinery for this suite, not a mirror of an existing fake-handler test.
//
// LogUploader.HttpClient and LogUploader.DelayFunc are swapped for the duration of each test (restored
// in `finally`) and serialized via LogUploaderHttpCollection (DisableParallelization) — the same pattern
// UploadApiBaseTests/ApiBaseCollection uses for LogUploader.ApiBase. DelayFunc lets h1/h3 exercise the
// REAL 3-attempt retry loop without blocking on the real 5s/15s backoff; LogUploader.RetryDelays itself
// (the production VALUES, h5) is never mutated by these tests.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

[Collection(LogUploaderHttpCollection.Name)]
public sealed class LogUploaderRetryTests
{
    private static CombatLog MakeLog(long levelUuid = 42) => new(
        1,
        new LogHeader("cm-retry-test", 2000L, "2.11", "SEA", "1.9.0", "1.1.0", "unlisted",
            new Encounter("dungeon", levelUuid, null, 100, 0, null, 0, null, null, 0, "kill", 1000L, 2000L, 1000L, 0),
            new Uploader(42L, "sig", "nonce")),
        new Dictionary<string, Actor>(),
        Array.Empty<CombatLogEvent>());

    // Pops one scripted step per SendAsync call: either throws (simulating a transport failure/timeout)
    // or returns a canned response. Runs out of steps -> throws (a test that under-scripts is a bug in
    // the test, not a silent pass).
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<Task<HttpResponseMessage>>> _steps;
        public int Calls { get; private set; }

        public ScriptedHandler(params Func<Task<HttpResponseMessage>>[] steps)
            => _steps = new Queue<Func<Task<HttpResponseMessage>>>(steps);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (_steps.Count == 0) throw new InvalidOperationException("ScriptedHandler ran out of steps");
            return _steps.Dequeue()();
        }
    }

    private static Func<Task<HttpResponseMessage>> Throws(string message = "boom")
        => () => throw new HttpRequestException(message);

    private static Func<Task<HttpResponseMessage>> Returns(HttpStatusCode status, string body = "")
        => () => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });

    // Points LogUploader at `handler` and bypasses the real backoff wait for the duration of `run`,
    // restoring both statics afterward — the shared-state reason this whole class shares one collection.
    private static void WithFakeTransport(ScriptedHandler handler, Action run)
    {
        var originalClient = LogUploader.HttpClient;
        var originalDelay = LogUploader.DelayFunc;
        LogUploader.HttpClient = new HttpClient(handler);
        LogUploader.DelayFunc = _ => Task.CompletedTask;
        try { run(); }
        finally
        {
            LogUploader.HttpClient = originalClient;
            LogUploader.DelayFunc = originalDelay;
        }
    }

    // (h5) the header-retry backoff — its OWN ladder, distinct from ChunkUploader/PositionUploader's 1s/3s.
    [Fact]
    public void RetryDelays_is_5s_then_15s()
        => Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) }, LogUploader.RetryDelays);

    // (h1) two transient transport failures then a 200 land the upload on the 3rd attempt.
    [Fact]
    public void Transport_exception_twice_then_200_lands_on_third_attempt()
    {
        var handler = new ScriptedHandler(Throws(), Throws(), Returns(HttpStatusCode.OK, "{\"ok\":true,\"kept\":true}"));
        (bool ok, int status, string? err, UploadVerdict? verdict, int attempt)? result = null;
        var done = new ManualResetEventSlim();

        WithFakeTransport(handler, () =>
        {
            LogUploader.UploadFireAndForget(MakeLog(),
                (ok, status, err, verdict, attempt) => { result = (ok, status, err, verdict, attempt); done.Set(); },
                delayMs: 0, skipPrecheck: true);
            Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "upload never completed");
        });

        Assert.Equal(3, handler.Calls);
        Assert.True(result!.Value.ok);
        Assert.Equal(200, result.Value.status);
        Assert.Equal(3, result.Value.attempt);
    }

    // (h2) a 400 is a permanent verdict — exactly one attempt, no retry.
    [Fact]
    public void ClientError_400_is_not_retried()
    {
        var handler = new ScriptedHandler(Returns(HttpStatusCode.BadRequest, "bad body"));
        (bool ok, int status, string? err, UploadVerdict? verdict)? result = null;
        var done = new ManualResetEventSlim();

        WithFakeTransport(handler, () =>
        {
            LogUploader.UploadFireAndForget(MakeLog(),
                (ok, status, err, verdict) => { result = (ok, status, err, verdict); done.Set(); },
                delayMs: 0, skipPrecheck: true);
            Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "upload never completed");
        });

        Assert.Equal(1, handler.Calls);
        Assert.False(result!.Value.ok);
        Assert.Equal(400, result.Value.status);
        Assert.Equal("bad body", result.Value.err);
        Assert.Null(result.Value.verdict);
    }

    // (h3) exhausting all 3 attempts on transport failures gives up honestly: status 0 (no HTTP response
    // was ever received), the last exception's message, no chunks (nothing to send — the callback never
    // fires with ok=true, so the production call site's chunk-send branch never runs).
    [Fact]
    public void Three_transport_exceptions_exhaust_retries_and_report_status_zero()
    {
        var handler = new ScriptedHandler(Throws("boom1"), Throws("boom2"), Throws("boom3"));
        (bool ok, int status, string? err, UploadVerdict? verdict)? result = null;
        var done = new ManualResetEventSlim();

        WithFakeTransport(handler, () =>
        {
            LogUploader.UploadFireAndForget(MakeLog(),
                (ok, status, err, verdict) => { result = (ok, status, err, verdict); done.Set(); },
                delayMs: 0, skipPrecheck: true);
            Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "upload never completed");
        });

        Assert.Equal(3, handler.Calls);
        Assert.False(result!.Value.ok);
        Assert.Equal(0, result.Value.status);
        Assert.Equal("boom3", result.Value.err);
        Assert.Null(result.Value.verdict);
    }

    // (h4) a fix-1-shaped 202 (kept:true, no shortUrl) parses correctly, and PreferredUrl falls back to
    // the constructed URL when the response carried none.
    [Fact]
    public void Accepted202_fix1Shape_parses_kept_true_no_shortUrl()
    {
        const string body = "{\"ok\":true,\"accepted\":true,\"kept\":true,\"deduped\":false,\"havePositions\":false,\"runUrl\":\"/api/run/sea/1\"}";
        var handler = new ScriptedHandler(Returns(HttpStatusCode.Accepted, body));
        UploadVerdict? verdict = null;
        var done = new ManualResetEventSlim();

        WithFakeTransport(handler, () =>
        {
            LogUploader.UploadFireAndForget(MakeLog(),
                (ok, status, err, v) => { verdict = v; done.Set(); },
                delayMs: 0, skipPrecheck: true);
            Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "upload never completed");
        });

        Assert.NotNull(verdict);
        Assert.True(verdict!.Kept);
        Assert.False(verdict.HavePositions);
        Assert.Equal("https://ctor/run/sea/1", UploadVerdict.PreferredUrl(verdict, "https://ctor/run/sea/1"));
    }

    // (h6) the 5xx half of the same retry policy TrySendAsync/UploadAsync applies to a transport
    // exception (h1): two 5xx responses then a 200 land the upload on the 3rd attempt. Mutation-tested
    // (review): temporarily narrowing LogUploader.cs's `status >= 500` retry gate to `status >= 502`
    // makes this fail — a 500 no longer retries, so the 3rd-attempt 200 is never reached — see the Fix
    // report for the exact failure line.
    [Fact]
    public void Http500_then_503_then_200_lands_on_third_attempt()
    {
        var handler = new ScriptedHandler(
            Returns(HttpStatusCode.InternalServerError, "server error 1"),
            Returns(HttpStatusCode.ServiceUnavailable, "server error 2"),
            Returns(HttpStatusCode.OK, "{\"ok\":true,\"kept\":true}"));
        (bool ok, int status, string? err, UploadVerdict? verdict, int attempt)? result = null;
        var done = new ManualResetEventSlim();

        WithFakeTransport(handler, () =>
        {
            LogUploader.UploadFireAndForget(MakeLog(),
                (ok, status, err, verdict, attempt) => { result = (ok, status, err, verdict, attempt); done.Set(); },
                delayMs: 0, skipPrecheck: true);
            Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "upload never completed");
        });

        Assert.Equal(3, handler.Calls);
        Assert.True(result!.Value.ok);
        Assert.Equal(200, result.Value.status);
        Assert.Equal(3, result.Value.attempt);
    }

    // (h7) a 409 ("server already has this run") is a PERMANENT verdict, never retried — exactly one
    // header POST, resolved as a success. MakeLog()'s Actors dict is empty (no local actor), so
    // SupplementPolicy.ShouldSendSupplement is false and HandleAlreadyUploadedAsync never fires the
    // supplement POST — the single ScriptedHandler (shared by both the header and supplement URLs, same
    // LogUploader.HttpClient) staying at Calls==1 is itself proof the supplement leg never ran.
    [Fact]
    public void Http409_is_not_retried_and_resolves_as_success()
    {
        var handler = new ScriptedHandler(Returns(HttpStatusCode.Conflict, "{\"ok\":true,\"kept\":true}"));
        (bool ok, int status, string? err, UploadVerdict? verdict, int attempt)? result = null;
        var done = new ManualResetEventSlim();

        WithFakeTransport(handler, () =>
        {
            LogUploader.UploadFireAndForget(MakeLog(),
                (ok, status, err, verdict, attempt) => { result = (ok, status, err, verdict, attempt); done.Set(); },
                delayMs: 0, skipPrecheck: true);
            Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "upload never completed");
        });

        Assert.Equal(1, handler.Calls);
        Assert.True(result!.Value.ok);
        Assert.Equal(409, result.Value.status);
        Assert.Equal(1, result.Value.attempt);
        Assert.NotNull(result.Value.verdict);
        // UploadVerdict.From409 FORCES Kept=false (the 409 body carries no `kept` field for Parse to
        // read — see UploadVerdict.cs's doc comment); this run is server-covered regardless.
        Assert.False(result.Value.verdict!.Kept);
    }

    // (h8) a 5xx on ALL three attempts exhausts the retry ladder and gives up honestly with the last
    // server status (not status 0 — a response WAS received every time, unlike h3's transport failures).
    [Fact]
    public void Three_5xx_responses_exhaust_retries_and_report_last_status()
    {
        var handler = new ScriptedHandler(
            Returns(HttpStatusCode.InternalServerError, "server error 1"),
            Returns(HttpStatusCode.BadGateway, "server error 2"),
            Returns(HttpStatusCode.ServiceUnavailable, "server error 3"));
        (bool ok, int status, string? err, UploadVerdict? verdict, int attempt)? result = null;
        var done = new ManualResetEventSlim();

        WithFakeTransport(handler, () =>
        {
            LogUploader.UploadFireAndForget(MakeLog(),
                (ok, status, err, verdict, attempt) => { result = (ok, status, err, verdict, attempt); done.Set(); },
                delayMs: 0, skipPrecheck: true);
            Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "upload never completed");
        });

        Assert.Equal(3, handler.Calls);
        Assert.False(result!.Value.ok);
        Assert.Equal(503, result.Value.status);
        Assert.Equal("server error 3", result.Value.err);
        Assert.Null(result.Value.verdict);
        Assert.Equal(3, result.Value.attempt);
    }
}

/// <summary>Serializes tests that swap LogUploader.HttpClient/DelayFunc — same DisableParallelization
/// pattern as ApiBaseCollection (UploadApiBaseTests) so no other test's upload can race the swap.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LogUploaderHttpCollection
{
    public const string Name = "LogUploader.HttpClient";
}
