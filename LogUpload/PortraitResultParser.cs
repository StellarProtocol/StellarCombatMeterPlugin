using System.Collections.Generic;
using System.Text.Json;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>Parses the /char/portraits response. Returns the uids whose identity did not fail AND
/// whose every media outcome stored ("stored"/"unchanged") — i.e. members safe to stamp as captured.
/// A member with any "…:failed…" media (or identity:"failed", or no media) is omitted so it retries.</summary>
internal static class PortraitResultParser
{
    internal static HashSet<long> FullyStoredUids(string? json)
    {
        var set = new HashSet<long>();
        if (string.IsNullOrEmpty(json)) return set;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return set;
            foreach (var r in results.EnumerateArray())
            {
                if (!r.TryGetProperty("uid", out var uidEl) || !uidEl.TryGetInt64(out var uid)) continue;
                if (r.TryGetProperty("identity", out var idEl) && idEl.GetString() == "failed") continue;
                var ok = true; var anyMedia = false;
                if (r.TryGetProperty("media", out var mediaEl) && mediaEl.ValueKind == JsonValueKind.Array)
                    foreach (var m in mediaEl.EnumerateArray())
                    {
                        anyMedia = true;
                        if ((m.GetString() ?? "").Contains(":failed")) { ok = false; break; }
                    }
                if (ok && anyMedia) set.Add(uid);
            }
        }
        catch { /* malformed → stamp nothing (safe: retries) */ }
        return set;
    }
}
