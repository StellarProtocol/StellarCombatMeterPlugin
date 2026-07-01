using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.Replay;

/// <summary>
/// Converts raw <see cref="PositionTrack"/> samples into a <see cref="PositionUploadDoc"/>
/// ready for serialization. Quantizes world coordinates via <see cref="PositionCodec"/> and
/// delta-encodes the resulting integer arrays. Pure managed; no allocation on the hot path
/// (all work happens once at upload time, off the game thread).
/// </summary>
internal static class PositionTrackAssembler
{
    private const float DefaultScale = 0.1f;

    /// <summary>
    /// Assembles a <see cref="PositionUploadDoc"/> from the captured tracks.
    /// </summary>
    /// <param name="tracks">Per-entity raw sample buffers from <see cref="ReplayCapture"/>.</param>
    /// <param name="hz">Sample rate in samples-per-second (e.g. 2).</param>
    /// <param name="mapId">Game map/zone identifier.</param>
    /// <param name="origin">World-space origin (X, Z) of the quantization grid.</param>
    /// <param name="scale">Quantization cell size (meters). Typically 0.1.</param>
    /// <param name="meta">Optional per-entity metadata (kind, name, professionId).</param>
    public static PositionUploadDoc Assemble(
        IReadOnlyDictionary<EntityId, PositionTrack> tracks,
        int hz,
        int mapId,
        (float X, float Z) origin,
        float scale,
        IReadOnlyDictionary<EntityId, PositionMetaDto>? meta = null)
    {
        var dtoTracks = BuildTracks(tracks, origin, scale);
        var dtoMeta = BuildMeta(meta);
        return new PositionUploadDoc(hz, mapId, origin, scale, dtoTracks, dtoMeta);
    }

    private static IReadOnlyDictionary<string, PositionTrackDto> BuildTracks(
        IReadOnlyDictionary<EntityId, PositionTrack> tracks,
        (float X, float Z) origin,
        float scale)
    {
        // Sort keys ascending by numeric entity id for deterministic output.
        var sorted = tracks.Keys
            .OrderBy(id => id.Value)
            .ToList();

        var result = new Dictionary<string, PositionTrackDto>(sorted.Count);
        foreach (var id in sorted)
            result[id.Value.ToString(CultureInfo.InvariantCulture)] = BuildTrackDto(tracks[id], origin, scale);
        return result;
    }

    private static PositionTrackDto BuildTrackDto(PositionTrack track, (float X, float Z) origin, float scale)
    {
        var samples = track.Snapshot();
        if (samples.Length == 0)
            return new PositionTrackDto(0, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());

        var absX   = new int[samples.Length];
        var absZ   = new int[samples.Length];
        var absY   = new int[samples.Length];
        var absYaw = new int[samples.Length];

        for (var i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            absX[i]   = PositionCodec.Quantize(s.X - origin.X, scale);
            absZ[i]   = PositionCodec.Quantize(s.Z - origin.Z, scale);
            absY[i]   = PositionCodec.Quantize(s.Y, scale);
            absYaw[i] = PositionCodec.QuantizeYaw(s.Yaw);
        }

        return new PositionTrackDto(
            Ms0: samples[0].Ms,
            Dx:  PositionCodec.DeltaEncode(absX),
            Dz:  PositionCodec.DeltaEncode(absZ),
            Y:   PositionCodec.DeltaEncode(absY),
            Yaw: PositionCodec.DeltaEncode(absYaw));
    }

    private static IReadOnlyDictionary<string, PositionMetaDto> BuildMeta(
        IReadOnlyDictionary<EntityId, PositionMetaDto>? meta)
    {
        if (meta == null || meta.Count == 0)
            return new Dictionary<string, PositionMetaDto>();

        // Sort keys ascending by numeric entity id.
        var sorted = meta.Keys
            .OrderBy(id => id.Value)
            .ToList();

        var result = new Dictionary<string, PositionMetaDto>(sorted.Count);
        foreach (var id in sorted)
            result[id.Value.ToString(CultureInfo.InvariantCulture)] = meta[id];
        return result;
    }
}
