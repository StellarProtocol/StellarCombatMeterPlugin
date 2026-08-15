// Plugin.DiscordWebhook.cs
using System.Collections.Generic;
using System.Linq;
using Stellar.CombatMeter.LogUpload;

namespace Stellar.CombatMeter;

// Discord webhook on run completion (spec 2026-08-15-combatmeter-discord-webhook-share-design.md).
// Read-only downstream observer: reads already-banked history + already-resolved upload status; NEVER
// mutates archive/verdict/latch/run-id logic. Fires exactly once per run (dedup by levelUuid).
public sealed partial class Plugin
{
    private const string PrefDiscordEnabled = "discord.enabled";
    private const string PrefDiscordUrl     = "discord.webhook.url";
    private const long   DiscordLinkWaitMs  = 20_000;
    private const int    DiscordPostedMax   = 512;

    private bool     _discordEnabled;
    private string   _discordWebhookUrl = "";
    private readonly bool[] _discordContent = new bool[System.Enum.GetValues(typeof(ContentKind)).Length];
    private string   _discordTestResult = "";

    private readonly record struct PendingDiscordPost(long LevelUuid, DiscordRunSummary Summary, long DeadlineMs);
    private readonly List<PendingDiscordPost> _discordPending = new();

    // Keys on levelUuid, which is NOT run-unique in shared-instance content (world-boss instances /
    // mid-dungeon relaunch share a levelUuid — see docs/recon/run-identity-relaunch-split.md). A
    // shared-instance levelUuid therefore posts once per INSTANCE, not once per party's completed run
    // (spec's "once per completed run" choice) — do NOT "fix" this into re-posting.
    private readonly HashSet<long> _discordPosted = new();
    private readonly Queue<long>   _discordPostedFifo = new();

    private static string PrefDiscordContent(ContentKind kind) => "discord.content." + kind;

    internal void LoadDiscordPrefs()
    {
        _discordEnabled    = _prefs.Get(PrefDiscordEnabled, false);
        _discordWebhookUrl = _prefs.Get(PrefDiscordUrl, "") ?? "";
        foreach (ContentKind k in System.Enum.GetValues(typeof(ContentKind)))
            _discordContent[(int)k] = _prefs.Get(PrefDiscordContent(k), k is ContentKind.Dungeon or ContentKind.Raid);
    }

    internal bool DiscordEnabled
    {
        get => _discordEnabled;
        set { _discordEnabled = value; _prefs.Set(PrefDiscordEnabled, value); _prefs.Save(); }
    }

    internal string DiscordWebhookUrl
    {
        get => _discordWebhookUrl;
        set { _discordWebhookUrl = value ?? ""; _prefs.Set(PrefDiscordUrl, _discordWebhookUrl); _prefs.Save(); }
    }

    internal bool DiscordContentFor(ContentKind kind) => _discordContent[(int)kind];

    internal void SetDiscordContent(ContentKind kind, bool on)
    {
        _discordContent[(int)kind] = on;
        _prefs.Set(PrefDiscordContent(kind), on);
        _prefs.Save();
    }

    internal string DiscordTestResult => _discordTestResult;

    internal void SendDiscordTest()
    {
        var probe = new DiscordRunSummary("CombatMeter", "test", 0, 1,
            new[] { new DiscordPlayerRow("Webhook connected ✓", 0, 0, 0) }, null);
        DiscordWebhookPoster.PostFireAndForget(_discordWebhookUrl, DiscordMessageBuilder.Build(probe),
            (ok, status, err) => _discordTestResult = ok ? "Sent ✓" : $"Failed: {(status == 0 ? err : status.ToString())}");
    }

    // Phase-1 card spike: renders a minimal card image IN-GAME (main thread, from the settings button) and
    // posts it as a webhook attachment. Falls back to a text note if the render returns null. This is the
    // in-game validation of the offscreen uGUI→PNG pipeline before the rich v2 layout is built.
    internal void SendDiscordCardTest()
    {
        var purple = new UnityEngine.Color32(167, 139, 250, 255);
        var green = new UnityEngine.Color32(74, 222, 128, 255);
        var rows = new List<CardRow>
        {
            new(1, "Somay", "Moonstrike", purple, true, 1.00f, "51,214", "361.9M", "36%", "1.35M", "1.49M active", "19% / 56%", "5.75M", "", "4.14M", "2"),
            new(2, "Eiori", "Moonstrike", purple, false, 0.967f, "49,704", "353.5M", "35%", "1.32M", "1.32M active", "21% / 55%", "3.61M", "", "3.84M", "2"),
            new(3, "Tampan", "Moonstrike", purple, false, 0.778f, "50,451", "284.5M", "28%", "1.06M", "1.17M active", "33% / 47%", "4.66M", "", "3.79M", "2"),
            new(4, "NatalCharm", "Recovery", green, false, 0.048f, "47,199", "17.5M", "2%", "65.4K", "66.7K active", "45% / 4%", "20.4M", "531K HPS", "4.45M", "1"),
            new(5, "巨刃守护者", "Lifebind", green, false, 0.012f, "46,725", "825.2K", "0%", "3.08K", "3.25K active", "14% / 5%", "67.1M", "251K HPS", "2.92M", "2"),
        };
        var totals = new CardRow(0, "", "", default, false, 0f, "", "1.02B", "", "3.80M", "", "", "86.6M", "", "52.6M", "9");
        var model = new CardModel("Depths of Decay", "MASTER 20", "Forgotten Nightmare  ·  6 players  ·  SEA",
            "CLEAR", green, "04:27", rows, totals, "Stellar CombatMeter", "logs.stellarresonance.app/run/sea/T54ZbOVly");

        var png = DiscordCardRenderer.Render(model, s => _services.Log.Info(s));
        if (png is null) { _discordTestResult = "Card render failed — see log"; return; }
        _discordTestResult = "Rendering… sending card";
        DiscordWebhookPoster.PostImage(_discordWebhookUrl, png,
            "{\"content\":\"Card v2 layout — rendered in-game.\"}",
            onComplete: (ok, status, err) => _discordTestResult = ok ? "Card sent ✓" : $"Card failed: {(status == 0 ? err : status.ToString())}");
    }

    // Read-only observer — called at the END of BankRunBoundary (Plugin.RunBoundary.cs), after the
    // outgoing run is fully banked, with the id captured before _lastRunId was zeroed.
    private void NotifyDiscordRunEnded(long levelUuid)
    {
        if (!_discordEnabled || levelUuid == 0 || _discordPosted.Contains(levelUuid)) return;

        var entries = _history.Where(e => e.LevelUuid == levelUuid).ToList();
        if (entries.Count == 0) return;

        var kind = ResolveKind(entries[^1]);
        if (!_discordContent[(int)kind]) return;

        var summary = DiscordRunAggregator.Aggregate(entries) with { MapName = ResolveSceneName(entries[^1].SceneName) };
        var link = ResolveShareableRunLink(entries);
        var uploadsTerminal = entries.All(e => _uploadStatus.PhaseFor(e) != LogUpload.UploadPhase.InFlight);

        var action = DiscordPostDecision.Decide(true, summary.Rows.Count > 0, false, link is not null, uploadsTerminal);
        if (action == DiscordPostAction.Skip) return;

        MarkDiscordPosted(levelUuid);
        if (action == DiscordPostAction.PostNow) PostDiscord(summary with { Link = link });
        else _discordPending.Add(new PendingDiscordPost(levelUuid, summary, _services.CombatSnapshot.ServerNowMs + DiscordLinkWaitMs));
    }

    private void DrainDiscordPendingPosts()
    {
        if (_discordPending.Count == 0) return;
        var now = _services.CombatSnapshot.ServerNowMs;
        for (int i = _discordPending.Count - 1; i >= 0; i--)
        {
            var p = _discordPending[i];
            var link = ResolveShareableRunLink(_history.Where(e => e.LevelUuid == p.LevelUuid).ToList());
            if (link is not null) { PostDiscord(p.Summary with { Link = link }); _discordPending.RemoveAt(i); }
            else if (now >= p.DeadlineMs) { PostDiscord(p.Summary); _discordPending.RemoveAt(i); }
        }
    }

    private string? ResolveShareableRunLink(List<EncounterHistoryEntry> entries)
        => DiscordLink.PickShareable(entries.Select(e =>
            (e.ArchivedAtMs, _uploadStatus.PhaseFor(e) == LogUpload.UploadPhase.Done, _uploadStatus.UrlFor(e))));

    private void PostDiscord(DiscordRunSummary summary)
        => DiscordWebhookPoster.PostFireAndForget(_discordWebhookUrl, DiscordMessageBuilder.Build(summary),
            (ok, status, err) => _services.Log.Info(
                ok ? $"[CombatMeter.SP1] Discord post OK ({summary.MapName})"
                   : $"[CombatMeter.SP1] Discord post FAILED (HTTP {status}): {err}"));

    private void MarkDiscordPosted(long levelUuid)
    {
        _discordPosted.Add(levelUuid);
        _discordPostedFifo.Enqueue(levelUuid);
        while (_discordPostedFifo.Count > DiscordPostedMax)
            _discordPosted.Remove(_discordPostedFifo.Dequeue());
    }
}
