using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>One player's row on the card. Values are PRE-FORMATTED strings (the caller/DiscordCardData
/// owns number formatting); <see cref="DmgShare"/> in [0,1] drives the damage bar; <see cref="Role"/> is
/// the class accent colour. <see cref="Icon"/> (when non-null) is the class-crest pixels (RGBA, y-UP
/// bottom-left origin like GetPixels32), <see cref="IconW"/>×<see cref="IconH"/>, blitted before the name.</summary>
internal sealed record CardRow(
    int Rank, string Name, string Class, Color32 Role, bool Mvp, float DmgShare, string Score, string Ibs,
    string Damage, string DmgPct, string Dps, string Active, string CritLucky,
    string ShieldBrk, string MaxHit, string CritDmg, string LuckyDmg,
    string Healing, string Hps, string Taken, string Deaths,
    Color32[]? Icon = null, int IconW = 0, int IconH = 0);

internal sealed record CardModel(
    string Title, string Difficulty, string Sub, string Verdict, Color32 VerdictColor, string Clock,
    IReadOnlyList<CardRow> Rows, CardRow Totals, string Footer, string Link,
    Color32[]? HeaderIcon = null, int HeaderIconW = 0, int HeaderIconH = 0);

/// <summary>Draws the run card directly to a <see cref="Texture2D"/> — NO camera, NO scene render, so it
/// is immune to the game's URP. Shapes are filled into a pixel buffer; text is rasterised by blitting
/// glyphs from the dynamic font's atlas. Returns null on ANY failure so the caller falls back to text.
/// MUST run on the Unity main thread.</summary>
internal static class DiscordCardRenderer
{
    private static Font? _font;
    private static bool _dumped;

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
    private const int W = 1972, Pad = 24;
    private const int HeaderH = 84, ColH = 34, RowH = 62, TotalH = 52, FooterH = 40;
    // right-edge x of each right-aligned numeric column (widened for Ability Score + IBS + the ZDPS columns)
    private const int ScoreR = 480, IbsR = 585, DmgX = 615, DmgW = 250;
    private const int DpsR = 995, ClR = 1135, ShieldR = 1270, MaxHitR = 1405, CritDmgR = 1540, LuckyDmgR = 1675, HealR = 1805, TakenR = 1900, DeathR = 1947;
    private const int NameX = 86;
    // Class-crest icon box (between the accent strip and the name). Space is ALWAYS reserved so names
    // align whether or not a row's icon has finished loading.
    private const int IconSize = 30, IconGap = 10, NameTextX = NameX + IconSize + IconGap;

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

            if (!_dumped)   // one-shot: confirm the M/m rotated-glyph sampling fix; if any residual 1px
            {               // remains, these UVs give the exact geometry to finish it (no guess round-trip).
                _dumped = true;
                foreach (var ch in "MmHox")
                    if (font.GetCharacterInfo(ch, out var ci, 17, FontStyle.Bold))
                    {
                        bool rot = Mathf.Abs(ci.uvTopLeft.x - ci.uvBottomLeft.x) > 1e-5f;
                        log($"[CombatMeter.SP1] glyph '{ch}'@17B mY={ci.minY} MY={ci.maxY} adv={ci.advance} rot={rot} " +
                            $"TL=({ci.uvTopLeft.x:F4},{ci.uvTopLeft.y:F4}) TR=({ci.uvTopRight.x:F4},{ci.uvTopRight.y:F4}) " +
                            $"BL=({ci.uvBottomLeft.x:F4},{ci.uvBottomLeft.y:F4}) BR=({ci.uvBottomRight.x:F4},{ci.uvBottomRight.y:F4})");
                    }
            }

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
        const int bs = 52, by = 16;
        int hx = Pad + bs + 16;
        // Real dungeon crest when we have it (cover-cropped over a dark rounded base so the wide banner
        // fills the square with no gradient bleed); else the gradient placeholder.
        if (m.HeaderIcon != null && m.HeaderIconW > 0 && m.HeaderIconH > 0)
        {
            g.Rounded(Pad, by, bs, bs, 13, HeaderBot);   // dark base
            g.DrawIcon(m.HeaderIcon, m.HeaderIconW, m.HeaderIconH, Pad, by, bs, bs, cover: true, radius: 13f);
        }
        else
            g.RoundedGrad(Pad, by, bs, bs, 13, new Color32(124, 92, 255, 255), new Color32(77, 208, 225, 255), false);
        g.Text(m.Title, 28, FontStyle.Bold, hx, 42, Primary);
        float tw = g.Measure(m.Title, 28, FontStyle.Bold);
        if (!string.IsNullOrEmpty(m.Difficulty))
        {
            int bx = hx + (int)tw + 14, bw = (int)g.Measure(m.Difficulty, 13, FontStyle.Bold) + 20;
            g.Rounded(bx, 24, bw, 24, 7, new Color32(255, 196, 85, 36));
            g.Text(m.Difficulty, 13, FontStyle.Bold, bx + 10, 41, Gold);
        }
        g.Text(m.Sub, 14, FontStyle.Normal, hx, 68, Muted);
        // verdict pill (right of the clock block)
        int cw = (int)g.Measure(m.Clock, 24, FontStyle.Bold);
        g.TextRight(m.Clock, 24, FontStyle.Bold, W - Pad, 42, Primary);
        g.TextRight("ENCOUNTER", 11, FontStyle.Bold, W - Pad, 64, Head);
        int pw = (int)g.Measure(m.Verdict, 13, FontStyle.Bold) + 24;
        int px = W - Pad - cw - 18 - pw;
        g.Rounded(px, 28, pw, 26, 13, Mul(m.VerdictColor, 0.20f));
        g.Text(m.Verdict, 13, FontStyle.Bold, px + 12, 45, m.VerdictColor);
    }

    private static void DrawColumnHeaders(Painter g, int y)
    {
        int b = y + 22;
        g.Text("#", 11, FontStyle.Bold, Pad, b, Head);
        g.Text("PLAYER", 11, FontStyle.Bold, NameX, b, Head);
        g.TextRight("ABILITY SCORE", 11, FontStyle.Bold, ScoreR, b, Head);
        g.TextRight("IBS", 11, FontStyle.Bold, IbsR, b, Head);
        g.Text("TOTAL DAMAGE", 11, FontStyle.Bold, DmgX, b, Head);
        g.TextRight("DPS", 11, FontStyle.Bold, DpsR, b, Head);
        g.TextRight("CRIT/LUCKY", 11, FontStyle.Bold, ClR, b, Head);
        g.TextRight("SHIELD BRK", 11, FontStyle.Bold, ShieldR, b, Head);
        g.TextRight("MAX HIT", 11, FontStyle.Bold, MaxHitR, b, Head);
        g.TextRight("CRIT DMG", 11, FontStyle.Bold, CritDmgR, b, Head);
        g.TextRight("LUCKY DMG", 11, FontStyle.Bold, LuckyDmgR, b, Head);
        g.TextRight("HEAL", 11, FontStyle.Bold, HealR, b, Head);
        g.TextRight("TAKEN", 11, FontStyle.Bold, TakenR, b, Head);
        g.TextRight("DIE", 11, FontStyle.Bold, DeathR, b, Head);
    }

    private static void DrawRow(Painter g, CardRow r, int y, int idx)
    {
        if (idx % 2 == 1) g.Fill(0, y, W, RowH, new Color32(255, 255, 255, 4));
        g.Fill(0, y, W, 1, RowLine);
        // Vertical layout is centred on the row: blocks (medal/accent/icon/bar) centre on yc; a single
        // line of text sits on bl (= centred); sub-lines (active / HPS) hang below on sl. This stops the
        // old top-heavy look where single-line cells left a big empty band below them.
        int yc = y + RowH / 2;           // row vertical CENTRE
        int bl = yc + 6;                 // single-line baseline (a lone line is centred on the row)
        int blPair = yc - 4;             // VALUE baseline for a value+sub pair (DPS / HPS) — the PAIR is
        int sl = yc + 15;                // centred (the old look), so a 2-line cell doesn't sit low
        var val = new Color32(223, 227, 236, 255);
        // rank medal for top-3; else number
        var medal = r.Rank == 1 ? Gold : r.Rank == 2 ? Silver : r.Rank == 3 ? Bronze : (Color32?)null;
        if (medal is { } mc) { g.Rounded(Pad - 2, yc - 13, 26, 26, 7, mc); g.TextCenter(r.Rank.ToString(), 13, FontStyle.Bold, Pad + 11, yc + 5, Bg); }
        else g.Text(r.Rank.ToString(), 15, FontStyle.Bold, Pad + 2, bl, Head);
        g.Rounded(NameX - 16, yc - 16, 4, 32, 2, r.Role);              // class accent strip (centred)
        if (r.Icon != null && r.IconW > 0 && r.IconH > 0)
            g.DrawIcon(r.Icon, r.IconW, r.IconH, NameX, yc - 15, IconSize, IconSize);   // class crest (centred)
        g.Text(r.Name, 17, FontStyle.Bold, NameTextX, bl, Primary);
        float nw = g.Measure(r.Name, 17, FontStyle.Bold);
        g.Text(r.Class, 13, FontStyle.Normal, NameTextX + (int)nw + 10, bl, Muted);
        if (r.Mvp)
        {
            int mx = NameTextX + (int)nw + 12 + (int)g.Measure(r.Class, 13, FontStyle.Normal) + 8;
            g.Rounded(mx, yc - 9, 40, 18, 5, new Color32(255, 196, 85, 40));
            g.Text("MVP", 10, FontStyle.Bold, mx + 7, yc + 4, Gold);
        }
        g.TextRight(r.Score, 15, FontStyle.Normal, ScoreR, bl, val);
        g.TextRight(r.Ibs, 15, FontStyle.Normal, IbsR, bl, val);
        // damage bar (centred on the row) + value/percent on it
        g.Rounded(DmgX, yc - 16, DmgW, 32, 6, new Color32(255, 255, 255, 12));
        int fw = Mathf.Clamp((int)(DmgW * r.DmgShare), 3, DmgW);
        g.RoundedGrad(DmgX, yc - 16, fw, 32, 6,
            new Color32(r.Role.r, r.Role.g, r.Role.b, 225), new Color32(r.Role.r, r.Role.g, r.Role.b, 95), true);
        g.Text(r.Damage, 15, FontStyle.Bold, DmgX + 12, bl, Primary);
        g.Text(r.DmgPct, 12, FontStyle.Normal, DmgX + 12 + (int)g.Measure(r.Damage, 15, FontStyle.Bold) + 8, bl, Muted);
        g.TextRight(r.Dps, 15, FontStyle.Bold, DpsR, blPair, Primary);   // always has an "active" sub → pair
        g.TextRight(r.Active, 11, FontStyle.Normal, DpsR, sl, Muted);
        g.TextRight(r.CritLucky, 14, FontStyle.Normal, ClR, bl, val);
        g.TextRight(r.ShieldBrk, 15, FontStyle.Normal, ShieldR, bl, val);
        g.TextRight(r.MaxHit, 15, FontStyle.Normal, MaxHitR, bl, val);
        g.TextRight(r.CritDmg, 15, FontStyle.Normal, CritDmgR, bl, val);
        g.TextRight(r.LuckyDmg, 15, FontStyle.Normal, LuckyDmgR, bl, val);
        int healBl = string.IsNullOrEmpty(r.Hps) ? bl : blPair;         // pair only when there's an HPS sub
        g.TextRight(r.Healing, 15, FontStyle.Bold, HealR, healBl, Green);
        if (!string.IsNullOrEmpty(r.Hps)) g.TextRight(r.Hps, 11, FontStyle.Normal, HealR, sl, Mul(Green, 1f, 190));
        g.TextRight(r.Taken, 15, FontStyle.Normal, TakenR, bl, val);
        g.TextRight(r.Deaths, 15, FontStyle.Bold, DeathR, bl, Red);
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
        var sb = new System.Text.StringBuilder();
        void A(string? s) { if (!string.IsNullOrEmpty(s)) sb.Append(s).Append(' '); }
        A(m.Title); A(m.Difficulty); A(m.Sub); A(m.Verdict); A(m.Clock); A(m.Footer); A(m.Link);
        foreach (var r in m.Rows)
        {
            A(r.Name); A(r.Class); A(r.Score); A(r.Ibs); A(r.Damage); A(r.DmgPct); A(r.Dps);
            A(r.Active); A(r.CritLucky); A(r.ShieldBrk); A(r.MaxHit); A(r.CritDmg); A(r.LuckyDmg);
            A(r.Healing); A(r.Hps); A(r.Taken); A(r.Deaths); A(r.Rank.ToString());
        }
        var t = m.Totals;
        A(t.Damage); A(t.Dps); A(t.Healing); A(t.Taken); A(t.Deaths);
        sb.Append("ENCOUNTER PLAYER ABILITY SCORE IBS TOTAL DAMAGE DPS CRIT/LUCKY SHIELD BRK MAX HIT CRIT DMG LUCKY DMG HEAL TAKEN DIE PARTY MVP HPS active # ");
        sb.Append("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.,%/:·- ");
        var all = sb.ToString();
        // EVERY glyph at EVERY size in BOTH styles must be requested BEFORE the readback — a request can
        // rebuild/resize the atlas and invalidate earlier UVs. (The garbled sub-line/footer link were
        // Normal-style glyphs that were only requested in Bold, so they were dropped at draw time.)
        foreach (int sz in new[] { 10, 11, 12, 13, 14, 15, 17, 24, 28 })
        {
            font.RequestCharactersInTexture(all, sz, FontStyle.Bold);
            font.RequestCharactersInTexture(all, sz, FontStyle.Normal);
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

    // Reads a class-crest (or any icon) out of its atlas texture into a small RGBA buffer sized to the
    // icon's native pixels. URP-safe (Graphics.Blit is a fullscreen shader blit, NOT a scene render — the
    // same path ReadAtlas uses). The atlas is read y-UP (GetPixels32 / ReadPixels order) and the uv
    // sub-rect is extracted with the SAME bottom-left-origin uv convention the font atlas is sampled with
    // (Painter.Text), so orientation matches without a per-platform flip guess. Returns null on any
    // failure (handle not a texture, empty, exception) → the row just omits the crest.
    internal static Color32[]? ReadIconRegion(object? handle, UvRect uv, out int w, out int h)
    {
        w = h = 0;
        if (handle is not Texture tex) return null;
        int aw = tex.width, ah = tex.height;
        if (aw <= 0 || ah <= 0) return null;
        var tmp = RenderTexture.GetTemporary(aw, ah, 0, RenderTextureFormat.ARGB32);
        var prev = RenderTexture.active;
        Texture2D? readable = null;
        try
        {
            Graphics.Blit(tex, tmp);
            RenderTexture.active = tmp;
            readable = new Texture2D(aw, ah, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, aw, ah), 0, 0);
            readable.Apply(false, false);
            var atlas = readable.GetPixels32();
            int iw = Mathf.Max(1, Mathf.RoundToInt(uv.W * aw));
            int ih = Mathf.Max(1, Mathf.RoundToInt(uv.H * ah));
            var icon = new Color32[iw * ih];
            for (int y = 0; y < ih; y++)
                for (int x = 0; x < iw; x++)
                {
                    float u = uv.X + (x + 0.5f) / iw * uv.W;
                    float v = uv.Y + (y + 0.5f) / ih * uv.H;
                    int ax = Mathf.Clamp((int)(u * aw), 0, aw - 1);
                    int ay = Mathf.Clamp((int)(v * ah), 0, ah - 1);
                    icon[y * iw + x] = atlas[ay * aw + ax];
                }
            w = iw; h = ih;
            return icon;
        }
        catch { return null; }
        finally { RenderTexture.active = prev; RenderTexture.ReleaseTemporary(tmp); if (readable != null) UnityEngine.Object.Destroy(readable); }
    }

    private static Color32 Mul(Color32 c, float rgb, byte a = 255)
        => new((byte)(c.r * rgb), (byte)(c.g * rgb), (byte)(c.b * rgb), a);
}
