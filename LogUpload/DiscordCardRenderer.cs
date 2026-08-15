using System;
using UnityEngine;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>Phase-1 SPIKE, take 2: renders the card by drawing directly to a <see cref="Texture2D"/> —
/// NO camera, NO scene render — so it is immune to the game's URP (the offscreen-camera take 1 captured
/// the world/skybox because URP ignores a raw camera's cullingMask/clearFlags). Shapes are filled into a
/// pixel buffer (same idea as the framework's ThemeRenderer); text is rasterised by blitting glyphs from
/// the dynamic font's atlas. Returns null on ANY failure so the caller falls back to the text post. MUST
/// run on the Unity main thread. Heavily logged.</summary>
internal static class DiscordCardRenderer
{
    private static Font? _font;

    internal static byte[]? RenderSpike(string title, string[] lines, Action<string> log)
    {
        Texture2D? card = null;
        try
        {
            const int W = 900;
            int rows = Math.Max(1, lines.Length);
            int H = 60 + rows * 40 + 16;                       // content-sized: header + rows + pad (no empty space)
            var font = ResolveFont(log);
            if (font == null) { log("[CombatMeter.SP1] Card: no font."); return null; }

            const int titleSize = 24, rowSize = 18;
            // Request EVERY glyph we will draw BEFORE reading the atlas (a request can rebuild/resize it).
            font.RequestCharactersInTexture(title, titleSize, FontStyle.Bold);
            foreach (var l in lines) font.RequestCharactersInTexture(l, rowSize, FontStyle.Normal);

            var atlas = ReadAtlas(font, log, out int aw, out int ah);
            if (atlas == null) { log("[CombatMeter.SP1] Card: atlas readback failed."); return null; }

            var buf = new Color32[W * H];                      // y-DOWN logical coords (y=0 at top)
            Fill(buf, W, H, 0, 0, W, H, new Color32(13, 15, 23, 255));            // background
            Fill(buf, W, H, 0, 0, W, 52, new Color32(28, 33, 51, 255));          // header bar
            Fill(buf, W, H, 0, 52, W, 2, new Color32(40, 47, 66, 255));          // header divider

            DrawText(buf, W, H, atlas, aw, ah, font, title, titleSize, FontStyle.Bold, 20, 36, new Color32(245, 247, 252, 255));
            for (int i = 0; i < lines.Length; i++)
                DrawText(buf, W, H, atlas, aw, ah, font, lines[i], rowSize, FontStyle.Normal,
                         20, 52 + 30 + i * 40, new Color32(222, 230, 242, 255));

            card = new Texture2D(W, H, TextureFormat.RGBA32, false);
            FlipRowsInto(card, buf, W, H);                     // buf is y-down; Texture2D is y-up
            card.Apply(false, false);
            var png = ImageConversion.EncodeToPNG(card);
            log($"[CombatMeter.SP1] Card drawn {W}x{H} (atlas {aw}x{ah}) -> {(png?.Length ?? 0)} PNG bytes.");
            return png;
        }
        catch (Exception ex)
        {
            log($"[CombatMeter.SP1] Card draw FAILED: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            if (card != null) UnityEngine.Object.Destroy(card);
        }
    }

    private static Font ResolveFont(Action<string> log)
    {
        if (_font != null) return _font;
        Font? best = null; int bestScore = -1;
        try
        {
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (f == null) continue;
                int score = (f.dynamic ? 100000 : 0) + (f.characterInfo?.Length ?? 0);
                if (score > bestScore) { bestScore = score; best = f; }
            }
        }
        catch (Exception ex) { log($"[CombatMeter.SP1] Font scan threw: {ex.Message}"); }
        _font = best;
        log(best != null ? $"[CombatMeter.SP1] Card font = '{best.name}' (dynamic={best.dynamic})."
                         : "[CombatMeter.SP1] Card font = NONE.");
        return _font!;
    }

    /// <summary>Copies the dynamic font's GPU atlas into a CPU-readable buffer via a camera-less
    /// <see cref="Graphics.Blit"/> (URP-safe) + ReadPixels. Coverage lives in the alpha channel.</summary>
    private static Color32[]? ReadAtlas(Font font, Action<string> log, out int w, out int h)
    {
        w = h = 0;
        var src = font.material != null ? font.material.mainTexture : null;
        if (src == null) { log("[CombatMeter.SP1] Font atlas texture null."); return null; }
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
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
            if (readable != null) UnityEngine.Object.Destroy(readable);
        }
    }

    // Fills a rect in y-down logical coords.
    private static void Fill(Color32[] buf, int W, int H, int x, int y, int w, int h, Color32 c)
    {
        int x1 = Mathf.Clamp(x + w, 0, W), y1 = Mathf.Clamp(y + h, 0, H);
        for (int yy = Mathf.Max(0, y); yy < y1; yy++)
            for (int xx = Mathf.Max(0, x); xx < x1; xx++)
                buf[yy * W + xx] = c;
    }

    // Draws a string left-to-right at pen (penX, baselineY) in y-down logical coords, blitting each glyph
    // from the atlas (alpha = coverage). The atlas is y-UP (uv.y=0 at bottom); the buffer is y-down.
    private static void DrawText(Color32[] buf, int W, int H, Color32[] atlas, int aw, int ah,
                                 Font font, string s, int size, FontStyle style, int penX, int baselineY, Color32 col)
    {
        float x = penX;
        foreach (var ch in s)
        {
            if (!font.GetCharacterInfo(ch, out var ci, size, style)) { x += size * 0.5f; continue; }
            int gl = Mathf.RoundToInt(x + ci.minX);
            int gr = Mathf.RoundToInt(x + ci.maxX);
            int gt = baselineY - ci.maxY;            // glyph top (y-down)
            int gb = baselineY - ci.minY;            // glyph bottom
            int gw = gr - gl, gh = gb - gt;
            if (gw > 0 && gh > 0)
            {
                for (int py = gt; py < gb; py++)
                {
                    if (py < 0 || py >= H) continue;
                    float v = (py - gt) / (float)gh;                       // 0 at glyph top
                    for (int px = gl; px < gr; px++)
                    {
                        if (px < 0 || px >= W) continue;
                        float u = (px - gl) / (float)gw;
                        // bilerp atlas UV (handles Unity's rotated-in-atlas glyphs); top uses uvTop*, bottom uvBottom*
                        var top = Vector2.Lerp(ci.uvTopLeft, ci.uvTopRight, u);
                        var bot = Vector2.Lerp(ci.uvBottomLeft, ci.uvBottomRight, u);
                        var uv = Vector2.Lerp(top, bot, v);
                        int ax = Mathf.Clamp((int)(uv.x * aw), 0, aw - 1);
                        int ay = Mathf.Clamp((int)(uv.y * ah), 0, ah - 1);
                        float cov = atlas[ay * aw + ax].a / 255f;
                        if (cov <= 0.003f) continue;
                        int di = py * W + px;
                        var d = buf[di];
                        buf[di] = new Color32(
                            (byte)(col.r * cov + d.r * (1 - cov)),
                            (byte)(col.g * cov + d.g * (1 - cov)),
                            (byte)(col.b * cov + d.b * (1 - cov)),
                            255);
                    }
                }
            }
            x += ci.advance;
        }
    }

    // Writes the y-down buffer into the y-up texture (flip rows).
    private static void FlipRowsInto(Texture2D tex, Color32[] buf, int W, int H)
    {
        var outp = new Color32[W * H];
        for (int y = 0; y < H; y++)
            Array.Copy(buf, y * W, outp, (H - 1 - y) * W, W);
        tex.SetPixels32(outp);
    }
}
