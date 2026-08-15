using System;
using System.Collections.Generic;
using UnityEngine;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>One player's row on the card. Values are PRE-FORMATTED strings (the caller/DiscordCardData
/// owns number formatting); <see cref="DmgShare"/> in [0,1] drives the damage bar; <see cref="Role"/> is
/// the class accent colour.</summary>
internal sealed record CardRow(
    int Rank, string Name, string Class, Color32 Role, bool Mvp, float DmgShare, string Score,
    string Damage, string DmgPct, string Dps, string Active, string CritLucky,
    string Healing, string Hps, string Taken, string Deaths);

internal sealed record CardModel(
    string Title, string Difficulty, string Sub, string Verdict, Color32 VerdictColor, string Clock,
    IReadOnlyList<CardRow> Rows, CardRow Totals, string Footer, string Link);

/// <summary>Draws the run card directly to a <see cref="Texture2D"/> — NO camera, NO scene render, so it
/// is immune to the game's URP. Shapes are filled into a pixel buffer; text is rasterised by blitting
/// glyphs from the dynamic font's atlas. Returns null on ANY failure so the caller falls back to text.
/// MUST run on the Unity main thread.</summary>
internal static class DiscordCardRenderer
{
    private static Font? _font;

    // Palette
    private static readonly Color32 Bg = new(13, 15, 23, 255);
    private static readonly Color32 HeaderTop = new(30, 36, 53, 255);
    private static readonly Color32 HeaderBot = new(20, 24, 33, 255);
    private static readonly Color32 Divider = new(38, 44, 61, 255);
    private static readonly Color32 RowLine = new(24, 29, 43, 255);
    private static readonly Color32 Primary = new(244, 246, 251, 255);
    private static readonly Color32 Muted = new(139, 147, 167, 255);
    private static readonly Color32 Head = new(121, 134, 160, 255);
    private static readonly Color32 Green = new(94, 240, 143, 255);
    private static readonly Color32 Red = new(255, 123, 123, 255);
    private static readonly Color32 Gold = new(255, 216, 107, 255);
    private static readonly Color32 Silver = new(215, 221, 232, 255);
    private static readonly Color32 Bronze = new(230, 160, 102, 255);
    private static readonly Color32 Accent = new(167, 139, 250, 255);

    // Layout (px). Card width, row/section heights, and the column anchors.
    private const int W = 1300, Pad = 24;
    private const int HeaderH = 84, ColH = 34, RowH = 62, TotalH = 52, FooterH = 40;
    // right-edge x of each right-aligned numeric column
    private const int ScoreR = 470, DmgX = 500, DmgW = 250, DpsR = 900, ClR = 1040, HealR = 1170, TakenR = 1250, DeathR = 1292;
    private const int NameX = 86;

    internal static byte[]? Render(CardModel m, Action<string> log)
    {
        Texture2D? card = null;
        try
        {
            var font = ResolveFont(log);
            if (font == null) return null;
            int H = HeaderH + ColH + m.Rows.Count * RowH + TotalH + FooterH;

            var atlas = ReadAtlasForModel(font, m, out int aw, out int ah, log);
            if (atlas == null) return null;
            var g = new Painter(new Color32[W * H], W, H, atlas, aw, ah, font);

            g.Fill(0, 0, W, H, Bg);
            DrawHeader(g, m);
            int y = HeaderH;
            DrawColumnHeaders(g, y);
            y += ColH;
            for (int i = 0; i < m.Rows.Count; i++) { DrawRow(g, m.Rows[i], y, i); y += RowH; }
            DrawTotals(g, m.Totals, y); y += TotalH;
            DrawFooter(g, m, y);

            card = new Texture2D(W, H, TextureFormat.RGBA32, false);
            g.Blit(card);
            card.Apply(false, false);
            var png = ImageConversion.EncodeToPNG(card);
            log($"[CombatMeter.SP1] Card drawn {W}x{H} ({m.Rows.Count} rows) -> {(png?.Length ?? 0)} PNG bytes.");
            return png;
        }
        catch (Exception ex) { log($"[CombatMeter.SP1] Card draw FAILED: {ex.GetType().Name}: {ex.Message}"); return null; }
        finally { if (card != null) UnityEngine.Object.Destroy(card); }
    }

    private static void DrawHeader(Painter g, CardModel m)
    {
        g.VGradient(0, 0, W, HeaderH, HeaderTop, HeaderBot);
        g.Fill(0, HeaderH - 2, W, 2, Divider);
        g.Text(m.Title, 28, FontStyle.Bold, Pad, 38, Primary);
        float tw = g.Measure(m.Title, 28, FontStyle.Bold);
        if (!string.IsNullOrEmpty(m.Difficulty))
        {
            int bx = Pad + (int)tw + 14, bw = (int)g.Measure(m.Difficulty, 13, FontStyle.Bold) + 20;
            g.Rounded(bx, 20, bw, 24, 7, new Color32(255, 196, 85, 36));
            g.Text(m.Difficulty, 13, FontStyle.Bold, bx + 10, 37, Gold);
        }
        g.Text(m.Sub, 14, FontStyle.Normal, Pad, 66, Muted);
        // verdict pill (right of the clock block)
        int cw = (int)g.Measure(m.Clock, 24, FontStyle.Bold);
        g.TextRight(m.Clock, 24, FontStyle.Bold, W - Pad, 40, Primary);
        g.TextRight("ENCOUNTER", 11, FontStyle.Bold, W - Pad, 62, Head);
        int pw = (int)g.Measure(m.Verdict, 13, FontStyle.Bold) + 24;
        int px = W - Pad - cw - 18 - pw;
        g.Rounded(px, 26, pw, 26, 13, Mul(m.VerdictColor, 0.20f));
        g.Text(m.Verdict, 13, FontStyle.Bold, px + 12, 43, m.VerdictColor);
    }

    private static void DrawColumnHeaders(Painter g, int y)
    {
        int b = y + 22;
        g.Text("#", 11, FontStyle.Bold, Pad, b, Head);
        g.Text("PLAYER", 11, FontStyle.Bold, NameX, b, Head);
        g.TextRight("SCORE", 11, FontStyle.Bold, ScoreR, b, Head);
        g.Text("TOTAL DAMAGE", 11, FontStyle.Bold, DmgX, b, Head);
        g.TextRight("DPS", 11, FontStyle.Bold, DpsR, b, Head);
        g.TextRight("CRIT/LUCKY", 11, FontStyle.Bold, ClR, b, Head);
        g.TextRight("HEAL", 11, FontStyle.Bold, HealR, b, Head);
        g.TextRight("TAKEN", 11, FontStyle.Bold, TakenR, b, Head);
        g.TextRight("DIE", 11, FontStyle.Bold, DeathR, b, Head);
    }

    private static void DrawRow(Painter g, CardRow r, int y, int idx)
    {
        if (idx % 2 == 1) g.Fill(0, y, W, RowH, new Color32(255, 255, 255, 4));
        g.Fill(0, y, W, 1, RowLine);
        int mid = y + 26, sub = y + 46;
        // rank medal for top-3 by damage share order (idx 0..2); else number
        var medal = r.Rank == 1 ? Gold : r.Rank == 2 ? Silver : r.Rank == 3 ? Bronze : (Color32?)null;
        if (medal is { } mc) { g.Rounded(Pad - 2, y + 15, 26, 26, 7, mc); g.TextCenter(r.Rank.ToString(), 13, FontStyle.Bold, Pad + 11, y + 33, Bg); }
        else g.Text(r.Rank.ToString(), 15, FontStyle.Bold, Pad + 2, mid + 3, Head);
        // class accent strip
        g.Rounded(NameX - 16, y + 16, 4, 30, 2, r.Role);
        // name + class + mvp
        g.Text(r.Name, 17, FontStyle.Bold, NameX, mid + 2, Primary);
        float nw = g.Measure(r.Name, 17, FontStyle.Bold);
        g.Text(r.Class, 13, FontStyle.Normal, NameX + (int)nw + 10, mid + 2, Muted);
        if (r.Mvp)
        {
            int mx = NameX + (int)nw + 12 + (int)g.Measure(r.Class, 13, FontStyle.Normal) + 8;
            g.Rounded(mx, y + 18, 40, 18, 5, new Color32(255, 196, 85, 40));
            g.Text("MVP", 10, FontStyle.Bold, mx + 7, y + 31, Gold);
        }
        g.TextRight(r.Score, 15, FontStyle.Normal, ScoreR, mid + 3, new Color32(223, 227, 236, 255));
        // damage bar
        g.Rounded(DmgX, y + 14, DmgW, 34, 6, new Color32(255, 255, 255, 10));
        int fw = Mathf.Clamp((int)(DmgW * r.DmgShare), 2, DmgW);
        g.Rounded(DmgX, y + 14, fw, 34, 6, Mul(r.Role, 0.9f, 90));
        g.Text(r.Damage, 15, FontStyle.Bold, DmgX + 12, mid + 6, Primary);
        g.Text(r.DmgPct, 12, FontStyle.Normal, DmgX + 12 + (int)g.Measure(r.Damage, 15, FontStyle.Bold) + 8, mid + 6, Muted);
        // dps + active
        g.TextRight(r.Dps, 15, FontStyle.Bold, DpsR, mid, Primary);
        g.TextRight(r.Active, 11, FontStyle.Normal, DpsR, sub, Muted);
        g.TextRight(r.CritLucky, 14, FontStyle.Normal, ClR, mid + 2, new Color32(223, 227, 236, 255));
        g.TextRight(r.Healing, 15, FontStyle.Bold, HealR, mid, Green);
        if (!string.IsNullOrEmpty(r.Hps)) g.TextRight(r.Hps, 11, FontStyle.Normal, HealR, sub, Mul(Green, 1f, 190));
        g.TextRight(r.Taken, 15, FontStyle.Normal, TakenR, mid + 2, new Color32(223, 227, 236, 255));
        g.TextRight(r.Deaths, 15, FontStyle.Bold, DeathR, mid + 2, Red);
    }

    private static void DrawTotals(Painter g, CardRow t, int y)
    {
        g.Fill(0, y, W, TotalH, new Color32(124, 92, 255, 16));
        g.Fill(0, y, W, 2, new Color32(44, 51, 70, 255));
        int b = y + 32;
        g.Text("PARTY TOTAL", 12, FontStyle.Bold, Pad, b, Head);
        g.Text(t.Damage, 15, FontStyle.Bold, DmgX, b, Primary);
        g.TextRight(t.Dps, 15, FontStyle.Bold, DpsR, b, Primary);
        g.TextRight(t.Healing, 15, FontStyle.Bold, HealR, b, Green);
        g.TextRight(t.Taken, 15, FontStyle.Bold, TakenR, b, Primary);
        g.TextRight(t.Deaths, 15, FontStyle.Bold, DeathR, b, Red);
    }

    private static void DrawFooter(Painter g, CardModel m, int y)
    {
        g.Fill(0, y, W, 1, RowLine);
        g.Text(m.Footer, 13, FontStyle.Bold, Pad, y + 26, Accent);
        if (!string.IsNullOrEmpty(m.Link)) g.TextRight(m.Link, 13, FontStyle.Normal, W - Pad, y + 26, new Color32(124, 139, 214, 255));
    }

    // ---- font + atlas ----

    private static Font ResolveFont(Action<string> log)
    {
        if (_font != null) return _font;
        Font? best = null; int bestScore = -1;
        try
        {
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
                if (f != null) { int s = (f.dynamic ? 100000 : 0) + (f.characterInfo?.Length ?? 0); if (s > bestScore) { bestScore = s; best = f; } }
        }
        catch (Exception ex) { log($"[CombatMeter.SP1] Font scan threw: {ex.Message}"); }
        _font = best;
        log(best != null ? $"[CombatMeter.SP1] Card font = '{best.name}'." : "[CombatMeter.SP1] Card font = NONE.");
        return _font!;
    }

    private static Color32[]? ReadAtlasForModel(Font font, CardModel m, out int w, out int h, Action<string> log)
    {
        // Request every glyph at every size BEFORE reading the atlas (a request can rebuild it).
        void Req(string s, int sz, FontStyle st) { if (!string.IsNullOrEmpty(s)) font.RequestCharactersInTexture(s, sz, st); }
        foreach (int sz in new[] { 10, 11, 12, 13, 14, 15, 17, 24, 28 })
        {
            Req(m.Title + m.Difficulty + m.Sub + m.Verdict + m.Clock + m.Footer + m.Link +
                "ENCOUNTER PLAYER SCORE TOTAL DAMAGE DPS CRIT/LUCKY HEAL TAKEN DIE PARTY 0123456789.%KMB", sz, FontStyle.Bold);
            Req("MVP HPS active", sz, FontStyle.Normal);
            foreach (var r in m.Rows) { Req(r.Name + r.Class + r.Damage + r.DmgPct + r.Dps + r.Active + r.CritLucky + r.Healing + r.Hps + r.Taken + r.Deaths + r.Score + r.Rank, sz, FontStyle.Bold); Req(r.Name + r.Class + r.Dps + r.Active + r.CritLucky + r.Score, sz, FontStyle.Normal); }
        }
        return ReadAtlas(font, log, out w, out h);
    }

    private static Color32[]? ReadAtlas(Font font, Action<string> log, out int w, out int h)
    {
        w = h = 0;
        var src = font.material != null ? font.material.mainTexture : null;
        if (src == null) { log("[CombatMeter.SP1] Font atlas null."); return null; }
        w = src.width; h = src.height;
        var tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
        var prev = RenderTexture.active;
        Texture2D? readable = null;
        try
        {
            Graphics.Blit(src, tmp);
            RenderTexture.active = tmp;
            readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            readable.Apply(false, false);
            return readable.GetPixels32();
        }
        finally { RenderTexture.active = prev; RenderTexture.ReleaseTemporary(tmp); if (readable != null) UnityEngine.Object.Destroy(readable); }
    }

    private static Color32 Mul(Color32 c, float rgb, byte a = 255)
        => new((byte)(c.r * rgb), (byte)(c.g * rgb), (byte)(c.b * rgb), a);
}
