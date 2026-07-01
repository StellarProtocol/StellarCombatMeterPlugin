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
    private readonly TryGetTransform _tryGet;
    private readonly int _maxSamplesPerTrack, _maxTotalSamples, _sampleIntervalMs;
    private readonly Dictionary<EntityId, PositionTrack> _tracks = new();
    private readonly List<EntityId> _order = new();          // stable iteration; reused
    private float _accumMs;
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

    public void NoteEntity(EntityId id)
    {
        if (id.Value == 0) return;
        if (!_tracks.ContainsKey(id)) { _tracks[id] = new PositionTrack(_maxSamplesPerTrack); _order.Add(id); }
    }

    public void Tick(int nowMs, float dtMs)
    {
        if (!Active || TotalSamples >= _maxTotalSamples) return;
        if (!_startStamped) { _combatStartMs = nowMs; _startStamped = true; }
        _accumMs += dtMs;
        if (_accumMs < _sampleIntervalMs) return;
        _accumMs = 0f;
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
            _tracks[id].Add(new PositionSample(rel, p.X, p.Y, p.Z, yaw));
            TotalSamples++;
        }
    }

    public void Reset()
    {
        _tracks.Clear();
        _order.Clear();
        _accumMs = 0f;
        TotalSamples = 0;
        _startStamped = false;
        _combatStartMs = 0;
    }
}
