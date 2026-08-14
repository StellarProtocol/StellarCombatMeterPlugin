using System.Collections.Generic;
using Xunit;

namespace Stellar.CombatMeter.Tests;

/// <summary>
/// Local history: tunable retention + the search-box filter (owner 2026-08-15, spec
/// docs/superpowers/specs/2026-08-15-combatmeter-history-search-and-retention-design.md). Retention is a
/// setting clamped to [50, 250]; the search box filters grouped run rows by a case-insensitive substring
/// over "mapName verdict clock".
/// </summary>
public class HistorySearchAndRetentionTests
{
    // ── Retention clamp ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClampRetention_holds_the_bounds()
    {
        Assert.Equal(50,  Plugin.ClampRetention(0));     // below min → min (never below the old fixed cap)
        Assert.Equal(50,  Plugin.ClampRetention(49));
        Assert.Equal(50,  Plugin.ClampRetention(50));
        Assert.Equal(100, Plugin.ClampRetention(100));   // default lives inside the range, untouched
        Assert.Equal(250, Plugin.ClampRetention(250));
        Assert.Equal(250, Plugin.ClampRetention(1000));  // above max → max (config-size ceiling)
    }

    // ── TrimToCapacity now takes the capacity ───────────────────────────────────────────────────────

    [Fact]
    public void TrimToCapacity_caps_at_the_passed_capacity()
    {
        var history = new List<Plugin.EncounterHistoryEntry>();
        for (var i = 0; i < 300; i++) history.Add(new Plugin.EncounterHistoryEntry { MemberCount = i });

        var evicted = Plugin.TrimToCapacity(history, 250);

        Assert.Equal(250, history.Count);   // keeps 250, not 50
        Assert.Equal(50, history[0].MemberCount);   // oldest 0..49 evicted
        Assert.Equal(299, history[^1].MemberCount);
        Assert.Equal(50, evicted.Count);
    }

    // ── Search filter ───────────────────────────────────────────────────────────────────────────────

    private const string Row = "Chaotic - Tina's Mindrealm kill 11:53p";   // "mapName verdict clock"

    [Fact]
    public void An_empty_or_whitespace_query_matches_everything()
    {
        Assert.True(Plugin.HistoryRowMatches(Row, ""));
        Assert.True(Plugin.HistoryRowMatches(Row, "   "));
        Assert.True(Plugin.HistoryRowMatches(Row, null!));
    }

    [Fact]
    public void A_query_matches_map_verdict_or_clock_case_insensitively()
    {
        Assert.True(Plugin.HistoryRowMatches(Row, "tina"));    // map, lower-case
        Assert.True(Plugin.HistoryRowMatches(Row, "MIND"));    // map, upper-case
        Assert.True(Plugin.HistoryRowMatches(Row, "kill"));    // verdict
        Assert.True(Plugin.HistoryRowMatches(Row, "11:5"));    // clock
    }

    [Fact]
    public void A_non_matching_query_excludes_the_row()
    {
        Assert.False(Plugin.HistoryRowMatches(Row, "cursed"));
        Assert.False(Plugin.HistoryRowMatches(Row, "partial"));   // this row is a kill
    }

    [Fact]
    public void The_query_is_trimmed_before_matching()
        => Assert.True(Plugin.HistoryRowMatches(Row, "  tina  "));
}
