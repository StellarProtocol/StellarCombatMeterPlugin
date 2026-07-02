// Builds the canonical signing payload matching the worker's canonicalPositionsPayload function.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Stellar.CombatMeter.Replay;

/// <summary>
/// Builds the canonical payload string for ECDSA signing / verification, matching
/// the TypeScript <c>canonicalPositionsPayload</c> function in
/// <c>services/stellar-logs/src/worker/verify.ts</c>:
/// <c>${logId}|${levelUuid}|${localUid}|${startMs}|${endMs}|${nonce}|${sha256hex(bodyJson)}</c>
/// where <c>bodyJson</c> is <see cref="PositionJsonWriter.WriteBodyOnly"/>.
/// </summary>
internal static class PositionCanonicalPayload
{
    /// <summary>
    /// Builds the canonical payload from <paramref name="doc"/>.
    /// The body hash covers <see cref="PositionJsonWriter.WriteBodyOnly"/> output —
    /// the same bytes the worker re-hashes after parsing.
    /// The <see cref="PositionUploadDoc.Sig"/> field is intentionally excluded.
    /// </summary>
    internal static string Build(PositionUploadDoc doc)
    {
        var bodyHash = Sha256Hex(PositionJsonWriter.WriteBodyOnly(doc));
        return string.Concat(
            doc.LogId, "|",
            doc.LevelUuid.ToString(CultureInfo.InvariantCulture), "|",
            doc.LocalUid.ToString(CultureInfo.InvariantCulture), "|",
            doc.StartMs.ToString(CultureInfo.InvariantCulture), "|",
            doc.EndMs.ToString(CultureInfo.InvariantCulture), "|",
            doc.Nonce ?? "", "|",
            bodyHash);
    }

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(64);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
