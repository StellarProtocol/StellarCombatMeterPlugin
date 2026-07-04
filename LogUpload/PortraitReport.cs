// Portrait batch report: whole-roster avatar URLs + identity, signed like positions uploads.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>One roster member's contribution to a portrait batch.</summary>
internal sealed record PortraitEntry(
    long Uid, string? ProfileUrl, string? HalfbodyUrl,
    string? Name, int Level, int ProfessionId,
    string? Guild, int MasterScore, int TitleId, long FightPoint,
    int FashionCollect = 0, int RideCollect = 0, int WeaponSkinCollect = 0);

/// <summary>
/// Serializes a portrait batch and builds its canonical signing payload.
/// CROSS-REPO INVARIANT (services/stellar-logs/src/worker/verify.ts): the server hashes
/// JSON.stringify of the re-parsed entries — key order and key omission here MUST match:
/// uid, profileUrl, halfbodyUrl, identity{name, level, professionId, guild, masterScore,
/// titleId, fightPoint, fashionCollect, rideCollect, weaponSkinCollect}; absent/zero values
/// are OMITTED, never emitted as null/0.
/// </summary>
internal static class PortraitReport
{
    internal static string WriteEntries(IReadOnlyList<PortraitEntry> entries)
    {
        var sb = new StringBuilder(256);
        sb.Append('[');
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0) sb.Append(',');
            WriteEntry(sb, entries[i]);
        }
        sb.Append(']');
        return sb.ToString();
    }

    internal static string WriteBody(long localUid, string nonce, string sig, string entriesJson)
        => new StringBuilder(entriesJson.Length + 128)
            .Append("{\"localUid\":").Append(localUid.ToString(CultureInfo.InvariantCulture))
            .Append(",\"nonce\":\"").Append(nonce)
            .Append("\",\"sig\":\"").Append(sig)
            .Append("\",\"entries\":").Append(entriesJson).Append('}')
            .ToString();

    /// <summary>portraits|{localUid}|{nonce}|{sha256hex(entriesJson)} — matches canonicalPortraitsPayload.</summary>
    internal static string CanonicalPayload(long localUid, string nonce, string entriesJson)
        => string.Concat("portraits|", localUid.ToString(CultureInfo.InvariantCulture), "|", nonce, "|", Sha256Hex(entriesJson));

    private static void WriteEntry(StringBuilder sb, PortraitEntry e)
    {
        sb.Append("{\"uid\":").Append(e.Uid.ToString(CultureInfo.InvariantCulture));
        AppendStr(sb, "profileUrl", e.ProfileUrl);
        AppendStr(sb, "halfbodyUrl", e.HalfbodyUrl);
        var id = new StringBuilder();
        AppendStr(id, "name", e.Name);
        AppendInt(id, "level", e.Level);
        AppendInt(id, "professionId", e.ProfessionId);
        AppendStr(id, "guild", e.Guild);
        AppendInt(id, "masterScore", e.MasterScore);
        AppendInt(id, "titleId", e.TitleId);
        AppendLong(id, "fightPoint", e.FightPoint);
        AppendInt(id, "fashionCollect", e.FashionCollect);
        AppendInt(id, "rideCollect", e.RideCollect);
        AppendInt(id, "weaponSkinCollect", e.WeaponSkinCollect);
        if (id.Length > 0)
            sb.Append(",\"identity\":{").Append(id.ToString(), 1, id.Length - 1).Append('}');
        sb.Append('}');
    }

    // Each helper writes ,"key":value — WriteEntry strips the first comma for the identity object.
    private static void AppendStr(StringBuilder sb, string key, string? v)
    { if (!string.IsNullOrEmpty(v)) sb.Append(",\"").Append(key).Append("\":\"").Append(JsonEscape(v!)).Append('"'); }
    private static void AppendInt(StringBuilder sb, string key, int v)
    { if (v > 0) sb.Append(",\"").Append(key).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture)); }
    private static void AppendLong(StringBuilder sb, string key, long v)
    { if (v > 0) sb.Append(",\"").Append(key).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture)); }

    private static string JsonEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        return sb.ToString();
    }

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(64);
        foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
