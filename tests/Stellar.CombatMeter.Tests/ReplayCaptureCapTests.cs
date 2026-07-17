using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.Replay;
using Xunit;

namespace Stellar.CombatMeter.Tests;

public class ReplayCaptureCapTests
{
    private static ReplayCapture Cap()
        => new(
            tryGet: (EntityId id, out Position3D p, out float yaw) => { p = default; yaw = 0f; return false; },
            maxSamplesPerTrack: 16, maxTotalSamples: 1000, sampleIntervalMs: 500);

    [Fact]
    public void NoteEntity_refuses_new_tracks_beyond_MaxTracks()
    {
        var c = Cap();
        for (var i = 1; i <= ReplayCapture.MaxTracks + 10; i++) c.NoteEntity(new EntityId(i));
        Assert.Equal(ReplayCapture.MaxTracks, c.Tracks.Count);
        Assert.True(c.TrackCapHit);
    }

    [Fact]
    public void NoteEntity_below_cap_does_not_set_TrackCapHit()
    {
        var c = Cap();
        c.NoteEntity(new EntityId(1));
        Assert.False(c.TrackCapHit);
    }

    [Fact]
    public void Reset_clears_cap_state()
    {
        var c = Cap();
        for (var i = 1; i <= ReplayCapture.MaxTracks + 1; i++) c.NoteEntity(new EntityId(i));
        c.Reset();
        Assert.False(c.TrackCapHit);
        Assert.Empty(c.Tracks);
        c.NoteEntity(new EntityId(1));
        Assert.Single(c.Tracks);
    }
}
