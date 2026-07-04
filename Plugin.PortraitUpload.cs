// Portrait batch reporting: after each run upload, send roster avatar URLs + identity
// to StellarLogs, throttled to once per 24 h per charId.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

public sealed partial class Plugin
{
    private const string PrefPortraitStamps = "portraits.sentStamps";   // "uid:unixMs,uid:unixMs,…"
    private const long PortraitTtlMs = 24L * 3_600_000L;
    private const int PortraitMaxTextLen = 64;                          // server rejects name/guild > 64 chars
    private const int PortraitMaxUrlLen = 1024;                         // server rejects urls > 1024 chars

    private Dictionary<long, long>? _portraitStamps;                     // loaded lazily from prefs
    private readonly ConcurrentQueue<(List<long> Uids, long SentAtMs)> _portraitAcks = new();

    /// <summary>Called from AssembleAndUpload right after the log upload is fired (main thread).
    /// Collects roster members whose stamp is stale and fires one signed batch POST. Never throws.</summary>
    private void MaybeReportPortraits()
    {
        try
        {
            var members = _services.PartyRoster.Members;
            if (members.Count == 0) return;                              // solo — nothing with URLs yet
            _portraitStamps ??= LoadPortraitStamps();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var entries = new List<PortraitEntry>(members.Count);
            var uids = new List<long>(members.Count);
            foreach (var m in members)
            {
                var entry = BuildPortraitEntry(m, now);
                if (entry is null) continue;
                entries.Add(entry);
                uids.Add(m.CharId);
                if (entries.Count == 24) break;                          // server cap
            }
            if (entries.Count == 0) return;

            var localUid = LocalUidForUpload();                          // same source as CombatLogAssembler's Uploader.LocalUid
            var nonce = Guid.NewGuid().ToString("N");
            var entriesJson = PortraitReport.WriteEntries(entries);
            var sig = SignPortraits(SignerKey, PortraitReport.CanonicalPayload(localUid, nonce, entriesJson));
            var body = PortraitReport.WriteBody(localUid, nonce, sig, entriesJson);

            _services.Log.Info($"[CombatMeter.Portraits] Reporting {entries.Count} roster portrait(s).");
            var sentAt = now;
            PortraitUploader.UploadFireAndForget(body, (ok, status) =>
            {
                if (ok) _portraitAcks.Enqueue((uids, sentAt));           // stamp on the main thread later
                else _services.Log.Warning($"[CombatMeter.Portraits] Report FAILED (HTTP {status}).");
            });
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[CombatMeter.Portraits] Report threw: {ex.Message}");
        }
    }

    /// <summary>Builds one member's batch entry, or null when the member is skipped
    /// (no charId, throttle stamp still fresh, or no usable portrait URL yet).</summary>
    private PortraitEntry? BuildPortraitEntry(PartyMember m, long nowMs)
    {
        if (m.CharId == 0) return null;
        if (_portraitStamps!.TryGetValue(m.CharId, out var t) && nowMs - t < PortraitTtlMs) return null;

        // Enrich from the social-snapshot cache when available (always populated for self
        // after the ID card was opened; opportunistic for others).
        var snap = _services.EntityDetail.GetSocialSnapshot(m.EntityId);

        var profileUrl  = PickUrl(m.ProfileUrl, snap?.ProfileUrl);
        var halfbodyUrl = PickUrl(m.HalfBodyUrl, snap?.HalfBodyUrl);
        if (profileUrl is null && halfbodyUrl is null) return null;      // nothing with URLs for this member yet

        return new PortraitEntry(
            Uid: m.CharId,
            ProfileUrl:  profileUrl,
            HalfbodyUrl: halfbodyUrl,
            Name:         Truncate(snap?.Name ?? m.Name),
            Level:        snap?.Level ?? m.Level,
            ProfessionId: snap?.ProfessionId ?? m.Profession,
            Guild:        Truncate(snap?.Identity.Guild),
            MasterScore:  snap?.Identity.MasterScore ?? 0,
            TitleId:      snap?.Identity.TitleId ?? 0,
            FightPoint:   snap?.FightPoint ?? 0);
    }

    /// <summary>Drain acks on the main thread (call from the plugin's existing per-frame poll,
    /// next to the other cross-thread drains). Persists stamps only after a 2xx.</summary>
    private void DrainPortraitAcks()
    {
        var dirty = false;
        while (_portraitAcks.TryDequeue(out var ack))
        {
            _portraitStamps ??= LoadPortraitStamps();
            foreach (var uid in ack.Uids) _portraitStamps[uid] = ack.SentAtMs;
            dirty = true;
        }
        if (dirty) SavePortraitStamps();
    }

    /// <summary>Same localUid source as <c>CombatLogAssembler.Assemble</c>'s <c>Uploader.LocalUid</c>.</summary>
    private long LocalUidForUpload() => _services.CombatSnapshot.LocalEntityId.Value;

    // Prefers the roster-broadcast URL (fresher, cheaper) and falls back to the on-demand
    // social-snapshot URL (e.g. self before the first team fast-sync). Defensively drops
    // URLs the server would reject outright (>1024 chars) rather than failing the whole batch.
    private static string? PickUrl(string? primary, string? fallback)
    {
        var v = !string.IsNullOrEmpty(primary) ? primary : fallback;
        return string.IsNullOrEmpty(v) || v!.Length > PortraitMaxUrlLen ? null : v;
    }

    // Defensively truncates name/guild so a single oversized field cannot reject the whole batch.
    private static string? Truncate(string? s)
        => string.IsNullOrEmpty(s) ? null : (s!.Length > PortraitMaxTextLen ? s[..PortraitMaxTextLen] : s);

    // Mirrors CombatLogAssembler.ComputeSig's degradation: a missing key or a key/crypto
    // failure yields an UNSIGNED batch (sig="", server rejects if it requires one) rather
    // than aborting the whole report cycle.
    private string SignPortraits(string? pkcs8Base64, string payload)
    {
        if (string.IsNullOrEmpty(pkcs8Base64)) return "";
        try
        {
            using var signer = new LogSigner(pkcs8Base64!);
            return signer.Sign(payload);
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[CombatMeter.Portraits] Signing failed ({ex.Message}) — sending unsigned.");
            return "";
        }
    }

    private Dictionary<long, long> LoadPortraitStamps()
    {
        var raw = _prefs.Get(PrefPortraitStamps, "") ?? "";
        var map = new Dictionary<long, long>();
        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf(':');
            if (i > 0
                && long.TryParse(pair.AsSpan(0, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid)
                && long.TryParse(pair.AsSpan(i + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                map[uid] = ms;
        }
        return map;
    }

    private void SavePortraitStamps()
    {
        if (_portraitStamps is null) return;
        var sb = new StringBuilder(_portraitStamps.Count * 24);
        foreach (var kv in _portraitStamps)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append(':')
              .Append(kv.Value.ToString(CultureInfo.InvariantCulture));
        }
        _prefs.Set(PrefPortraitStamps, sb.ToString());
        _prefs.Save();
    }
}
