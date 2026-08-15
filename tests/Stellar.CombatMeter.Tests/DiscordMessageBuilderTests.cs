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
}
