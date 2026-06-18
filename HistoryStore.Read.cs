using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter;

/// <summary>Deserialize half of <see cref="HistoryStore"/> — the reflection-free, never-throwing reader.</summary>
internal static partial class HistoryStore
{
    /// <summary>
    /// Parse one serialized entry. Returns false (and leaves <paramref name="entry"/> null) on ANY malformed or
    /// unsupported input — the caller skips it and keeps the rest of the history.
    /// </summary>
    internal static bool TryDeserializeEntry(string json, out Plugin.EncounterHistoryEntry? entry)
    {
        entry = null;
        var r = new HistoryJsonReader(json);
        if (r.Next() != JsonTokenKind.ObjectStart) return false;
        var e = new Plugin.EncounterHistoryEntry();
        if (!ReadEntryFields(r, e)) return false;
        // Reject anything that didn't carry the version marker (legacy / corrupt).
        entry = e;
        return true;
    }

    // Read the top-level entry object's key/value pairs. Returns false on structural error.
    private static bool ReadEntryFields(HistoryJsonReader r, Plugin.EncounterHistoryEntry e)
    {
        var seenVersion = false;
        while (true)
        {
            var k = r.Next();
            if (k == JsonTokenKind.ObjectEnd) return seenVersion;   // require "v" to have appeared
            if (k == JsonTokenKind.Comma) continue;
            if (k != JsonTokenKind.String) return false;            // key must be a string
            var key = r.StringValue;
            if (r.Next() != JsonTokenKind.Colon) return false;
            switch (key)
            {
                // Accept any supported version (v1 || v2). v1 entries simply carry no "entities" key, so they load
                // with an empty Entities map — backward compatible. A future (unsupported) version is rejected.
                case "v":       if (!ReadLong(r, out var v)) return false; seenVersion = v >= MinSupportedVersion && v <= FormatVersion; break;
                case "scene":   if (!ReadString(r, out var sc)) return false; e.SceneName = sc; break;
                case "enter":   if (!ReadLong(r, out e.EnteredAtMs)) return false; break;
                case "arch":    if (!ReadLong(r, out e.ArchivedAtMs)) return false; break;
                case "dur":     if (!ReadLong(r, out e.CombatDurationMs)) return false; break;
                case "party":   if (!ReadLong(r, out var pt)) return false; e.PartyType = (PartyType)(int)pt; break;
                case "members": if (!ReadLong(r, out var mc)) return false; e.MemberCount = (int)mc; break;
                case "stats":   if (!ReadStats(r, e.Stats)) return false; break;
                case "series":  if (!ReadSeries(r, e.Series)) return false; break;
                case "entities": if (!ReadEntities(r, e.Entities)) return false; break;
                default: return false;
            }
        }
    }

    private static bool ReadStats(HistoryJsonReader r, Dictionary<EntityId, SourceStats> into)
        => ReadArray(r, () =>
        {
            var s = new SourceStats();
            long id = 0;
            if (!ReadObject(r, key =>
            {
                switch (key)
                {
                    case "id":  return ReadLong(r, out id);
                    case "td":  return ReadLong(r, out s.TotalDamage);
                    case "th":  return ReadLong(r, out s.TotalHealing);
                    case "tk":  return ReadLong(r, out s.TotalTaken);
                    case "top": return ReadLong(r, out s.TopHit);
                    case "h":   return ReadInt(r, out s.Hits);
                    case "c":   return ReadInt(r, out s.Crits);
                    case "lk":  return ReadInt(r, out s.Luckys);
                    case "k":   return ReadInt(r, out s.Kills);
                    case "fh":  return ReadLong(r, out s.FirstHitMs);
                    case "lh":  return ReadLong(r, out s.LastHitMs);
                    case "sk":  return ReadSkills(r, s.BySkill);
                    case "in":  return ReadIncoming(r, s.IncomingBySkill);
                    default:    return false;
                }
            })) return false;
            into[new EntityId(id)] = s;
            return true;
        });

    private static bool ReadSkills(HistoryJsonReader r, Dictionary<int, SkillStats> into)
        => ReadArray(r, () =>
        {
            var sk = new SkillStats(); int id = 0;
            if (!ReadObject(r, key => key switch
            {
                "id"  => ReadInt(r, out id),
                "t"   => ReadLong(r, out sk.Total),
                "ht"  => ReadLong(r, out sk.HealTotal),
                "h"   => ReadInt(r, out sk.Hits),
                "c"   => ReadInt(r, out sk.Crits),
                "lk"  => ReadInt(r, out sk.Luckys),
                "top" => ReadLong(r, out sk.TopHit),
                _     => false,
            })) return false;
            into[id] = sk;
            return true;
        });

    private static bool ReadIncoming(HistoryJsonReader r, Dictionary<int, IncomingSkillStats> into)
        => ReadArray(r, () =>
        {
            var inc = new IncomingSkillStats(); int id = 0;
            if (!ReadObject(r, key => key switch
            {
                "id"  => ReadInt(r, out id),
                "t"   => ReadLong(r, out inc.Total),
                "h"   => ReadInt(r, out inc.Hits),
                "top" => ReadLong(r, out inc.TopHit),
                _     => false,
            })) return false;
            into[id] = inc;
            return true;
        });

    private static bool ReadSeries(HistoryJsonReader r, Dictionary<EntityId, SourceSeries> into)
        => ReadArray(r, () =>
        {
            var sr = new SourceSeries(); long id = 0;
            if (!ReadObject(r, key =>
            {
                switch (key)
                {
                    case "id": return ReadLong(r, out id);
                    case "b":  return ReadInt(r, out sr.BucketMs);
                    case "d":  return ReadLongArray(r, out sr.Dealt);
                    case "hl": return ReadLongArray(r, out sr.Healing);
                    case "tk": return ReadLongArray(r, out sr.Taken);
                    default:   return false;
                }
            })) return false;
            into[new EntityId(id)] = sr;
            return true;
        });

    // v2 entities. Each element parses into a snapshot; unknown keys / wrong-shaped values fail the whole entry
    // (consistent with the rest of the reader), but mismatched parallel-array LENGTHS are tolerated — Clamp()
    // truncates each group to its shortest member so a hand-truncated file degrades instead of mis-indexing.
    private static bool ReadEntities(HistoryJsonReader r, Dictionary<EntityId, EntitySnapshot> into)
        => ReadArray(r, () =>
        {
            var s = new EntitySnapshot();
            long id = 0;
            if (!ReadObject(r, key =>
            {
                switch (key)
                {
                    case "id":  return ReadLong(r, out id);
                    case "nm":  return ReadString(r, out s.Name);
                    case "fp":  return ReadLong(r, out s.FightPoint);
                    case "hp":  return ReadLong(r, out s.Hp);
                    case "mhp": return ReadLong(r, out s.MaxHp);
                    case "tm":  return ReadLong(r, out s.TeamId);
                    case "ai":  return ReadIntArray(r, out s.AttrIds);
                    case "av":  return ReadLongArray(r, out s.AttrValues);
                    case "gs":  return ReadIntArray(r, out s.GearSlots);
                    case "gi":  return ReadIntArray(r, out s.GearItemIds);
                    case "si":  return ReadIntArray(r, out s.SkillIds);
                    case "sl":  return ReadIntArray(r, out s.SkillLevels);
                    case "st":  return ReadIntArray(r, out s.SkillTiers);
                    case "fs":  return ReadIntArray(r, out s.FashionSlots);
                    case "fi":  return ReadIntArray(r, out s.FashionIds);
                    case "fc":  return ReadIntArray(r, out s.FashionDyeCounts);
                    case "fd":  return ReadFloatArray(r, out s.FashionDyes);
                    default:    return false;
                }
            })) return false;
            ClampSnapshot(s);
            into[new EntityId(id)] = s;
            return true;
        });

    // Defensive clamp: a truncated/garbage file can leave the index-aligned arrays at unequal lengths. Truncate
    // each parallel group to its shortest member so the render loops (which index by the shortest) never throw.
    private static void ClampSnapshot(EntitySnapshot s)
    {
        var attrN = Min(s.AttrIds.Length, s.AttrValues.Length);
        s.AttrIds = Trim(s.AttrIds, attrN); s.AttrValues = Trim(s.AttrValues, attrN);

        var gearN = Min(s.GearSlots.Length, s.GearItemIds.Length);
        s.GearSlots = Trim(s.GearSlots, gearN); s.GearItemIds = Trim(s.GearItemIds, gearN);

        var skillN = Min(Min(s.SkillIds.Length, s.SkillLevels.Length), s.SkillTiers.Length);
        s.SkillIds = Trim(s.SkillIds, skillN);
        s.SkillLevels = Trim(s.SkillLevels, skillN);
        s.SkillTiers = Trim(s.SkillTiers, skillN);

        var fashN = Min(Min(s.FashionSlots.Length, s.FashionIds.Length), s.FashionDyeCounts.Length);
        s.FashionSlots = Trim(s.FashionSlots, fashN);
        s.FashionIds = Trim(s.FashionIds, fashN);
        s.FashionDyeCounts = Trim(s.FashionDyeCounts, fashN);
        // FashionDyes is a flat RGBA stream consumed by FashionDyeCounts; leave it as read (the renderer clamps
        // its own cursor against the stream length), but never let it carry a partial colour.
        s.FashionDyes = Trim(s.FashionDyes, s.FashionDyes.Length - s.FashionDyes.Length % 4);
    }

    private static int Min(int a, int b) => a < b ? a : b;

    private static int[] Trim(int[] arr, int n) => arr.Length == n ? arr : SubArrayInt(arr, n);
    private static long[] Trim(long[] arr, int n) => arr.Length == n ? arr : SubArrayLong(arr, n);
    private static float[] Trim(float[] arr, int n) => arr.Length == n ? arr : SubArrayFloat(arr, n);

    private static int[] SubArrayInt(int[] arr, int n) { var o = new int[n]; System.Array.Copy(arr, o, n); return o; }
    private static long[] SubArrayLong(long[] arr, int n) { var o = new long[n]; System.Array.Copy(arr, o, n); return o; }
    private static float[] SubArrayFloat(float[] arr, int n) { var o = new float[n]; System.Array.Copy(arr, o, n); return o; }
}
