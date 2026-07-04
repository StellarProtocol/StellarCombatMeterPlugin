// Portrait batch reporting: after each run upload, send roster avatar URLs + identity
// to StellarLogs, throttled to once per 24 h per character (keyed by entity uid).

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
    private bool _portraitEmptyLogged;                                   // one-shot breadcrumb, see LogNothingToReportOnce

    /// <summary>Called from AssembleAndUpload right after the log upload is fired (main thread).
    /// Collects roster members whose stamp is stale and fires one signed batch POST. Never throws.</summary>
    private void MaybeReportPortraits()
    {
        try
        {
            var members = _services.PartyRoster.Members;                 // empty on solo/NPC runs — self is covered below
            _portraitStamps ??= LoadPortraitStamps();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var entries = new List<PortraitEntry>(members.Count + 1);
            var uids = new List<long>(members.Count + 1);
            foreach (var m in members)
            {
                var entry = BuildPortraitEntry(m, now);
                if (entry is null) continue;
                entries.Add(entry);
                uids.Add(entry.Uid);
                if (entries.Count == 24) break;                          // server cap
            }
            AppendSelfIfMissing(entries, uids, now);
            if (entries.Count == 0) { LogNothingToReportOnce(); return; }

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
        // Entity uid ((charId << 16) | 640) — the key the StellarLogs site/DO reads for character
        // pages (same keying as the combat-log actor map). Stamps use the same uid we send.
        var uid = m.EntityId.Value;
        if (_portraitStamps!.TryGetValue(uid, out var t) && nowMs - t < PortraitTtlMs) return null;

        // Enrich from the social-snapshot cache when available (always populated for self
        // after the ID card was opened; opportunistic for others).
        var snap = _services.EntityDetail.GetSocialSnapshot(m.EntityId);

        var profileUrl  = PickUrl(m.ProfileUrl, snap?.ProfileUrl);
        var halfbodyUrl = PickUrl(m.HalfBodyUrl, snap?.HalfBodyUrl);
        if (profileUrl is null && halfbodyUrl is null) return null;      // nothing with URLs for this member yet

        return new PortraitEntry(
            Uid: uid,
            ProfileUrl:  profileUrl,
            HalfbodyUrl: halfbodyUrl,
            Name:         Truncate(snap?.Name ?? m.Name),
            Level:        snap?.Level ?? m.Level,
            ProfessionId: snap?.ProfessionId ?? m.Profession,
            Guild:        Truncate(snap?.Identity.Guild),
            MasterScore:  snap?.Identity.MasterScore ?? 0,
            TitleId:      snap?.Identity.TitleId ?? 0,
            FightPoint:   snap?.FightPoint ?? 0,
            FashionCollect:    snap?.Identity.FashionCollect ?? 0,
            RideCollect:       snap?.Identity.RideCollect ?? 0,
            WeaponSkinCollect: snap?.Identity.WeaponSkinCollect ?? 0);
    }

    /// <summary>Ensures the LOCAL player is in the batch even when the roster is empty (solo/NPC
    /// runs) or missed self. Built from the social-snapshot cache alone; no-op when the snapshot
    /// is absent, self is already batched (by uid), the stamp is fresh, or the batch is full.</summary>
    private void AppendSelfIfMissing(List<PortraitEntry> entries, List<long> uids, long nowMs)
    {
        if (entries.Count >= 24) return;                                 // server cap
        var self = TryBuildSelfEntry(nowMs);
        if (self is null || uids.Contains(self.Uid)) return;
        entries.Add(self);
        uids.Add(self.Uid);
    }

    /// <summary>Builds the local player's entry from the cached social snapshot (populated after
    /// the ID card was opened), or null when unavailable/throttled/URL-less.</summary>
    private PortraitEntry? TryBuildSelfEntry(long nowMs)
    {
        var selfEntity = _services.CombatSnapshot.LocalEntityId;
        if (selfEntity.IsNone) return null;                              // not in world yet (no snapshot key either)
        var snap = _services.EntityDetail.GetSocialSnapshot(selfEntity);
        if (snap is null) return null;

        // Entity uid, same keying as BuildPortraitEntry: prefer the live LocalEntityId; the
        // (charId << 16) | 640 reconstruction from the snapshot is the equivalent fallback.
        var uid = selfEntity.Value != 0 ? selfEntity.Value : (snap.CharId << 16) | 640;
        if (uid == 0) return null;
        if (_portraitStamps!.TryGetValue(uid, out var t) && nowMs - t < PortraitTtlMs) return null;

        var profileUrl  = PickUrl(snap.ProfileUrl, null);
        var halfbodyUrl = PickUrl(snap.HalfBodyUrl, null);
        if (profileUrl is null && halfbodyUrl is null) return null;      // no pictures on the CDN yet

        return new PortraitEntry(
            Uid: uid,
            ProfileUrl:  profileUrl,
            HalfbodyUrl: halfbodyUrl,
            Name:         Truncate(snap.Name),
            Level:        snap.Level,
            ProfessionId: snap.ProfessionId,
            Guild:        Truncate(snap.Identity.Guild),
            MasterScore:  snap.Identity.MasterScore,
            TitleId:      snap.Identity.TitleId,
            FightPoint:   snap.FightPoint,
            FashionCollect:    snap.Identity.FashionCollect,
            RideCollect:       snap.Identity.RideCollect,
            WeaponSkinCollect: snap.Identity.WeaponSkinCollect);
    }

    // One-shot (per session) diagnosis breadcrumb: fires only when the reporter had nothing to
    // send AND the self social snapshot is absent — the case E2E would otherwise be blind to.
    private void LogNothingToReportOnce()
    {
        if (_portraitEmptyLogged) return;
        if (_services.EntityDetail.GetSocialSnapshot(_services.CombatSnapshot.LocalEntityId) is not null) return;
        _portraitEmptyLogged = true;
        _services.Log.Info("[CombatMeter.Portraits] Nothing to report: no eligible roster entries and no self social snapshot cached yet.");
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
