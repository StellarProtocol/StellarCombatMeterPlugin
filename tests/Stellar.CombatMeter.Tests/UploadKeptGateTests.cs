// (c1) chunk-loss fix 2 (docs/superpowers/specs/2026-09-20-combatmeter-upload-resilience-design.md
// § 1(b)): "upload all — never skip chunks on the merge verdict" (owner rule 2026-08-25, commit
// c4988960). That fix already shipped in production code — OnSummaryUploadOk (Plugin.LogUpload.cs)
// has carried no `v.Kept` gate since 2026-08-26 — but it never got a pinned regression test, so a
// future "optimization" could silently reintroduce the skip with nothing to catch it. The brief for
// this task asked to "invert today's skip pin"; no such pin exists in this suite (grepped — the 2026-
// 07-11 commit that introduced the skip, and the 2026-08-26 commit that removed it, both touched zero
// test files). This file closes that gap instead of inverting a pin that was never written.
//
// OnSummaryUploadOk itself is a private Plugin instance method needing a live IPluginServices — this
// suite deliberately never constructs one (see LogUploadTests.cs's header + UploadTargetTests.cs's own
// note). So the exact decision it makes — "send chunks iff the segment has any, verdict irrelevant" —
// is extracted to Plugin.ShouldSendChunks (a pure static predicate, same pattern as
// ShouldRetainUnsentArchive/PhaseFromResult in this same file), which IS what OnSummaryUploadOk calls.

using Stellar.CombatMeter.LogUpload;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public sealed class UploadKeptGateTests
{
    // (c1) A merge-LOSER's chunks (kept:false) still upload — cites spec § 1(b) above.
    [Fact]
    public void ShouldSendChunks_sends_even_when_verdict_kept_is_false()
    {
        var keptFalse = new UploadVerdict(Kept: false, HavePositions: false);
        Assert.True(Plugin.ShouldSendChunks(chunkCount: 1, keptFalse));
    }

    [Fact]
    public void ShouldSendChunks_sends_when_verdict_kept_is_true_too()
    {
        var keptTrue = new UploadVerdict(Kept: true, HavePositions: false);
        Assert.True(Plugin.ShouldSendChunks(chunkCount: 1, keptTrue));
    }

    [Fact]
    public void ShouldSendChunks_sends_even_with_a_null_verdict()
        => Assert.True(Plugin.ShouldSendChunks(chunkCount: 1, verdict: null));

    // The only thing that actually gates the send is whether there is anything TO send.
    [Fact]
    public void ShouldSendChunks_false_when_no_chunks_regardless_of_verdict()
    {
        Assert.False(Plugin.ShouldSendChunks(chunkCount: 0, new UploadVerdict(Kept: true, HavePositions: false)));
        Assert.False(Plugin.ShouldSendChunks(chunkCount: 0, new UploadVerdict(Kept: false, HavePositions: false)));
    }
}
