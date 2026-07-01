using System.Collections.Generic;

namespace Stellar.CombatMeter.Replay;

/// <summary>
/// Top-level DTO uploaded to the replay worker for one combat session.
/// Serialized by <see cref="PositionJsonWriter"/>.
/// </summary>
internal sealed record PositionUploadDoc(
    int Hz,
    int MapId,
    (float X, float Z) Origin,
    float Scale,
    IReadOnlyDictionary<string, PositionTrackDto> Tracks,
    IReadOnlyDictionary<string, PositionMetaDto> Meta,
    string? Sig = null,
    string? Nonce = null);

/// <summary>
/// Per-entity delta-encoded track. Arrays are delta-encoded; ms0 is absolute start time.
/// </summary>
internal sealed record PositionTrackDto(
    int Ms0,
    int[] Dx,
    int[] Dz,
    int[] Y,
    int[] Yaw);

/// <summary>
/// Per-entity metadata: kind ("player"/"npc"), display name, and profession/class id.
/// </summary>
internal sealed record PositionMetaDto(
    string Kind,
    string Name,
    int ProfessionId);
