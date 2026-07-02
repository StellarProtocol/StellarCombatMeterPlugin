// SP1: Builds the canonical signing payload matching the verify.ts canonicalPayload function.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>
/// Builds the canonical payload string for ECDSA signing / verification, matching
/// the TypeScript <c>canonicalPayload</c> function in <c>services/stellar-logs/src/worker/verify.ts</c>:
/// <c>${logId}|${levelUuid}|${localUid}|${startMs}|${endMs}|${nonce}|${sha256hex(JSON.stringify(events))}</c>
/// </summary>
internal static class CanonicalPayload
{
    /// <summary>
    /// Builds the canonical payload from the assembled <see cref="CombatLog"/>.
    /// The events JSON must be produced with the same serializer that will be uploaded
    /// (i.e. <see cref="CombatLogWriter.Write"/> — we re-serialize just the events array here
    /// so the hash matches what the server sees after re-parsing from JSON).
    /// </summary>
    internal static string Build(CombatLog log)
    {
        var eventsJson = SerializeEventsOnly(log.Events);
        var eventsHash = Sha256Hex(eventsJson);

        var h = log.Header;
        return string.Concat(
            h.LogId, "|",
            h.Encounter.LevelUuid.ToString(CultureInfo.InvariantCulture), "|",
            h.Uploader.LocalUid.ToString(CultureInfo.InvariantCulture), "|",
            h.Encounter.StartMs.ToString(CultureInfo.InvariantCulture), "|",
            h.Encounter.EndMs.ToString(CultureInfo.InvariantCulture), "|",
            h.Uploader.Nonce, "|",
            eventsHash);
    }

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(64);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // Serialize only the events array (matches JSON.stringify(log.events) on the server side).
    // Reuses the CombatLogWriter internals via a minimal wrapper.
    private static string SerializeEventsOnly(IReadOnlyList<CombatLogEvent> events)
    {
        // CombatLogWriter.Write serializes the whole log; we need just the events array.
        // Use an intermediate CombatLog with empty header fields to extract via string parsing
        // would be brittle — instead we call WriteEvents on a standalone JsonWriter.
        // CombatLogWriter's inner WriteEvents is private, so replicate a minimal subset here.
        // This is a one-time cost at archive; not on the hot-path.
        return EventsJsonWriter.Write(events);
    }
}
