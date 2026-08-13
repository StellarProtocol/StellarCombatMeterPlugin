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
/// Multi-boss (2026-08-12, multi-boss-per-battle plan Task 4): <see cref="Bosses"/> additively
/// carries EVERY boss the stage's <c>StageBossSet</c> knows this window — id, monster config id, and
/// its own sliced HP track — for a stage with more than one boss (e.g. a raid's two-co-boss phase).
/// <see cref="BossEntityId"/>/<see cref="BossHp"/> stay populated too, as the FIRST-ADMITTED member's
/// representative (else today's single-boss behavior when the stage set is empty) — old readers that
/// only understand the scalar pair keep working unchanged. <c>Bosses</c> is null on a bossless run or
/// when boss-phase detection never populated the set. Downstream (Plan A2/A3, not this plugin): the
/// worker/site prefer <c>bosses[]</c> when present.
/// </para>
/// <para>
/// ELITE CAPTURE channel (owner ruling 2026-08-13): <see cref="Elites"/> additively carries every
/// MonsterType==1 entity captured this window — SAME <see cref="BossTrackDto"/> shape as
/// <see cref="Bosses"/> (id, monster config id, sliced HP track), reused as-is rather than a duplicate
/// DTO type. CAPTURE ONLY: unlike <see cref="Bosses"/> there is no scalar representative (no
/// <c>EliteEntityId</c>/<c>EliteHp</c> pair) and it feeds nothing in AutoArchive/BossStatus/verdict
/// paths — see <c>AutoArchive/EliteSet.cs</c>. Null on a bossless/eliteless run or when the channel
/// never captured anything.
/// </para>
/// <para>
/// Boss + playerHp are emitted only by the full <see cref="PositionJsonWriter.Write"/>
/// output — NOT by <see cref="PositionJsonWriter.WriteBodyOnly"/>, which the worker's
/// signature verification hashes and must match exactly
/// <c>{hz,mapId,origin,scale,tracks,meta}</c>. <c>Bosses</c>/<c>Elites</c> are likewise excluded —
/// signature-neutral, like <c>bossHp</c>/<c>playerHp</c>.
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
    IReadOnlyDictionary<string, HpTrack>? PlayerHp = null,
    IReadOnlyList<BossTrackDto>? Bosses = null,
    IReadOnlyList<BossTrackDto>? Elites = null);

/// <summary>
/// One stage boss's id/config/HP timeline, carried in <see cref="PositionUploadDoc.Bosses"/>
/// alongside the scalar <see cref="PositionUploadDoc.BossEntityId"/>/<see cref="PositionUploadDoc.BossHp"/>
/// representative (multi-boss per battle, Spec A / Task 4). <see cref="EntityId"/> is the decimal
/// entity-id string (same encoding as <see cref="PositionUploadDoc.BossEntityId"/>/Tracks/Meta keys).
/// <see cref="ConfigId"/> is the monster-table config id (e.g. 102800 Sunfire, 102801 Moonstrike)
/// snapshotted at admission — master data (names) is resolved server/site-side; the plugin sends ids
/// only. <see cref="Hp"/> is null when this boss has no sampled HP in the current window
/// (position-only presence this window). Additive: old readers ignore it.
/// </summary>
internal sealed record BossTrackDto(string EntityId, int ConfigId, HpTrack? Hp);

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
