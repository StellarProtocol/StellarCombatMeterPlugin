using System.Collections.Generic;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// mapId → <see cref="ContentTier"/>, sourced from the worker's <c>GET /api/site/content-kinds</c>
/// <c>tiers</c> object (payload version 2). Sibling of <see cref="ContentKindMap"/> and cached the same
/// way, for the same reason (spec § 2.3's chosen option): the taxonomy lives in the site's
/// <c>rankedContent.ts</c>, so a content patch that retiers or adds content needs no plugin release.
/// Shipping the table in the plugin is option 2 in that spec and is recorded as rejected.
///
/// <para><b>Absent ⇒ <see cref="ContentTier.Unknown"/> ⇒ the filter FAILS OPEN.</b> The worker
/// deliberately omits untiered ranked content (world bosses, untiered dungeon entries), and a plugin
/// that has never reached the endpoint has an empty map. Neither may withhold an upload: a tier filter
/// must never suppress content that has no tier to compare.</para>
///
/// <para>Parsed with the hand-rolled <see cref="HistoryJsonReader"/> — no <c>System.Text.Json</c>
/// (reflection, AOT-stripped under IL2CPP). Immutable once built.</para>
/// </summary>
internal sealed class ContentTierMap
{
    private readonly Dictionary<int, ContentTier> _byMapId;

    private ContentTierMap(Dictionary<int, ContentTier> byMapId) => _byMapId = byMapId;

    /// <summary>The never-fetched map: every mapId resolves <see cref="ContentTier.Unknown"/>.</summary>
    internal static ContentTierMap Empty { get; } = new(new Dictionary<int, ContentTier>());

    internal bool IsEmpty => _byMapId.Count == 0;

    internal ContentTier TierOf(int mapId)
        => _byMapId.TryGetValue(mapId, out var tier) ? tier : ContentTier.Unknown;

    /// <summary>Flattens to the parallel arrays the prefs cache stores (ids + tier keys, same order).</summary>
    internal void ToArrays(out int[] ids, out string[] tags)
    {
        ids = new int[_byMapId.Count];
        tags = new string[_byMapId.Count];
        var i = 0;
        foreach (var kv in _byMapId)
        {
            ids[i] = kv.Key;
            tags[i] = UploadTierFilter.TierKey(kv.Value);
            i++;
        }
    }

    /// <summary>Rebuilds from the prefs cache. Length-mismatched or null arrays yield an empty (fail-open)
    /// map rather than a partial one — a half-read tier map would silently withhold uploads.</summary>
    internal static ContentTierMap FromArrays(int[]? ids, string[]? tags)
    {
        if (ids is null || tags is null || ids.Length != tags.Length) return Empty;
        var map = new Dictionary<int, ContentTier>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            var tier = UploadTierFilter.ParseTier(tags[i]);
            if (tier != ContentTier.Unknown) map[ids[i]] = tier;
        }
        return map.Count == 0 ? Empty : new ContentTierMap(map);
    }

    /// <summary>
    /// Parses the <c>tiers</c> object out of the endpoint payload:
    /// <c>{"version":2,"kinds":{…},"tiers":{"1150":"master","13001":"backtrack",…}}</c>.
    /// Returns <c>false</c> — with <paramref name="map"/> set to a usable empty map, never null — for an
    /// absent, malformed, or empty <c>tiers</c> object, so a version-1 payload (or a bad response) can
    /// never wipe a good cache and never blocks an upload.
    /// </summary>
    internal static bool TryParse(string? json, out ContentTierMap map)
    {
        map = Empty;
        if (string.IsNullOrEmpty(json)) return false;

        var parsed = new Dictionary<int, ContentTier>();
        if (!TryReadTiers(json!, parsed)) return false;
        if (parsed.Count == 0) return false;
        map = new ContentTierMap(parsed);
        return true;
    }

    // Walks the raw token stream to the "tiers" object, then reads its string→string pairs. Mirrors
    // ContentKindMap.TryReadKinds: HistoryJsonReader has no seek/object helpers, so this walks tokens
    // directly. Unrecognised tier words are read and DISCARDED (not fatal) — a future tier name must
    // degrade to Unknown/fail-open rather than rejecting the whole map.
    private static bool TryReadTiers(string json, Dictionary<int, ContentTier> into)
    {
        var r = new HistoryJsonReader(json);

        while (true)
        {
            var k = r.Next();
            if (k == JsonTokenKind.Eof || k == JsonTokenKind.Error) return false;
            if (k != JsonTokenKind.String || r.StringValue != "tiers") continue;
            if (r.Next() != JsonTokenKind.Colon) return false;
            if (r.Next() != JsonTokenKind.ObjectStart) return false;
            break;
        }

        while (true)
        {
            var k = r.Next();
            if (k == JsonTokenKind.ObjectEnd) return true;
            if (k == JsonTokenKind.Comma) continue;
            if (k != JsonTokenKind.String) return false;

            var key = r.StringValue;
            if (r.Next() != JsonTokenKind.Colon) return false;
            if (r.Next() != JsonTokenKind.String) return false;
            var tier = UploadTierFilter.ParseTier(r.StringValue);
            if (tier != ContentTier.Unknown && int.TryParse(key, out var mapId)) into[mapId] = tier;
        }
    }
}
