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

    private const int MasterScorePollAttempts = 4;
    private const int MasterScorePollDelayMs = 1500;
    private const string PrefMasterScoreLastSentPrefix = "masterScore.lastSent.";  // + self uid
    private const int MasterScoreNeverSent = -1;                                    // sentinel: no persisted baseline yet

    private Dictionary<long, long>? _portraitStamps;                     // loaded lazily from prefs
    private readonly ConcurrentQueue<(List<long> Uids, long SentAtMs)> _portraitAcks = new();
    private bool _portraitEmptyLogged;                                   // one-shot breadcrumb, see LogNothingToReportOnce

    /// <summary>Called from AssembleAndUpload right after the log upload is fired (main thread).
    /// Collects roster members whose stamp is stale and fires one signed batch POST. Never throws.</summary>
    private void MaybeReportPortraits()
    {
        try
        {
            if (!RegionKnownOrWarn()) return;                            // Task 12: withhold — region rides the batch body
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
            var body = PortraitReport.WriteBody(localUid, nonce, sig, entriesJson, _services.GameEnvironment.RegionCode);

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

    /// <summary>Called from <c>SerializeAndUpload</c> right after a master-mode run's log upload
    /// is fired (main thread), gated on <see cref="MasterScoreRefresh.IsMasterModeRun"/>. ALWAYS
    /// refreshes the account master score, then pushes it via a self-only, identity-only batch —
    /// completely decoupled from the throttled roster portrait feed above (does NOT read/write
    /// <see cref="_portraitStamps"/>). Fire-and-forget; never throws into the caller.
    ///
    /// Send decision: compares the freshly-fetched score against the last score we actually SENT
    /// to the server (persisted per self-uid via <c>_prefs</c>) — NOT the volatile in-memory
    /// social-snapshot cache. Gating on the cache made correctness depend on whether the player
    /// happened to have opened their ID card this session (which pre-warms the cache to the
    /// current score and can suppress a send the char page still needs). Gating on the persisted
    /// last-sent baseline means the first run after this change always uploads (baseline unknown,
    /// sentinel <see cref="MasterScoreNeverSent"/>), and every run after that uploads only when the
    /// score genuinely differs from what was last pushed.
    ///
    /// Threading: <c>RefreshSocialSnapshot</c> drives the game's Lua VM and is main-thread-only,
    /// so it — and the <c>LocalEntityId</c> read — MUST happen synchronously here, before the first
    /// await. The poll and pref read/write that follow only touch the thread-safe social-snapshot
    /// cache and <see cref="IConfigSection"/> (internally lock-guarded), so it is safe to resume
    /// off the main thread after the await hop.</summary>
    internal async void RefreshAndSendSelfMasterScore()
    {
        try
        {
            var self = _services.CombatSnapshot.LocalEntityId;
            if (self.IsNone) return;

            _services.EntityDetail.RefreshSocialSnapshot(self);   // main-thread-only RPC; must run before any await
            var score = await MasterScoreRefresh.PollForScore(
                () => _services.EntityDetail.GetSocialSnapshot(self)?.Identity.MasterScore ?? 0,
                attempts: MasterScorePollAttempts, delayMs: MasterScorePollDelayMs).ConfigureAwait(false);

            var lastSentKey = MasterScoreLastSentKey(self);
            var lastSent = _prefs.Get(lastSentKey, MasterScoreNeverSent);
            if (!MasterScoreRefresh.ShouldSend(score, lastSent)) return;  // unpopulated, or unchanged from what we already pushed

            SendSelfMasterScoreEntry(self, score);
            _prefs.Set(lastSentKey, score);
            _prefs.Save();
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[CombatMeter.MasterScore] refresh threw: {ex.Message}");
        }
    }

    /// <summary>Per-character pref key for the last master score actually pushed to the server —
    /// keyed by self uid so alt characters on the same install don't clobber each other's baseline.</summary>
    private static string MasterScoreLastSentKey(EntityId self)
        => PrefMasterScoreLastSentPrefix + self.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Builds + sends a self-only, identity-only portrait-batch entry carrying just the
    /// fresh master score, via the existing signed <see cref="PortraitUploader"/> path. Other
    /// identity fields are omitted (0) — the server's <c>mergeIdentity</c> <c>&gt;0</c> guard
    /// ignores them, so this cannot clobber previously-reported name/guild/etc.</summary>
    // Not re-gated on RegionKnownOrWarn() here: the sole caller (RefreshAndSendSelfMasterScore,
    // invoked from AssembleAndUpload) already passed that gate before reaching this method, and
    // RefreshAndSendSelfMasterScore unconditionally persists lastSentKey right after this call —
    // an unreachable early-return here would silently desync that bookkeeping if this method were
    // ever reached ungated in the future. Region still rides the body (Task 12 wire requirement).
    private void SendSelfMasterScoreEntry(EntityId self, int score)
    {
        var entry = new PortraitEntry(
            Uid: self.Value,
            ProfileUrl: null,
            HalfbodyUrl: null,
            Name: null,
            Level: 0,
            ProfessionId: 0,
            Guild: null,
            MasterScore: score,
            TitleId: 0,
            FightPoint: 0);

        var localUid = LocalUidForUpload();
        var nonce = Guid.NewGuid().ToString("N");
        var entriesJson = PortraitReport.WriteEntries(new List<PortraitEntry> { entry });
        var sig = SignPortraits(SignerKey, PortraitReport.CanonicalPayload(localUid, nonce, entriesJson));
        var body = PortraitReport.WriteBody(localUid, nonce, sig, entriesJson, _services.GameEnvironment.RegionCode);

        _services.Log.Info($"[CombatMeter.MasterScore] Sending refreshed master score {score} for self.");
        PortraitUploader.UploadFireAndForget(body, (ok, status) =>
        {
            if (!ok) _services.Log.Warning($"[CombatMeter.MasterScore] Send FAILED (HTTP {status}).");
        });
    }
}
