using System.Collections.Generic;

namespace Stellar.CombatMeter.Replay;

/// <summary>
/// Top-level DTO uploaded to the replay worker for one combat session.
/// Serialized by <see cref="PositionJsonWriter"/>.
/// <para>
/// <see cref="PositionTrackAssembler.Assemble"/> populates the body fields
/// (<see cref="Hz"/>, <see cref="MapId"/>, <see cref="Origin"/>,
/// <see cref="Scale"/>, <see cref="Tracks"/>, <see cref="Meta"/>).
/// The upload caller fills the header fields via a <c>with</c> expression:
/// <code>
/// assembled with { LogId = ..., LevelUuid = ..., LocalUid = ...,
///                  StartMs = ..., EndMs = ..., Nonce = ..., Sig = ... }
/// </code>
/// </para>
/// <para>
/// Boss fields: <see cref="BossEntityId"/> is non-empty when a boss entity was
/// identified (entity id as decimal string). <see cref="BossHp"/> is non-null
/// when boss vitals were sampled; absent in bossless runs. <see cref="PlayerHp"/>
/// carries per-player HP% timelines keyed by entity id (as decimal string).
/// </para>
/// <para>
/// Boss + playerHp are emitted only by the full <see cref="PositionJsonWriter.Write"/>
/// output — NOT by <see cref="PositionJsonWriter.WriteBodyOnly"/>, which the worker's
/// signature verification hashes and must match exactly
/// <c>{hz,mapId,origin,scale,tracks,meta}</c>.
/// </para>
/// </summary>
internal sealed record PositionUploadDoc(
    int Hz,
    int MapId,
    (float X, float Z) Origin,
    float Scale,
    IReadOnlyDictionary<string, PositionTrackDto> Tracks,
    IReadOnlyDictionary<string, PositionMetaDto> Meta,
    string? Sig = null,
    string? Nonce = null,
    string LogId = "",
    long LevelUuid = 0,
    long LocalUid = 0,
    long StartMs = 0,
    long EndMs = 0,
    string BossEntityId = "",
    HpTrack? BossHp = null,
    IReadOnlyDictionary<string, HpTrack>? PlayerHp = null);

/// <summary>
/// HP% timeline sampled at the replay capture cadence (2 Hz). Used for the boss
/// (<c>bossHp</c>) and per-player (<c>playerHp</c>) uploads.
/// <para>
/// <see cref="Ms0"/> is the encounter-relative timestamp (ms) of the first sample,
/// matching the relative timestamps used by <see cref="PositionTrackDto.Ms0"/>.
/// <see cref="Pct"/> is HP% per sample: <c>round(100 * hp / maxHp)</c>, clamped 0..100.
/// Only emitted in the upload JSON when a track exists for the entity.
/// </para>
/// </summary>
internal sealed record HpTrack(long Ms0, IReadOnlyList<int> Pct);

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
