using System.Collections.Generic;
using Stellar.CombatMeter;
using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

// No tiny replay fragments (owner ruling 2026-07-19). Now that short kill-tails SAVE (a kill-carrying
// tail is run-terminal), a ~1.4s positions doc would be uploaded — and tiny replay fragments are
// EXACTLY what broke multi-segment site rendering on 2026-07-19. The HISTORY side no longer has a
// span floor, but the REPLAY side keeps one: only assembled position docs covering at least
// MinReplayUploadMs of wall-clock get uploaded. The damage segment + settlement upload regardless;
// the site tolerates a segment without a linked recording ("play what's present").
public class ReplayFragmentGateTests
{
    // Build a doc at the given Hz with one track per (ms0, sampleCount) entry. The gate only reads
    // Hz + per-track Ms0 + sample count (Dx.Length), so the array contents are irrelevant here.
    private static PositionUploadDoc MakeDoc(int hz, params (long ms0, int samples)[] tracks)
    {
        var dto = new Dictionary<string, PositionTrackDto>();
        var i = 0;
        foreach (var (ms0, samples) in tracks)
            dto[(i++).ToString()] = new PositionTrackDto(
                (int)ms0, new int[samples], new int[samples], new int[samples], new int[samples]);
        return new PositionUploadDoc(hz, 0, (0f, 0f), 0.1f, dto,
            new Dictionary<string, PositionMetaDto>());
    }

    // ── captured-span computation ──────────────────────────────────────────────────────────────

    [Fact]
    public void CapturedSpan_of_a_short_tail_is_below_the_floor()
    {
        // A ~1.4s tail at 2 Hz = 3 samples → (3-1) * 500ms = 1000ms.
        var doc = MakeDoc(2, (0, 3));
        Assert.Equal(1_000, Plugin.ReplayCapturedSpanMs(doc));
    }

    [Fact]
    public void CapturedSpan_of_a_real_run_is_well_above_the_floor()
    {
        // 61 samples at 2 Hz → 30_000ms.
        var doc = MakeDoc(2, (0, 61));
        Assert.Equal(30_000, Plugin.ReplayCapturedSpanMs(doc));
    }

    [Fact]
    public void CapturedSpan_spans_earliest_start_to_latest_end_across_tracks()
    {
        // Track A: ms0=0, 5 samples → ends at 2000. Track B: ms0=1000, 9 samples → ends at 5000.
        // Window = 5000 - 0 = 5000.
        var doc = MakeDoc(2, (0, 5), (1000, 9));
        Assert.Equal(5_000, Plugin.ReplayCapturedSpanMs(doc));
    }

    [Fact]
    public void CapturedSpan_of_empty_doc_is_zero()
        => Assert.Equal(0, Plugin.ReplayCapturedSpanMs(MakeDoc(2)));

    // ── upload gate ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Short_fragment_is_not_uploaded()
        => Assert.False(Plugin.ShouldUploadReplay(MakeDoc(2, (0, 3))));   // 1000ms < 3000

    [Fact]
    public void Real_run_is_uploaded()
        => Assert.True(Plugin.ShouldUploadReplay(MakeDoc(2, (0, 61))));   // 30_000ms >= 3000

    [Fact]
    public void Just_below_floor_is_not_uploaded()
    {
        // 6 samples at 2 Hz → 2500ms < 3000.
        Assert.False(Plugin.ShouldUploadReplay(MakeDoc(2, (0, 6))));
    }

    [Fact]
    public void At_floor_is_uploaded()
    {
        // 7 samples at 2 Hz → 3000ms == MinReplayUploadMs.
        Assert.Equal(Plugin.MinReplayUploadMs, Plugin.ReplayCapturedSpanMs(MakeDoc(2, (0, 7))));
        Assert.True(Plugin.ShouldUploadReplay(MakeDoc(2, (0, 7))));
    }

    [Fact]
    public void Empty_doc_is_not_uploaded()
        => Assert.False(Plugin.ShouldUploadReplay(MakeDoc(2)));
}
