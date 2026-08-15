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

    [Fact]
    public void Aggregate_verdict_is_last_entry_result_when_no_kill()
    {
        var entries = new List<Plugin.EncounterHistoryEntry>
        {
            Entry("1201", "partial", 3000, 1000, 4000, (1, "Void", 100, 0, 0)),
            Entry("1201", "aborted", 3000, 4000, 8000, (1, "Void", 200, 0, 0)),  // later EnteredAtMs
        };
        var s = DiscordRunAggregator.Aggregate(entries);
        Assert.Equal("aborted", s.Verdict);
    }

    [Fact]
    public void Aggregate_row_name_falls_back_to_entity_id_when_snapshot_name_is_null()
    {
        var e = new Plugin.EncounterHistoryEntry
        {
            SceneName = "1201", Result = "partial", CombatDurationMs = 1000,
            EnteredAtMs = 0, ArchivedAtMs = 1000, LevelUuid = 42,
        };
        e.Entities[new EntityId(9)] = new EntitySnapshot { Name = null };
        e.Stats[new EntityId(9)] = new SourceStats { TotalDamage = 500 };

        var s = DiscordRunAggregator.Aggregate(new List<Plugin.EncounterHistoryEntry> { e });

        Assert.Single(s.Rows);
        Assert.Equal("9", s.Rows[0].Name);
    }
}
