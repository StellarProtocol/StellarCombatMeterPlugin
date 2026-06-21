using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>
/// Sticky last-known entity snapshots. The framework's entity / name services are AOI-scoped: a player who
/// leaves the area of interest (or a scene teardown) drops their live data. The archive used to read that live
/// data LAZILY at <see cref="Plugin.ManualArchive"/> time, by which point it was already gone — so non-self
/// combatants froze as <c>Player#&lt;uid&gt;</c> with Level 0 and every stat blank, and a player who left AOI
/// mid-fight reverted to <c>Player#&lt;uid&gt;</c> in the live table.
///
/// <para>
/// We instead capture each combatant's full <see cref="EntitySnapshot"/> WHILE they're live and in AOI, freeze
/// it, and keep the last-known-good copy. The archive, the history source rows, and the live rows all read this
/// store, so identity + stats survive AOI-exit and the scene-change-before-archive race. Capture is throttled
/// and runs only until a player's snapshot is populated (entity detail rarely changes mid-encounter), so the
/// cost is ~party-size captures per encounter, not per frame. Cleared by <see cref="Plugin.Clear"/> with the
/// rest of the live encounter state.
/// </para>
/// </summary>
public sealed partial class Plugin
{
    private readonly Dictionary<EntityId, EntitySnapshot> _entitySnaps = new();

    // Sticky-capture cadence, independent of the 10 Hz row-snapshot rebuild.
    private const float EntitySnapIntervalS = 1.0f;
    private float _entitySnapAccum;

    // The local player's last-known display name. PlayerState.Name goes blank in social areas (guild center,
    // homestead, …) where the name attribute isn't broadcast, so EntityLabel falls back to "Self" for our own
    // row. Our name never changes within a session, so we cache it whenever it's present and reuse it. NOT
    // cleared by Clear() — it must outlive encounter resets / scene changes.
    private string? _lastKnownSelfName;

    private void TickEntitySnapshots(float deltaTime)
    {
        _entitySnapAccum += deltaTime;
        if (_entitySnapAccum < EntitySnapIntervalS) return;
        _entitySnapAccum = 0f;
        // Cache the local name at 1 Hz (it changes rarely) — no need to poll PlayerState.Name every frame.
        var live = _services.PlayerState.Name;
        if (!string.IsNullOrEmpty(live)) _lastKnownSelfName = live;
        RefreshEntitySnapshots();
    }

    // The cached local-player name to use when EntityLabel can only produce the "Self" fallback (PlayerState.Name
    // blank in a social area). Null until we've seen a real name at least once this session.
    private string? SelfNameFallback() => _lastKnownSelfName;

    // Sticky sub-profession (spec) cache, keyed by the stable char id. The framework's ICombatSpec cache resets
    // on every scene change, so a party member's spec (e.g. "Frost Mage") reverts to the base class name once you
    // change areas — even though you fought alongside them and the spec was known. A player's spec is stable
    // within a session, so we remember the last non-zero spec per character and reuse it whenever the live cache
    // has been cleared. A respec overwrites it on the next observed cast. NOT cleared by Clear() — it must
    // outlive encounter resets and scene changes (that's the whole point).
    private readonly Dictionary<long, int> _lastKnownSpec = new();

    private int StickySpec(EntityId id, int liveSpec)
    {
        long charId = id.Value >> 16;
        if (liveSpec > 0) { _lastKnownSpec[charId] = liveSpec; return liveSpec; }
        return _lastKnownSpec.TryGetValue(charId, out var cached) ? cached : 0;
    }

    // Session-persistent name cache, keyed by stable char id. StickyName (the per-encounter snapshot store)
    // clears on scene change, so a party member's name reverts to "Player#<uid>" after you change areas. This
    // cache outlives scene changes: we remember every real name we resolve and reuse it when live resolution
    // degrades to the synthesized fallback. NOT cleared by Clear().
    private readonly Dictionary<long, string> _lastKnownName = new();

    private void RememberName(EntityId id, string name) => _lastKnownName[id.Value >> 16] = name;
    private string? LastKnownName(EntityId id) => _lastKnownName.TryGetValue(id.Value >> 16, out var n) ? n : null;

    // Capture each participating PLAYER whose sticky snapshot isn't populated yet. Once a player's snapshot has
    // real attributes (entity detail loaded for them in AOI) it's frozen and skipped — a later AOI-exit or scene
    // teardown can no longer empty it. Players we never observe live keep a best-effort stub and keep retrying
    // (cheap: the throttle bounds it and an off-AOI capture returns empty quickly), so a latecomer who enters
    // AOI partway through still gets upgraded to a full snapshot.
    private void RefreshEntitySnapshots()
    {
        foreach (var id in _stats.Keys)
        {
            if (!id.IsPlayer) continue;
            if (_entitySnaps.TryGetValue(id, out var existing) && IsSnapshotPopulated(existing)) continue;
            var fresh = CaptureEntity(id);
            if (IsSnapshotPopulated(fresh) || !_entitySnaps.ContainsKey(id)) _entitySnaps[id] = fresh;
        }
    }

    // "Populated" = entity detail was live when captured (non-zero broadcast attrs present). A synthesized
    // Player#id label alone doesn't count — we want the real stats frozen, not just a name.
    private static bool IsSnapshotPopulated(EntitySnapshot s) => s.AttrIds.Length > 0;

    // The frozen REAL display name for a live row, or null when we have no non-synthesized name yet. Lets the
    // live table keep a player's name after they leave AOI instead of reverting to "Player#<uid>".
    private string? StickyName(EntityId id)
        => _entitySnaps.TryGetValue(id, out var s) && !string.IsNullOrEmpty(s.Name) && !IsSynthesizedName(s.Name!)
            ? s.Name
            : null;

    // The synthesized fallbacks EntityLabel emits when no real name resolves (see EntityLabel.Resolve).
    internal static bool IsSynthesizedName(string name)
        => name.StartsWith("Player#") || name.StartsWith("Mob#") || name.StartsWith("Entity#");
}
