using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>Pure: folds a run's banked history entries into one party summary. Sums each player's
/// damage/healing/taken across the run's archives; the linked run page stays the authoritative merge.
/// MapName is left RAW (the observer resolves it) and Link null (the observer attaches it).</summary>
internal static class DiscordRunAggregator
{
    internal static DiscordRunSummary Aggregate(IReadOnlyList<Plugin.EncounterHistoryEntry> entries)
    {
        if (entries.Count == 0)
            return new DiscordRunSummary("", "partial", 0, 0, Array.Empty<DiscordPlayerRow>(), null);

        var ordered = entries.OrderBy(e => e.EnteredAtMs).ToList();
        var runCombatSpanMs = ordered.Sum(e => e.CombatDurationMs);
        var realDurationMs = ordered.Max(e => e.ArchivedAtMs) - ordered.Min(e => e.EnteredAtMs);
        var verdict = ordered.Any(e => e.Result == "kill") ? "kill" : ordered[^1].Result;
        var mapName = ordered.FirstOrDefault(e => !string.IsNullOrEmpty(e.SceneName))?.SceneName ?? "";

        var rows = AggregateRows(ordered);

        return new DiscordRunSummary(mapName, verdict, realDurationMs, runCombatSpanMs, rows, null);
    }

    // Player set = union of Entities keys (the frozen per-PLAYER snapshots) — filters out NPC sources.
    private static List<DiscordPlayerRow> AggregateRows(List<Plugin.EncounterHistoryEntry> ordered)
    {
        var damage = new Dictionary<EntityId, long>();
        var healing = new Dictionary<EntityId, long>();
        var taken = new Dictionary<EntityId, long>();
        var names = new Dictionary<EntityId, string>();

        foreach (var e in ordered)
            foreach (var id in e.Entities.Keys)
            {
                if (!names.ContainsKey(id))
                    names[id] = e.Entities.TryGetValue(id, out var snap) && !string.IsNullOrEmpty(snap.Name)
                        ? snap.Name! : id.Value.ToString();
                if (e.Stats.TryGetValue(id, out var s))
                {
                    damage[id] = damage.GetValueOrDefault(id) + s.TotalDamage;
                    healing[id] = healing.GetValueOrDefault(id) + s.TotalHealing;
                    taken[id] = taken.GetValueOrDefault(id) + s.TotalTaken;
                }
            }

        return names.Keys
            .Select(id => new DiscordPlayerRow(names[id], damage.GetValueOrDefault(id), healing.GetValueOrDefault(id), taken.GetValueOrDefault(id)))
            .OrderByDescending(r => r.Damage)
            .ToList();
    }
}
