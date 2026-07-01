using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class ReplayCaptureTests
{
    private static ReplayCapture Make(Dictionary<EntityId, Position3D> world, out List<EntityId> reads)
    {
        var r = new List<EntityId>();
        reads = r;
        var localReads = r;
        return new ReplayCapture(
            tryGet: (EntityId id, out Position3D p, out float yaw) =>
            {
                localReads.Add(id);
                yaw = 0f;
                if (world.TryGetValue(id, out p)) return true;
                p = Position3D.Zero; return false;
            },
            maxSamplesPerTrack: 3600, maxTotalSamples: 200_000, sampleIntervalMs: 500);
    }

    [Fact]
    public void Tick_Inactive_DoesNotSample()
    {
        var cap = Make(new(), out _);
        cap.NoteEntity(new EntityId(1));
        cap.Tick(nowMs: 0, dtMs: 500);
        Assert.Equal(0, cap.TotalSamples);
    }

    [Fact]
    public void Tick_Active_SamplesTrackedEntitiesAtInterval()
    {
        var world = new Dictionary<EntityId, Position3D> { [new EntityId(1)] = new(1, 2, 3) };
        var cap = Make(world, out _);
        cap.Active = true;
        cap.NoteEntity(new EntityId(1));
        cap.Tick(0, 500);     // first sample
        cap.Tick(0, 250);     // < interval accumulated -> no new sample
        cap.Tick(0, 250);     // now 500 accumulated -> second sample
        Assert.Equal(2, cap.TotalSamples);
    }

    [Fact]
    public void UnresolvableEntity_IsSkippedThatTick_ProducesNoSample()
    {
        var cap = Make(new(), out var reads);   // world empty -> tryGet returns false
        cap.Active = true;
        cap.NoteEntity(new EntityId(9));
        cap.Tick(0, 500);
        Assert.Equal(0, cap.TotalSamples);
        Assert.Contains(new EntityId(9), reads);
    }

    [Fact]
    public void GlobalBudget_StopsSampling()
    {
        var world = new Dictionary<EntityId, Position3D> { [new EntityId(1)] = new(1, 1, 1) };
        var cap = new ReplayCapture(
            (EntityId id, out Position3D p, out float y) => { y = 0; return world.TryGetValue(id, out p); },
            maxSamplesPerTrack: 3600, maxTotalSamples: 3, sampleIntervalMs: 500) { Active = true };
        cap.NoteEntity(new EntityId(1));
        for (var i = 0; i < 10; i++) cap.Tick(0, 500);
        Assert.Equal(3, cap.TotalSamples);
    }

    [Fact]
    public void AccumulatorCarry_SubIntervalRemainderRollsForward()
    {
        // With sampleIntervalMs=500:
        // Tick(dtMs=750): accum 0+750=750 >= 500 → fires sample 1; accum = 750-500 = 250 (carry).
        // Tick(dtMs=250): accum 250+250=500 >= 500 → fires sample 2; accum = 500-500 = 0.
        // Old reset-to-0 behaviour would have left 0 after the first tick and produced only 1 sample.
        var world = new Dictionary<EntityId, Position3D> { [new EntityId(1)] = new(1, 1, 1) };
        var cap = Make(world, out _);
        cap.Active = true;
        cap.NoteEntity(new EntityId(1));

        cap.Tick(nowMs: 0, dtMs: 750);   // fires sample 1; carries 250ms remainder
        Assert.Equal(1, cap.TotalSamples);

        cap.Tick(nowMs: 750, dtMs: 250);  // 250 remainder + 250 = 500 → fires sample 2
        Assert.Equal(2, cap.TotalSamples);
    }
}
