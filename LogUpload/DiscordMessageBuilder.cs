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

    // Column widths in ASCII cells. The NAME is emitted LAST, unpadded: CJK/emoji glyphs do NOT tile
    // cleanly to ASCII cells in Discord's code-block font (a name-first column padded to a 2-cell-per-
    // glyph width still misaligned — owner report 2026-08-15, CJK renders between 1 and 2 cells), so the
    // fixed-width right-aligned numeric columns go BEFORE the name where their alignment can't be shifted.
    private const int RankW = 2, NumW = 8, NameMaxW = 24;

    private static string BuildTable(DiscordRunSummary s)
    {
        var span = System.Math.Max(1L, s.RunCombatSpanMs);
        var table = new StringBuilder();
        table.Append(Row("#", "DPS", "HPS", "Taken", "Name")).Append('\n');
        int rank = 0;
        foreach (var r in s.Rows)
        {
            if (rank >= MaxRows) break;
            rank++;
            var dps = Plugin.FormatAmount(r.Damage * 1000L / span);
            var hps = Plugin.FormatAmount(r.Healing * 1000L / span);
            table.Append(Row(rank.ToString(), dps, hps, Plugin.FormatAmount(r.Taken), Sanitize(r.Name))).Append('\n');
        }

        return "```\n" + table + "```";
    }

    // One monospace table line: rank, right-aligned DPS/HPS/Taken (pure ASCII, so they always align),
    // then the variable-width name LAST so a CJK/emoji name cannot shift the numeric columns.
    private static string Row(string rank, string dps, string hps, string taken, string name)
        => rank.PadRight(RankW) + "  " + dps.PadLeft(NumW) + "  " + hps.PadLeft(NumW) + "  "
         + taken.PadLeft(NumW) + "  " + Clip(name);

    private static string Sanitize(string? v) => (v ?? "").Replace('\n', ' ').Replace('\r', ' ').Replace("`", "'");

    // Length-cap the name by display width (surrogate-safe) so a very long name can't blow up the line;
    // it is the last column, so this never affects the numeric alignment.
    private static string Clip(string name) => TruncateToWidth(name, NameMaxW);

    // Truncate to at most `width` display cells (CJK/emoji = 2), never splitting a surrogate pair.
    private static string TruncateToWidth(string v, int width)
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
        return i == v.Length ? v : v.Substring(0, i);
    }

    private static bool IsWide(char c)
        => (c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0x303E) || (c >= 0x3041 && c <= 0x33FF)
        || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0xA000 && c <= 0xA4CF)
        || (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F)
        || (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6);

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
