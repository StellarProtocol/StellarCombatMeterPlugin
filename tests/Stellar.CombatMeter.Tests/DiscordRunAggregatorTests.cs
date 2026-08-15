using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter;
using Stellar.CombatMeter.LogUpload;
using Xunit;

public class DiscordRunAggregatorTests
{
    private static Plugin.EncounterHistoryEntry Entry(
        string scene, string result, long combatMs, long enteredMs, long archivedMs,
        params (long id, string name, long dmg, long heal, long taken)[] players)
    {
        var e = new Plugin.EncounterHistoryEntry
        {
            SceneName = scene, Result = result, CombatDurationMs = combatMs,
            EnteredAtMs = enteredMs, ArchivedAtMs = archivedMs, LevelUuid = 42,
        };
        foreach (var p in players)
        {
            e.Stats[new EntityId(p.id)] = new SourceStats { TotalDamage = p.dmg, TotalHealing = p.heal, TotalTaken = p.taken };
            e.Entities[new EntityId(p.id)] = new EntitySnapshot { Name = p.name };
        }
        return e;
    }

    [Fact]
    public void Aggregate_sums_across_entries_and_ranks_by_damage()
    {
        var entries = new List<Plugin.EncounterHistoryEntry>
        {
            Entry("1201", "partial", 4000, 1000, 5000, (1, "Void", 400_000, 0, 100_000), (2, "Revette", 300_000, 50_000, 80_000)),
            Entry("1201", "kill",    6000, 5000, 11000, (1, "Void", 800_000, 0, 110_000), (2, "Revette", 680_000, 70_000, 100_000)),
        };
        var s = DiscordRunAggregator.Aggregate(entries);

        Assert.Equal("1201", s.MapName);          // RAW scene name (observer resolves later)
        Assert.Equal("kill", s.Verdict);          // any kill => kill
        Assert.Equal(10000, s.RealDurationMs);    // maxArchived(11000) - minEntered(1000)
        Assert.Equal(10000, s.RunCombatSpanMs);   // 4000 + 6000
        Assert.Null(s.Link);
        Assert.Equal(2, s.Rows.Count);
        Assert.Equal("Void", s.Rows[0].Name);     // 1.2M total > Revette 980k
        Assert.Equal(1_200_000, s.Rows[0].Damage);
        Assert.Equal(0, s.Rows[0].Healing);
        Assert.Equal(210_000, s.Rows[0].Taken);
        Assert.Equal("Revette", s.Rows[1].Name);
        Assert.Equal(980_000, s.Rows[1].Damage);
        Assert.Equal(120_000, s.Rows[1].Healing);
    }

    [Fact]
    public void Aggregate_single_entry_equals_that_entry()
    {
        var s = DiscordRunAggregator.Aggregate(new List<Plugin.EncounterHistoryEntry>
        {
            Entry("30", "kill", 5000, 0, 5000, (7, "Kai", 500_000, 0, 250_000)),
        });
        Assert.Single(s.Rows);
        Assert.Equal("Kai", s.Rows[0].Name);
        Assert.Equal(500_000, s.Rows[0].Damage);
        Assert.Equal(5000, s.RunCombatSpanMs);
    }

    [Fact]
    public void Aggregate_empty_entries_yields_no_rows()
    {
        var s = DiscordRunAggregator.Aggregate(new List<Plugin.EncounterHistoryEntry>());
        Assert.Empty(s.Rows);
        Assert.Equal("partial", s.Verdict);
    }
}
