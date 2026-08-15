using System.Collections.Generic;
using Stellar.CombatMeter.LogUpload;
using Xunit;

public class DiscordMessageBuilderTests
{
    private static DiscordRunSummary Summary(string? link, params DiscordPlayerRow[] rows)
        // RealDurationMs=8300 ("8.3s" title); RunCombatSpanMs=1000 so a per-second DPS equals the
        // damage total (1.2M damage / 1s = 1.2M DPS) — makes the rate math assertion clean.
        => new("Chaotic – Tina's Mindrealm", "kill", 8300, 1000, rows, link);

    [Fact]
    public void Build_includes_title_link_and_table_and_no_mentions()
    {
        var json = DiscordMessageBuilder.Build(Summary(
            "https://revette.io/run/sea/CvCyokazcx",
            new DiscordPlayerRow("Void", 1_200_000, 0, 210_000),
            new DiscordPlayerRow("Revette", 980_000, 120_000, 180_000)));

        Assert.Contains("Chaotic", json);
        Assert.Contains("kill", json);
        Assert.Contains("https://revette.io/run/sea/CvCyokazcx", json);
        Assert.Contains("Void", json);
        Assert.Contains("1.2M", json);   // Plugin.FormatAmount
        Assert.Contains("\\\"parse\\\":[]".Replace("\\\"", "\""), json);  // allowed_mentions parse:[]
    }

    [Fact]
    public void Build_without_link_says_not_uploaded()
    {
        var json = DiscordMessageBuilder.Build(Summary(null, new DiscordPlayerRow("Kai", 500_000, 0, 0)));
        Assert.Contains("Not uploaded", json);
        Assert.DoesNotContain("/run/", json);
    }

    [Fact]
    public void Build_caps_rows_at_MaxRows()
    {
        var rows = new List<DiscordPlayerRow>();
        for (int i = 0; i < 15; i++) rows.Add(new DiscordPlayerRow("P" + i, 1000 - i, 0, 0));
        var json = DiscordMessageBuilder.Build(new DiscordRunSummary("m", "kill", 1000, 1000, rows, null));
        Assert.Contains("P0", json);
        Assert.Contains("P9", json);
        Assert.DoesNotContain("P10", json);   // 11th row dropped
    }

    [Fact]
    public void Build_escapes_quotes_and_newlines_in_names()
    {
        var json = DiscordMessageBuilder.Build(new DiscordRunSummary(
            "map \"x\"", "kill", 1000, 1000,
            new[] { new DiscordPlayerRow("a\"b\nc", 1, 0, 0) }, null));
        Assert.Contains("\\\"", json);        // escaped quote present
        Assert.DoesNotContain("\n\nc", json); // raw newline from the name did not leak a bare LF into a JSON string
    }

    [Fact]
    public void Build_with_zero_combat_span_does_not_divide_by_zero()
    {
        // A heal-only tail archive has CombatDurationMs=0, so RunCombatSpanMs=0 is a reachable input.
        // BuildTable guards with Math.Max(1L, RunCombatSpanMs); this pins that guard.
        var summary = new DiscordRunSummary("map", "kill", 5000, 0,
            new[] { new DiscordPlayerRow("Kai", 500_000, 120_000, 0) }, null);
        var json = DiscordMessageBuilder.Build(summary);
        Assert.Contains("Kai", json);
    }

    [Fact]
    public void Build_puts_name_last_so_cjk_width_cannot_shift_numeric_columns()
    {
        // Discord's code-block font renders CJK between 1 and 2 ASCII cells, so a name-FIRST column can't
        // be aligned by any integer per-glyph width (owner report). Numbers are emitted first
        // (right-aligned, pure ASCII), the name LAST — so a CJK name cannot shift the numeric columns.
        var json = DiscordMessageBuilder.Build(new DiscordRunSummary("m", "kill", 1000, 1000, new[]
        {
            new DiscordPlayerRow("Revette", 543_600, 0, 100),
            new DiscordPlayerRow("巨刃守护者", 829, 0, 3),
        }, null));

        Assert.Contains("  543.6K", json);      // DPS right-aligned in 8 → 2 leading spaces
        Assert.Contains("     829", json);      // DPS right-aligned in 8 → 5 leading spaces
        Assert.Contains("100  Revette", json);  // name follows the Taken value (name is LAST)
        Assert.Contains("3  巨刃守护者", json);    // CJK name is also last, after its Taken value
    }
}
