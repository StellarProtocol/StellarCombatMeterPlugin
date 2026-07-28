using System.Globalization;
using Stellar.Abstractions.Services;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

// Per-content upload policy (spec 2026-07-26-combatmeter-per-content-upload-config-design.md).
// Owns the eight-cell table's prefs lifecycle, the content-kind map cache, and the cached hot-path
// booleans. Enforcement itself lives at the call sites (Plugin.LogUpload.cs / Plugin.Replay.cs).
public sealed partial class Plugin
{
    private const string PrefKindMapDungeon   = "logUpload.kindMap.dungeon";
    private const string PrefKindMapRaid      = "logUpload.kindMap.raid";
    private const string PrefKindMapWorldBoss = "logUpload.kindMap.worldboss";
    private const string PrefKindMapEtag      = "logUpload.kindMap.etag";
    private const string PrefKindMapFetchedAt = "logUpload.kindMap.fetchedAtMs";
    // Plugin version that fetched the cached map — the re-fetch TRIGGER (owner ruling 2026-07-28).
    private const string PrefKindMapVersion   = "logUpload.kindMap.pluginVersion";

    private readonly UploadPolicyTable _uploadPolicy = UploadPolicyTable.AllAuto();
    private ContentKindMap _contentKinds = ContentKindMap.Empty;

    // Cached resolution for the CURRENT scene. Recomputed on scene change and on any settings write —
    // MaybeCaptureForLog runs per combat event and TickReplayCapture per frame, so neither may touch
    // prefs or re-resolve a kind.
    private ContentKind _currentKind = ContentKind.Other;
    private bool _captureForLogEnabled;
    private bool _replayCaptureEnabled = true;

    /// <summary>Parses a stored scene name to a mapId. Mirrors
    /// <c>CombatLogAssembler.BuildEncounter</c> exactly (unparseable ⇒ 0 ⇒ classified `other`).</summary>
    internal static int ParseMapId(string? sceneName)
        => int.TryParse(sceneName ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;

    /// <summary>Kind of an ARCHIVED entry, from its STORED scene name — never the live scene, so a
    /// deferred / manual / re-upload resolves the kind the run had when it was archived (spec § 2.3).
    /// Pure static seam: Plugin cannot be instantiated headless, so this is what the tests pin.</summary>
    internal static ContentKind ResolveKind(ContentKindMap map, EncounterHistoryEntry entry)
        => map.KindOf(ParseMapId(entry.SceneName));

    /// <summary>Kind of an ARCHIVED entry, from its stored scene name — so a deferred / manual /
    /// re-upload resolves the kind the run had when it was archived, not the live scene (spec § 2.3).</summary>
    internal ContentKind ResolveKind(EncounterHistoryEntry entry)
        => ResolveKind(_contentKinds, entry);

    /// <summary>Pure seam for the archive-time replay gate: the ARCHIVED entry's own kind decides, and
    /// only <c>auto</c> permits the automatic upload. Static because Plugin cannot be instantiated
    /// headless — this is what the tests pin (artifact axis, trigger axis, and entry-derived kind all
    /// at once).</summary>
    internal static bool ReplayAutoUploadAllowed(ContentKindMap map, UploadPolicyTable policy, EncounterHistoryEntry entry)
        => UploadPolicy.Allows(policy[ResolveKind(map, entry), UploadArtifact.Replay], UploadTrigger.Auto);

    /// <summary>
    /// True when ANY kind's replay cell is not <c>off</c> — capture is deliberately kind-INDEPENDENT.
    ///
    /// Rationale (corrected 2026-07-28 against real data — the earlier version of this comment was
    /// WRONG and is recorded here so it is not reintroduced): capture asks about the scene you are
    /// standing in, while the upload gate asks about the archived run's own stored scene. Those are
    /// different questions, so keeping capture broad guarantees the upload gate can never want samples
    /// that were never taken. It also matches pre-feature behaviour, where capture followed the single
    /// global replay toggle regardless of content.
    ///
    /// What this is NOT: an earlier comment here claimed a raid lobby → boss-room scene hop with an
    /// unlisted map id would clip a raid's walk-in. That is false twice over. The owner confirms a raid
    /// is a single big map with no second load, and production data shows a real raid classifies as
    /// <c>other</c> end-to-end (mapId 12052 "Giant Golem Crusade" and 13011 "Backtrack! Dreambloom
    /// Ruins", both 20-player, are absent from RANKED_RAID_MAP_IDS), so both gates agree and there is
    /// no divergence to exploit. No failure case for the kind-keyed form has been demonstrated; this
    /// form is kept because it cannot lose samples, not because a clip was proven.
    /// </summary>
    internal static bool AnyReplayCellEnabled(UploadPolicyTable policy)
    {
        foreach (var kind in UploadPolicyTable.Kinds)
            if (policy[kind, UploadArtifact.Replay] != UploadPolicyState.Off) return true;
        return false;
    }

    internal UploadPolicyState UploadPolicyFor(ContentKind kind, UploadArtifact artifact)
        => _uploadPolicy[kind, artifact];

    internal void SetUploadPolicy(ContentKind kind, UploadArtifact artifact, UploadPolicyState state)
    {
        _uploadPolicy[kind, artifact] = state;
        _prefs.Set(UploadPolicy.PrefKey(kind, artifact), UploadPolicy.Format(state));
        _prefs.Save();
        RecomputeUploadPolicyCache();
    }

    internal bool UploadAllowed(ContentKind kind, UploadArtifact artifact, UploadTrigger trigger)
        => UploadPolicy.Allows(_uploadPolicy[kind, artifact], trigger);

    // One line per refusal naming kind/artifact/state (spec § 2.4), so "why didn't my run upload" is
    // answerable from /log. Ungated — same reasoning as the archive-outcome line.
    private void LogUploadRefusal(ContentKind kind, UploadArtifact artifact, UploadTrigger trigger, UploadPolicyState state)
        => _services.Log.Info(
            $"[CombatMeter.SP1] {UploadPolicy.ArtifactKey(artifact)} upload skipped: " +
            $"{UploadPolicy.KindKey(kind)}={UploadPolicy.Format(state)} (trigger={(trigger == UploadTrigger.Auto ? "auto" : "manual")}).");

    private void InitUploadPolicy()
    {
        LoadOrMigrateUploadPolicy();
        NormalizeReplayManualToOff();
        _contentKinds = ContentKindMap.FromIds(
            _prefs.Get<int[]>(PrefKindMapDungeon, null),
            _prefs.Get<int[]>(PrefKindMapRaid, null),
            _prefs.Get<int[]>(PrefKindMapWorldBoss, null));
        RecomputeUploadPolicyCache();
        MaybeRefreshContentKinds();
    }

    // Spec § 2.2: seed the eight cells from the two legacy prefs on the first load where no new key
    // exists, so an existing install keeps its behaviour. Legacy keys stay on disk, ignored afterwards.
    private void LoadOrMigrateUploadPolicy()
    {
        var probe = _prefs.Get<string>(UploadPolicy.PrefKey(ContentKind.Dungeon, UploadArtifact.Stats), null);
        if (string.IsNullOrEmpty(probe))
        {
            var migrated = UploadPolicyTable.Migrate(
                _prefs.Get("logUpload.autoUpload", true),
                _prefs.Get("logUpload.uploadReplay", true));
            foreach (var kind in UploadPolicyTable.Kinds)
            foreach (var artifact in UploadPolicyTable.Artifacts)
            {
                _uploadPolicy[kind, artifact] = migrated[kind, artifact];
                _prefs.Set(UploadPolicy.PrefKey(kind, artifact), UploadPolicy.Format(migrated[kind, artifact]));
            }
            _prefs.Save();
            _services.Log.Info("[CombatMeter.SP1] Migrated legacy upload toggles to the per-content upload policy.");
            return;
        }

        foreach (var kind in UploadPolicyTable.Kinds)
        foreach (var artifact in UploadPolicyTable.Artifacts)
            _uploadPolicy[kind, artifact] =
                UploadPolicy.Parse(_prefs.Get<string>(UploadPolicy.PrefKey(kind, artifact), null));
    }

    /// <summary>
    /// Replay has no <c>manual</c> state (owner ruling 2026-07-28; see <c>Plugin.SettingsArchive.cs</c>'s
    /// <c>UploadsSection</c>). It cannot upload on ANY path — the retained re-upload payload takes its
    /// positions from the archive-time doc, which is null under <c>manual</c> — yet <c>manual</c> still
    /// counts as enabled for capture, so a stored <c>manual</c> would sample all run long and ship
    /// nothing. The grid offers only auto/off, so normalise any stray value (a hand-edited config, or a
    /// pref left by an interim build) down to <c>off</c>. That is what makes the UI, the capture gate and
    /// the upload gate agree. <see cref="UploadPolicyTable.Migrate"/> never produces it.
    /// </summary>
    private void NormalizeReplayManualToOff()
    {
        var changed = false;
        foreach (var kind in UploadPolicyTable.Kinds)
        {
            if (_uploadPolicy[kind, UploadArtifact.Replay] != UploadPolicyState.Manual) continue;
            _uploadPolicy[kind, UploadArtifact.Replay] = UploadPolicyState.Off;
            _prefs.Set(UploadPolicy.PrefKey(kind, UploadArtifact.Replay), UploadPolicy.Format(UploadPolicyState.Off));
            changed = true;
            _services.Log.Info(
                $"[CombatMeter.SP1] replay policy for {UploadPolicy.KindKey(kind)} was 'manual', which can never " +
                "upload — normalised to 'off'.");
        }
        if (changed) _prefs.Save();
    }

    // Called on scene change (OnSceneChanged) and after any policy write — nowhere else. Resolves the
    // live scene's kind ONCE so the per-event / per-frame paths read a plain bool. Reading the live
    // ClientState.CurrentSceneName is safe inside OnSceneChanged: ClientStateService assigns it BEFORE
    // raising SceneChanged, so this does not depend on the handler's own _lastSceneName assignment
    // order. An archive fired from that handler is unaffected either way — it resolves ITS kind from
    // the entry's stored scene name, never from this cache.
    private void RecomputeUploadPolicyCache()
    {
        _currentKind = _contentKinds.KindOf(ParseMapId(_services.ClientState.CurrentSceneName));
        // D3: raw-event buffering keeps today's semantics — only when this kind auto-uploads stats.
        _captureForLogEnabled = _uploadPolicy[_currentKind, UploadArtifact.Stats] == UploadPolicyState.Auto;
        // Replay capture is deliberately NOT _currentKind-derived: the upload gate resolves the kind
        // from the ARCHIVED entry's stored scene, and if the two disagree the samples are already lost.
        // A raid lobby / dungeon approach is a different (often unlisted ⇒ `other`) map id from the boss
        // room, and the buffer is deliberately KEPT across that hop (Plugin.History.cs), so a live-kind
        // gate would skip the walk-in and clip the start of the raid's replay — the exact P0 shape; a
        // mid-run policy write would gap the middle the same way. Capture broadly, gate narrowly.
        _replayCaptureEnabled = AnyReplayCellEnabled(_uploadPolicy);
    }

    /// <summary>Running plugin version, used as the kind-map cache key. Null when unreadable, which
    /// <see cref="ContentKindFetcher.NeedsFetch"/> treats as "fetch" rather than pinning a stale map.</summary>
    private static string? CurrentPluginVersion => typeof(Plugin).Assembly.GetName().Version?.ToString();

    // Owner ruling 2026-07-28: fetch ONCE and cache; re-fetch only when the plugin version changes.
    // Steady state is ZERO requests — every request to a Worker route bills an invocation on Cloudflare.
    // See ContentKindFetcher.NeedsFetch for the full rationale and the accepted staleness trade-off.
    private void MaybeRefreshContentKinds()
    {
        if (!ContentKindFetcher.NeedsFetch(
                _prefs.Get<string>(PrefKindMapVersion, null), CurrentPluginVersion, _contentKinds.IsEmpty))
            return;
        FetchContentKinds(userRequested: false);
    }

    /// <summary>Settings-pane "Refresh content list" — fetches unconditionally. The escape hatch for a
    /// content patch that lands without a plugin release. User-initiated, so it cannot run away.</summary>
    internal void RefreshContentKindsNow() => FetchContentKinds(userRequested: true);

    private void FetchContentKinds(bool userRequested)
    {
        // Wall clock, NOT ServerNowMs: the server clock reads 0 until the client has a server time, and
        // this can run at construction. Informational only now — the fetch TRIGGER is the version stamp.
        var now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ContentKindFetcher.FetchFireAndForget(
            LogUploader.ApiBase,
            _prefs.Get<string>(PrefKindMapEtag, null),
            (body, etag) => OnContentKindsFetched(body, etag, now, userRequested),
            msg => _services.Log.Warning(msg));
    }

    // Set by the fetch callback (thread-pool thread) and drained on the Unity main thread
    // (DrainContentKindsNotice, called from OnUpdate) — Notifications must not be poked off-thread, the
    // same rule the upload-status drain follows.
    private volatile string? _contentKindsNotice;
    private volatile bool _contentKindsNoticeOk;

    private void DrainContentKindsNotice()
    {
        var notice = _contentKindsNotice;
        if (notice is null) return;
        _contentKindsNotice = null;
        _services.Notifications.Notify(notice,
            _contentKindsNoticeOk ? NotificationKind.Success : NotificationKind.Warning);
    }

    // Thread-pool thread: only prefs + thread-safe log calls, never uGUI. A null body (304 / failure)
    // keeps the cached map; a parse failure keeps it too, so a bad response can never wipe a good cache.
    private void OnContentKindsFetched(string? body, string? etag, long fetchedAtMs, bool userRequested)
    {
        if (body is null)
        {
            // 304 / unreachable. A manual refresh still owes the user an answer; the automatic path stays quiet.
            if (userRequested) RaiseContentKindsNotice("Content list already up to date.", ok: true);
            return;
        }
        if (!ContentKindMap.TryParse(body, out var map))
        {
            _services.Log.Warning("[CombatMeter.SP1] content-kinds payload unparseable — keeping cached map.");
            if (userRequested) RaiseContentKindsNotice("Content list refresh failed — kept the cached list.", ok: false);
            return;
        }

        _contentKinds = map;
        _prefs.Set(PrefKindMapDungeon,   map.Ids(ContentKind.Dungeon));
        _prefs.Set(PrefKindMapRaid,      map.Ids(ContentKind.Raid));
        _prefs.Set(PrefKindMapWorldBoss, map.Ids(ContentKind.WorldBoss));
        if (!string.IsNullOrEmpty(etag)) _prefs.Set(PrefKindMapEtag, etag);
        _prefs.Set(PrefKindMapFetchedAt, fetchedAtMs);
        // Stamp the version LAST-ish, before Save: this is what suppresses every future fetch until the
        // plugin is updated. Without it the map would be re-fetched on every launch.
        if (CurrentPluginVersion is { } v) _prefs.Set(PrefKindMapVersion, v);
        _prefs.Save();
        RecomputeUploadPolicyCache();
        _services.Log.Info($"[CombatMeter.SP1] content-kinds map updated (cached for plugin {CurrentPluginVersion ?? "?"}).");
        if (userRequested) RaiseContentKindsNotice("Content list updated ✓", ok: true);
    }

    private void RaiseContentKindsNotice(string message, bool ok)
    {
        _contentKindsNoticeOk = ok;
        _contentKindsNotice = message;   // written last: the drain reads the message as the ready flag
    }
}
