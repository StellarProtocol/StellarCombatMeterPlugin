using System.Text;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>Pure: a <see cref="DiscordRunSummary"/> → the Discord webhook JSON body. One embed, a fenced
/// monospace DPS/HPS/Taken table, and <c>allowed_mentions.parse=[]</c> so it can never ping. No I/O.</summary>
internal static class DiscordMessageBuilder
{
    internal const int MaxRows = 10;

    internal static string Build(DiscordRunSummary s)
    {
        var title = $"{Sanitize(s.MapName)} — {s.Verdict} · {FormatDuration(s.RealDurationMs)}";
        var description = s.Link is not null ? s.Link : "Not uploaded";
        var table = BuildTable(s);

        var sb = new StringBuilder();
        sb.Append("{\"embeds\":[{\"title\":\"").Append(Escape(title))
          .Append("\",\"description\":\"").Append(Escape(description))
          .Append("\",\"fields\":[{\"name\":\"Party\",\"value\":\"").Append(Escape(table))
          .Append("\"}]}],\"allowed_mentions\":{\"parse\":[]}}");
        return sb.ToString();
    }

    private static string BuildTable(DiscordRunSummary s)
    {
        var span = System.Math.Max(1L, s.RunCombatSpanMs);
        var table = new StringBuilder();
        table.Append("#  Name        DPS     HPS     Taken\n");
        int rank = 0;
        foreach (var r in s.Rows)
        {
            if (rank >= MaxRows) break;
            rank++;
            var dps = Plugin.FormatAmount(r.Damage * 1000L / span);
            var hps = Plugin.FormatAmount(r.Healing * 1000L / span);
            var taken = Plugin.FormatAmount(r.Taken);
            table.Append($"{rank,-2} {Pad(Sanitize(r.Name), 11)} {Pad(dps, 7)} {Pad(hps, 7)} {taken}\n");
        }

        return "```\n" + table + "```";
    }

    private static string Sanitize(string? v) => (v ?? "").Replace('\n', ' ').Replace('\r', ' ').Replace("`", "'");

    private static string Pad(string v, int width) => v.Length >= width ? v.Substring(0, width) : v.PadRight(width);

    private static string FormatDuration(long ms)
    {
        if (ms < 0) ms = 0;
        var totalSec = ms / 1000.0;
        if (totalSec < 60) return $"{totalSec:0.#}s";
        var m = (int)(totalSec / 60);
        var sec = (int)(totalSec % 60);
        return $"{m}m {sec}s";
    }

    private static string Escape(string v)
    {
        var sb = new StringBuilder(v.Length + 8);
        foreach (var c in v)
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        return sb.ToString();
    }
}
