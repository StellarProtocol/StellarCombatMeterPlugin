// Plugin.DiscordWebhook.cs
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain.GameData;
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

    // Renders the run card IN-GAME (main thread, from the settings button) from your latest banked run's
    // REAL stats (falls back to the sample if no run yet) and posts it as a webhook attachment.
    internal void SendDiscordCardTest()
    {
        var model = BuildCardFromLatestRun() ?? SampleCardModel();
        var png = DiscordCardRenderer.Render(model, s => _services.Log.Info(s));
        if (png is null) { _discordTestResult = "Card render failed — see log"; return; }
        _discordTestResult = "Rendering… sending card";
        DiscordWebhookPoster.PostImage(_discordWebhookUrl, png,
            "{\"content\":\"Run card — rendered in-game.\"}",
            onComplete: (ok, status, err) => _discordTestResult = ok ? "Card sent ✓" : $"Card failed: {(status == 0 ? err : status.ToString())}");
    }

    private sealed class CardAgg
    {
        public long Dmg, Heal, Taken, Top, First, Last, Fp, Prof;
        public int Hits, Crits, Luckys, Deaths;
        public string Name = "";
    }

    // Builds the card model from the most-recent banked run's REAL per-player stats; null if no run yet.
    private CardModel? BuildCardFromLatestRun()
    {
        if (_history.Count == 0) return null;
        long runId = _history[^1].LevelUuid;
        var entries = _history.Where(e => e.LevelUuid == runId).OrderBy(e => e.EnteredAtMs).ToList();
        if (entries.Count == 0) return null;

        var agg = new Dictionary<long, CardAgg>();
        foreach (var e in entries)
            foreach (var kv in e.Entities)
            {
                if (!agg.TryGetValue(kv.Key.Value, out var a)) { a = new CardAgg(); agg[kv.Key.Value] = a; }
                if (string.IsNullOrEmpty(a.Name)) a.Name = string.IsNullOrEmpty(kv.Value.Name) ? kv.Key.Value.ToString() : kv.Value.Name!;
                if (kv.Value.FightPoint > a.Fp) a.Fp = kv.Value.FightPoint;
                if (a.Prof == 0 && kv.Value.ClassSpanProf.Length > 0) a.Prof = kv.Value.ClassSpanProf[^1];
                if (e.Stats.TryGetValue(kv.Key, out var s))
                {
                    a.Dmg += s.TotalDamage; a.Heal += s.TotalHealing; a.Taken += s.TotalTaken;
                    a.Hits += s.Hits; a.Crits += s.Crits; a.Luckys += s.Luckys; a.Deaths += s.Deaths;
                    if (s.TopHit > a.Top) a.Top = s.TopHit;
                    if (s.FirstHitMs > 0) a.First = a.First == 0 ? s.FirstHitMs : System.Math.Min(a.First, s.FirstHitMs);
                    if (s.LastHitMs > a.Last) a.Last = s.LastHitMs;
                }
            }
        if (agg.Count == 0) return null;

        long combat = System.Math.Max(1, entries.Sum(e => e.CombatDurationMs));
        long totalDmg = agg.Values.Sum(a => a.Dmg);
        long maxDmg = System.Math.Max(1, agg.Values.Max(a => a.Dmg));

        var rows = new List<CardRow>();
        int rank = 0;
        foreach (var a in agg.Values.OrderByDescending(a => a.Dmg))
        {
            rank++;
            int prof = (int)a.Prof, parent = RoleClassifier.ParentProfession(prof);
            var role = RoleClassifier.Classify(parent != 0 ? parent : prof);
            var color = role == Role.Healer ? new UnityEngine.Color32(74, 222, 128, 255)
                      : role == Role.Tank ? new UnityEngine.Color32(77, 160, 225, 255)
                      : new UnityEngine.Color32(167, 139, 250, 255);
            string cls = ProfessionSpecs.Name(prof) is { Length: > 0 } n ? n : role.ToString();
            _services.Log.Info($"[CombatMeter.SP1] card player '{a.Name}' prof={prof} parent={parent} role={role} class='{cls}'");
            long active = System.Math.Max(1, a.Last - a.First);
            int critPct = a.Hits > 0 ? (int)System.Math.Round(a.Crits * 100.0 / a.Hits) : 0;
            int luckyPct = a.Hits > 0 ? (int)System.Math.Round(a.Luckys * 100.0 / a.Hits) : 0;
            rows.Add(new CardRow(rank, a.Name, cls, color, rank == 1, (float)(a.Dmg / (double)maxDmg),
                a.Fp.ToString("N0"), FormatAmount(a.Dmg), totalDmg > 0 ? $"{a.Dmg * 100 / totalDmg}%" : "0%",
                FormatAmount(a.Dmg * 1000 / combat), FormatAmount(a.Dmg * 1000 / active) + " active",
                $"{critPct}% / {luckyPct}%", FormatAmount(a.Heal),
                a.Heal > 0 ? FormatAmount(a.Heal * 1000 / combat) + " HPS" : "",
                FormatAmount(a.Taken), a.Deaths.ToString()));
            if (rows.Count >= 10) break;
        }

        var totals = new CardRow(0, "", "", default, false, 0f, "", FormatAmount(totalDmg), "",
            FormatAmount(totalDmg * 1000 / combat), "", "", FormatAmount(agg.Values.Sum(a => a.Heal)), "",
            FormatAmount(agg.Values.Sum(a => a.Taken)), agg.Values.Sum(a => a.Deaths).ToString());

        var p = entries[^1];
        string verdict = p.Result == "kill" ? "CLEAR" : "PARTIAL";
        var vColor = p.Result == "kill" ? new UnityEngine.Color32(74, 222, 128, 255) : new UnityEngine.Color32(255, 196, 85, 255);
        string region = _services.GameEnvironment.Region.ToString().ToUpperInvariant();
        long realMs = System.Math.Max(0, entries[^1].ArchivedAtMs - entries[0].EnteredAtMs);
        string link = ResolveShareableRunLink(entries) is { } url
            ? url.Replace("https://", "").Replace("http://", "") : "logs.stellarresonance.app";
        return new CardModel(ResolveSceneName(p.SceneName), "", $"{agg.Count} players  ·  {region}",
            verdict, vColor, FormatClock(realMs), rows, totals, "Stellar CombatMeter", link);
    }

    private static string FormatClock(long ms) { long s = System.Math.Max(0, ms) / 1000; return $"{s / 60:00}:{s % 60:00}"; }

    private static CardModel SampleCardModel()
    {
        var purple = new UnityEngine.Color32(167, 139, 250, 255);
        var green = new UnityEngine.Color32(74, 222, 128, 255);
        var rows = new List<CardRow>
        {
            new(1, "Somay", "Moonstrike", purple, true, 1.00f, "51,214", "361.9M", "36%", "1.35M", "1.49M active", "19% / 56%", "5.75M", "", "4.14M", "2"),
            new(2, "Eiori", "Moonstrike", purple, false, 0.967f, "49,704", "353.5M", "35%", "1.32M", "1.32M active", "21% / 55%", "3.61M", "", "3.84M", "2"),
            new(5, "巨刃守护者", "Lifebind", green, false, 0.012f, "46,725", "825.2K", "0%", "3.08K", "3.25K active", "14% / 5%", "67.1M", "251K HPS", "2.92M", "2"),
        };
        var totals = new CardRow(0, "", "", default, false, 0f, "", "1.02B", "", "3.80M", "", "", "86.6M", "", "52.6M", "9");
        return new CardModel("Depths of Decay", "MASTER 20", "6 players  ·  SEA",
            "CLEAR", green, "04:27", rows, totals, "Stellar CombatMeter", "logs.stellarresonance.app/run/sea/T54ZbOVly");
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
