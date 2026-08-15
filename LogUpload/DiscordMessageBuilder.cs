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

    // Column display widths (monospace CELLS, not chars — CJK/emoji occupy 2). Name is left-aligned;
    // the numeric columns are right-aligned so magnitudes line up.
    private const int RankW = 2, NameW = 14, NumW = 8;

    private static string BuildTable(DiscordRunSummary s)
    {
        var span = System.Math.Max(1L, s.RunCombatSpanMs);
        var table = new StringBuilder();
        table.Append(Row("#", "Name", "DPS", "HPS", "Taken")).Append('\n');
        int rank = 0;
        foreach (var r in s.Rows)
        {
            if (rank >= MaxRows) break;
            rank++;
            var dps = Plugin.FormatAmount(r.Damage * 1000L / span);
            var hps = Plugin.FormatAmount(r.Healing * 1000L / span);
            table.Append(Row(rank.ToString(), Sanitize(r.Name), dps, hps, Plugin.FormatAmount(r.Taken))).Append('\n');
        }

        return "```\n" + table + "```";
    }

    // One monospace table line. The number columns are right-aligned; the name is left-aligned and
    // truncated by DISPLAY width so a CJK/emoji name (2 cells per glyph in Discord's code-block font)
    // doesn't shove the later columns out of alignment.
    private static string Row(string rank, string name, string dps, string hps, string taken)
        => PadRight(rank, RankW) + " " + PadRight(name, NameW) + " " +
           PadLeft(dps, NumW) + " " + PadLeft(hps, NumW) + " " + PadLeft(taken, NumW);

    private static string Sanitize(string? v) => (v ?? "").Replace('\n', ' ').Replace('\r', ' ').Replace("`", "'");

    // CJK/fullwidth code points render as 2 cells in a monospace code block; astral (surrogate-pair)
    // code points (emoji) are treated as 2 and never split.
    private static bool IsWide(char c)
        => (c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0x303E) || (c >= 0x3041 && c <= 0x33FF)
        || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0xA000 && c <= 0xA4CF)
        || (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F)
        || (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6);

    // Truncate to at most `width` display cells; returns the substring and its actual display width.
    private static (string Text, int Width) TruncateToWidth(string v, int width)
    {
        int w = 0, i = 0;
        while (i < v.Length)
        {
            bool astral = char.IsHighSurrogate(v[i]) && i + 1 < v.Length && char.IsLowSurrogate(v[i + 1]);
            int cw = astral || IsWide(v[i]) ? 2 : 1;
            if (w + cw > width) break;
            w += cw;
            i += astral ? 2 : 1;
        }
        return (i == v.Length ? v : v.Substring(0, i), w);
    }

    private static string PadRight(string v, int width)   // left-align to `width` display cells
    {
        var (text, w) = TruncateToWidth(v, width);
        return w < width ? text + new string(' ', width - w) : text;
    }

    private static string PadLeft(string v, int width)    // right-align to `width` display cells
    {
        var (text, w) = TruncateToWidth(v, width);
        return w < width ? new string(' ', width - w) + text : text;
    }

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
