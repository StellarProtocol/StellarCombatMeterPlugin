// Portrait batch reporting: after each run upload, send roster avatar URLs + identity
// to StellarLogs. Each member's profile is uploaded only when its content changes (content-hash gate), keyed by entity uid.

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
    private const string PrefPortraitHashes = "portraits.sentHashes.v2";   // "uid:hexhash,uid:hexhash,…"
    private const int PortraitMaxTextLen = 64;                          // server rejects name/guild > 64 chars
    private const int PortraitMaxUrlLen = 1024;                         // server rejects urls > 1024 chars

    private const int MasterScorePollAttempts = 4;
    private const int MasterScorePollDelayMs = 1500;
    private const string PrefMasterScoreLastSentPrefix = "masterScore.lastSent.";  // + self uid
    private const int MasterScoreNeverSent = -1;                                    // sentinel: no persisted baseline yet

    private Dictionary<long, string>? _portraitHashes;                  // uid -> last successfully-sent ChangeHash
    private readonly ConcurrentQueue<Dictionary<long, string>> _portraitAcks = new();
    private bool _portraitEmptyLogged;                                   // one-shot breadcrumb, see LogNothingToReportOnce

    /// <summary>Called from AssembleAndUpload right after the log upload is fired (main thread).
    /// Collects roster members whose profile content changed since the last successful send and fires one signed batch POST. Never throws.</summary>
    private void MaybeReportPortraits()
    {
        try
        {
            if (!RegionKnownOrWarn()) return;                            // Task 12: withhold — region rides the batch body
            var members = _services.PartyRoster.Members;                 // empty on solo/NPC runs — self is covered below
            _portraitHashes ??= LoadPortraitHashes();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var candidates = new List<PortraitEntry>(members.Count + 1);
            foreach (var m in members)
            {
                var entry = BuildPortraitEntry(m, now);
                if (entry is not null) candidates.Add(entry);
                if (candidates.Count == 24) break;
            }
            AppendSelfIfMissing(candidates, candidates.ConvertAll(e => e.Uid), now);

            // Include only members whose content CHANGED since the last successful send.
            var entries = new List<PortraitEntry>(candidates.Count);
            var sentHashes = new Dictionary<long, string>(candidates.Count);
            foreach (var e in candidates)
            {
                var hash = PortraitReport.ChangeHash(e);
                if (_portraitHashes.TryGetValue(e.Uid, out var prev) && prev == hash) continue;
                entries.Add(e);
                sentHashes[e.Uid] = hash;
            }
            if (entries.Count == 0) { LogNothingToReportOnce(); return; }

            var localUid = LocalUidForUpload();
            var nonce = Guid.NewGuid().ToString("N");
            var entriesJson = PortraitReport.WriteEntries(entries);
            var sig = SignPortraits(SignerKey, PortraitReport.CanonicalPayload(localUid, nonce, entriesJson));
            var body = PortraitReport.WriteBody(localUid, nonce, sig, entriesJson, _services.GameEnvironment.RegionCode);

            _services.Log.Info($"[CombatMeter.Portraits] Reporting {entries.Count} changed portrait(s).");
            PortraitUploader.UploadFireAndForget(body, (ok, status, respBody) =>
            {
                if (!ok) { _services.Log.Warning($"[CombatMeter.Portraits] Report FAILED (HTTP {status})."); return; }
                var stored = PortraitResultParser.FullyStoredUids(respBody);
                var toStamp = new Dictionary<long, string>(sentHashes.Count);
                foreach (var kv in sentHashes) if (stored.Contains(kv.Key)) toStamp[kv.Key] = kv.Value;
                if (toStamp.Count > 0) _portraitAcks.Enqueue(toStamp);   // members with a failed image are NOT stamped → retried
            });
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[CombatMeter.Portraits] Report threw: {ex.Message}");
        }
    }

    /// <summary>Builds one member's batch entry, or null when the member is skipped
    /// (no charId or no usable portrait URL yet).</summary>
    private PortraitEntry? BuildPortraitEntry(PartyMember m, long nowMs)
    {
        if (m.CharId == 0) return null;
        // Entity uid ((charId << 16) | 640) — the key the StellarLogs site/DO reads for character
        // pages (same keying as the combat-log actor map). Stamps use the same uid we send.
        var uid = m.EntityId.Value;

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
    /// is absent, self is already batched (by uid), or the batch is full.</summary>
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
    /// next to the other cross-thread drains). Persists hashes only after a 2xx.</summary>
    private void DrainPortraitAcks()
    {
        var dirty = false;
        while (_portraitAcks.TryDequeue(out var sent))
        {
            _portraitHashes ??= LoadPortraitHashes();
            foreach (var kv in sent) _portraitHashes[kv.Key] = kv.Value;
            dirty = true;
        }
        if (dirty) SavePortraitHashes();
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

    private Dictionary<long, string> LoadPortraitHashes()
    {
        var raw = _prefs.Get(PrefPortraitHashes, "") ?? "";
        var map = new Dictionary<long, string>();
        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf(':');
            if (i > 0 && long.TryParse(pair.AsSpan(0, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid))
                map[uid] = pair[(i + 1)..];
        }
        return map;
    }

    private void SavePortraitHashes()
    {
        if (_portraitHashes is null) return;
        var sb = new StringBuilder(_portraitHashes.Count * 72);
        foreach (var kv in _portraitHashes)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append(':').Append(kv.Value);
        }
        _prefs.Set(PrefPortraitHashes, sb.ToString());
        _prefs.Save();
    }

    /// <summary>Called from <c>SerializeAndUpload</c> right after a master-mode run's log upload
    /// is fired (main thread), gated on <see cref="MasterScoreRefresh.IsMasterModeRun"/>. ALWAYS
    /// refreshes the account master score, then pushes it via a self-only, identity-only batch —
    /// completely decoupled from the throttled roster portrait feed above (does NOT read/write
    /// <see cref="_portraitHashes"/>). Fire-and-forget; never throws into the caller.
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
        PortraitUploader.UploadFireAndForget(body, (ok, status, _) =>
        {
            if (!ok) _services.Log.Warning($"[CombatMeter.MasterScore] Send FAILED (HTTP {status}).");
        });
    }
}
