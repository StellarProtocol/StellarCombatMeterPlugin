using System.Collections.Generic;
using Stellar.CombatMeter;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// mapId → <see cref="ContentKind"/>, sourced from the worker's <c>GET /api/site/content-kinds</c>
/// (spec § 2.3). The site's <c>rankedContent.ts</c> stays the single source of truth, so a content
/// patch that adds a raid needs no plugin release.
///
/// Anything not listed — including every mapId when the endpoint has never been reached — is
/// <see cref="ContentKind.Other"/>. With all-Auto defaults that is behaviour-identical to today, so a
/// fresh install with no network uploads exactly as it does now.
///
/// Parsed with the hand-rolled <see cref="HistoryJsonReader"/>: no <c>System.Text.Json</c> (reflection,
/// AOT-stripped under IL2CPP). Immutable once built.
/// </summary>
internal sealed class ContentKindMap
{
    private readonly HashSet<int> _dungeon;
    private readonly HashSet<int> _raid;
    private readonly HashSet<int> _worldboss;

    private ContentKindMap(HashSet<int> dungeon, HashSet<int> raid, HashSet<int> worldboss)
    {
        _dungeon = dungeon;
        _raid = raid;
        _worldboss = worldboss;
    }

    /// <summary>The never-fetched map: classifies everything as <see cref="ContentKind.Other"/>.</summary>
    internal static ContentKindMap Empty { get; } = new(new HashSet<int>(), new HashSet<int>(), new HashSet<int>());

    internal bool IsEmpty => _dungeon.Count == 0 && _raid.Count == 0 && _worldboss.Count == 0;

    internal ContentKind KindOf(int mapId)
    {
        if (_dungeon.Contains(mapId)) return ContentKind.Dungeon;
        if (_raid.Contains(mapId)) return ContentKind.Raid;
        if (_worldboss.Contains(mapId)) return ContentKind.WorldBoss;
        return ContentKind.Other;   // unlisted content, unparseable scene, or no map fetched yet
    }

    /// <summary>Ids for one kind, for the prefs cache. <see cref="ContentKind.Other"/> is the implicit
    /// fallback and always yields an empty array.</summary>
    internal int[] Ids(ContentKind kind)
    {
        var set = SetFor(kind);
        if (set is null) return System.Array.Empty<int>();
        var ids = new int[set.Count];
        set.CopyTo(ids);
        return ids;
    }

    private HashSet<int>? SetFor(ContentKind kind) => kind switch
    {
        ContentKind.Dungeon   => _dungeon,
        ContentKind.Raid      => _raid,
        ContentKind.WorldBoss => _worldboss,
        _                     => null,
    };

    /// <summary>Rebuilds from the prefs cache. Null arrays are treated as empty.</summary>
    internal static ContentKindMap FromIds(int[]? dungeon, int[]? raid, int[]? worldboss)
        => new(ToSet(dungeon), ToSet(raid), ToSet(worldboss));

    private static HashSet<int> ToSet(int[]? ids)
    {
        var set = new HashSet<int>();
        if (ids is not null) foreach (var id in ids) set.Add(id);
        return set;
    }

    /// <summary>
    /// Parses the endpoint payload <c>{"version":1,"kinds":{"dungeon":[…],"raid":[…],"worldboss":[…],"other":[]}}</c>.
    /// Returns <c>false</c> — with <paramref name="map"/> set to a usable all-Other map, never null —
    /// for absent, malformed, or wholly empty payloads, so a bad response can never wipe a good cache.
    /// </summary>
    internal static bool TryParse(string? json, out ContentKindMap map)
    {
        map = Empty;
        if (string.IsNullOrEmpty(json)) return false;

        var dungeon = new HashSet<int>();
        var raid = new HashSet<int>();
        var worldboss = new HashSet<int>();
        if (!TryReadKinds(json!, dungeon, raid, worldboss)) return false;

        var parsed = new ContentKindMap(dungeon, raid, worldboss);
        if (parsed.IsEmpty) return false;   // a payload with no ids is not a usable map
        map = parsed;
        return true;
    }

    // Pulls the three id arrays out of the "kinds" object. HistoryJsonReader is a RAW TOKEN STREAM
    // (Next() → JsonTokenKind, with StringValue/NumberValue) — it has no seek/object/array helpers, so
    // this walks tokens directly, exactly as HistoryStore.Read.cs does. Deliberately permissive: an
    // unrecognised key ("other", or a future fifth kind) is read and discarded rather than being fatal.
    private static bool TryReadKinds(string json, HashSet<int> dungeon, HashSet<int> raid, HashSet<int> worldboss)
    {
        var r = new HistoryJsonReader(json);

        // Seek the "kinds" object: a String token "kinds" followed by ':' then '{'.
        while (true)
        {
            var k = r.Next();
            if (k == JsonTokenKind.Eof || k == JsonTokenKind.Error) return false;
            if (k != JsonTokenKind.String || r.StringValue != "kinds") continue;
            if (r.Next() != JsonTokenKind.Colon) return false;
            if (r.Next() != JsonTokenKind.ObjectStart) return false;
            break;
        }

        // key → int-array pairs until the "kinds" object closes.
        while (true)
        {
            var k = r.Next();
            if (k == JsonTokenKind.ObjectEnd) return true;
            if (k == JsonTokenKind.Comma) continue;
            if (k != JsonTokenKind.String) return false;

            var target = r.StringValue switch
            {
                "dungeon"   => dungeon,
                "raid"      => raid,
                "worldboss" => worldboss,
                _           => null,       // "other" / unknown kind: consume the array, keep nothing
            };
            if (r.Next() != JsonTokenKind.Colon) return false;
            if (r.Next() != JsonTokenKind.ArrayStart) return false;
            while (true)
            {
                var t = r.Next();
                if (t == JsonTokenKind.ArrayEnd) break;
                if (t == JsonTokenKind.Comma) continue;
                if (t != JsonTokenKind.Number) return false;
                target?.Add((int)r.NumberValue);
            }
        }
    }
}
