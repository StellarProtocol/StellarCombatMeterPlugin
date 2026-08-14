using System;
using Stellar.CombatMeter.LogUpload;
using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Parity pin (fix 2026-08-14): the positions uploader retries with the SAME bounded transport
/// policy the chunk uploader has always had — 3 attempts total, 1s then 3s backoff. Positions used
/// to get exactly ONE attempt (no retry), so a transient network blip permanently lost a banked
/// replay window while the summary/chunk uploads beside it retried and landed. NEVER weaken these:
/// the policy must stay BOUNDED (no unbounded retry loops — hard rule) and must not regress to
/// one-shot.
/// </summary>
public class PositionUploaderRetryTests
{
    [Fact]
    public void Retry_sequence_is_1s_then_3s_then_exhausted()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), PositionUploader.NextRetryDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(3), PositionUploader.NextRetryDelay(1));
        Assert.Null(PositionUploader.NextRetryDelay(2));    // bounded: 3 attempts total, then give up
        Assert.Null(PositionUploader.NextRetryDelay(99));   // an exhausted policy never resurrects
    }

    [Fact]
    public void Policy_matches_ChunkUploader_exactly()
        => Assert.Equal(ChunkUploader.RetryDelays, PositionUploader.RetryDelays);
}
