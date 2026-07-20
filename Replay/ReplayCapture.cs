using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.Replay;

/// <summary>Reads a transform by id on the main thread. Matches IEntityTransforms.TryGetTransform.</summary>
internal delegate bool TryGetTransform(EntityId id, out Position3D position, out float yawDegrees);

/// <summary>
/// Samples tracked combat entities at a fixed interval into bounded per-entity tracks.
/// Entity set is fed via NoteEntity from the combat stream. Sampling only runs while Active
/// (set by the plugin: dungeon/raid + toggle-on + combat-active). Main-thread only; the steady-state
/// Tick path allocates nothing (dictionary reuse, struct samples, no LINQ).
/// </summary>
internal sealed class ReplayCapture
{
    /// <summary>Hard cap on distinct tracked entities per encounter. Real instanced runs
    /// see far fewer; the cap is a backstop against unexpected entity-id churn.</summary>
    internal const int MaxTracks = 512;

    private readonly TryGetTransform _tryGet;
    private readonly int _maxSamplesPerTrack, _maxTotalSamples, _sampleIntervalMs;
    private readonly Dictionary<EntityId, PositionTrack> _tracks = new();
    private readonly List<EntityId> _order = new();          // stable iteration; reused
    private float _accumMs;

    // Per-encounter timestamps. _combatStartMs is stamped on the first Tick after Reset() and is
    // INTENTIONALLY preserved across Active on/off toggles within one encounter (combat lulls) so
    // the replay timeline is continuous. Reset() (called per-encounter at archive) re-arms the stamp.
    // This is deliberate, NOT a bug.
    private int _combatStartMs;
    private bool _startStamped;

    public ReplayCapture(TryGetTransform tryGet, int maxSamplesPerTrack, int maxTotalSamples, int sampleIntervalMs)
    {
        _tryGet = tryGet;
        _maxSamplesPerTrack = maxSamplesPerTrack;
        _maxTotalSamples = maxTotalSamples;
        _sampleIntervalMs = sampleIntervalMs;
    }

    public bool Active { get; set; }
    public int TotalSamples { get; private set; }
    public IReadOnlyDictionary<EntityId, PositionTrack> Tracks => _tracks;
    public int CombatStartMs => _combatStartMs;

    /// <summary>True once <see cref="NoteEntity"/> refused an id because <see cref="MaxTracks"/>
    /// was reached. Cleared by <see cref="Reset"/>.</summary>
    public bool TrackCapHit { get; private set; }

    public void NoteEntity(EntityId id)
    {
        if (id.Value == 0) return;
        if (_tracks.ContainsKey(id)) return;
        if (_tracks.Count >= MaxTracks) { TrackCapHit = true; return; }
        _tracks[id] = new PositionTrack(_maxSamplesPerTrack);
        _order.Add(id);
    }

    public void Tick(int nowMs, float dtMs)
    {
        if (!Active || TotalSamples >= _maxTotalSamples) return;
        if (!_startStamped) { _combatStartMs = nowMs; _startStamped = true; }
        _accumMs += dtMs;
        if (_accumMs < _sampleIntervalMs) return;
        _accumMs -= _sampleIntervalMs;
        // If a very large stall built up more than one interval of backlog, drop it — we can't
        // retroactively sample past positions, so carry only a sub-interval remainder forward.
        if (_accumMs >= _sampleIntervalMs) _accumMs = 0f;
        SampleAllEntities(nowMs);
    }

    private void SampleAllEntities(int nowMs)
    {
        var rel = nowMs - _combatStartMs;
        if (rel < 0) rel = 0;
        for (var i = 0; i < _order.Count; i++)
        {
            if (TotalSamples >= _maxTotalSamples) break;
            var id = _order[i];
            if (!_tryGet(id, out var p, out var yaw)) continue;
            // Lobby / walk-in phase: the live model exists (liveness gate passed) but AttrGoPosition
            // has not streamed, so the probe resolves TRUE with the Position3D.Zero default. Treat that
            // as a failed probe — drop it exactly like a false return — so the run is never anchored at
            // the map origin. Sampling only runs while Active, i.e. inside an instanced/candidate scene
            // (ReplayCaptureGate.ShouldCapture), so this guard is scoped to instanced-run captures.
            // Regression: run sea/UaU5VejCA0 — docs/recon/thanatos-walkin-geo.md.
            if (IsUnstreamedZeroTransform(p.X, p.Y, p.Z)) continue;
            _tracks[id].Add(new PositionSample(rel, p.X, p.Y, p.Z, yaw));
            TotalSamples++;
        }
    }

    /// <summary>Frees every track's samples with <c>Ms &lt;= ms</c> (an uploaded window's footage) and
    /// keeps <see cref="TotalSamples"/> accurate so the <c>maxTotalSamples</c> cap still accounts only
    /// for retained samples. Called at archive time when the watermark advances — the delta-window
    /// replacement for the old whole-buffer <see cref="Reset"/> at every archive. Track objects (and
    /// their entity ids / stable order) survive so sampling continues seamlessly into the next window.</summary>
    public void TrimBelow(long ms)
    {
        var freed = 0;
        foreach (var track in _tracks.Values) freed += track.TrimBelow(ms);
        TotalSamples -= freed;
        if (TotalSamples < 0) TotalSamples = 0;
    }

    /// <summary>Sub-metre tolerance for the horizontal (X/Z) plane in <see cref="IsUnstreamedZeroTransform"/>.
    /// Positions are metres, so 0.5 is well below any meaningful movement at the 2 Hz sample cadence.</summary>
    internal const float ZeroPlaneEpsilon = 0.5f;

    /// <summary>
    /// True when a RESOLVED transform is the uninitialised <see cref="Position3D.Zero"/> default the live
    /// probe returns before <c>AttrGoPosition</c> has streamed (raid lobby / dungeon walk-in of an instanced
    /// run — regression run sea/UaU5VejCA0, docs/recon/thanatos-walkin-geo.md). Such a sample is dropped
    /// exactly like a failed probe so the run is not anchored at the world origin.
    /// <para>
    /// The discriminator is the VERTICAL axis. The georef table (site names.generated.json <c>sceneMaps</c>)
    /// shows (X,Z)=(0,0) is a legitimate INTERIOR position in 518/609 instanced maps — many centre on the
    /// world origin — so an X/Z-only test would wrongly drop real map-centre positions. A real instanced
    /// floor is elevated (in that run the boss on the SAME floor in the SAME tick read Y=100.2), while the
    /// default sentinel is Y bit-exactly <c>0f</c>. Requiring <c>Y == 0f</c> exactly (plus X/Z within a
    /// metre of the origin) separates the default from any genuine near-origin standing position, which
    /// still carries its real, non-zero floor Y. Pure; unit-tested.
    /// </para>
    /// </summary>
    internal static bool IsUnstreamedZeroTransform(float x, float y, float z)
        => y == 0f && MathF.Abs(x) < ZeroPlaneEpsilon && MathF.Abs(z) < ZeroPlaneEpsilon;

    public void Reset()
    {
        _tracks.Clear();
        _order.Clear();
        _accumMs = 0f;
        TotalSamples = 0;
        _startStamped = false;
        _combatStartMs = 0;
        TrackCapHit = false;
    }
}
