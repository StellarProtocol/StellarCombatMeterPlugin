using UnityEngine;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>Low-level pixel surface for <see cref="DiscordCardRenderer"/>: alpha-blended rect/rounded-rect
/// fills, vertical gradients, and text rasterised by blitting glyphs from a dynamic font's atlas. All
/// coordinates are y-DOWN (y=0 at the TOP); <see cref="Blit"/> flips into the y-up <see cref="Texture2D"/>.
/// No Unity render pipeline is touched — this is pure CPU pixel work (URP-immune).</summary>
internal sealed class Painter
{
    private readonly Color32[] _buf;
    private readonly int _w, _h;
    private readonly Color32[] _atlas;
    private readonly int _aw, _ah;
    private readonly Font _font;

    internal Painter(Color32[] buf, int w, int h, Color32[] atlas, int aw, int ah, Font font)
    {
        _buf = buf; _w = w; _h = h; _atlas = atlas; _aw = aw; _ah = ah; _font = font;
    }

    private void Blend(int idx, Color32 c, float cov)
    {
        float a = c.a / 255f * cov;
        if (a <= 0.003f) return;
        if (a >= 0.999f) { _buf[idx] = new Color32(c.r, c.g, c.b, 255); return; }
        var d = _buf[idx];
        _buf[idx] = new Color32(
            (byte)(c.r * a + d.r * (1 - a)),
            (byte)(c.g * a + d.g * (1 - a)),
            (byte)(c.b * a + d.b * (1 - a)), 255);
    }

    internal void Fill(int x, int y, int w, int h, Color32 c)
    {
        int x1 = Mathf.Clamp(x + w, 0, _w), y1 = Mathf.Clamp(y + h, 0, _h);
        for (int yy = Mathf.Max(0, y); yy < y1; yy++)
            for (int xx = Mathf.Max(0, x); xx < x1; xx++)
                Blend(yy * _w + xx, c, 1f);
    }

    internal void VGradient(int x, int y, int w, int h, Color32 top, Color32 bot)
    {
        int x1 = Mathf.Clamp(x + w, 0, _w), y1 = Mathf.Clamp(y + h, 0, _h);
        for (int yy = Mathf.Max(0, y); yy < y1; yy++)
        {
            float t = (yy - y) / (float)h;
            var c = Color32.Lerp(top, bot, t);
            for (int xx = Mathf.Max(0, x); xx < x1; xx++) Blend(yy * _w + xx, c, 1f);
        }
    }

    internal void Rounded(int x, int y, int w, int h, float r, Color32 c)
    {
        r = Mathf.Min(r, Mathf.Min(w, h) / 2f);
        float cx = x + w / 2f, cy = y + h / 2f, hw = w / 2f, hh = h / 2f;
        int x1 = Mathf.Clamp(x + w, 0, _w), y1 = Mathf.Clamp(y + h, 0, _h);
        for (int yy = Mathf.Max(0, y); yy < y1; yy++)
            for (int xx = Mathf.Max(0, x); xx < x1; xx++)
            {
                float qx = Mathf.Abs(xx + 0.5f - cx) - (hw - r);
                float qy = Mathf.Abs(yy + 0.5f - cy) - (hh - r);
                float d = Mathf.Sqrt(Mathf.Max(qx, 0) * Mathf.Max(qx, 0) + Mathf.Max(qy, 0) * Mathf.Max(qy, 0))
                          + Mathf.Min(Mathf.Max(qx, qy), 0) - r;
                float cov = Mathf.Clamp01(0.5f - d);
                if (cov > 0) Blend(yy * _w + xx, c, cov);
            }
    }

    // Rounded rect filled with a gradient (horizontal → across x, else vertical → down y).
    internal void RoundedGrad(int x, int y, int w, int h, float r, Color32 c0, Color32 c1, bool horizontal)
    {
        r = Mathf.Min(r, Mathf.Min(w, h) / 2f);
        float cx = x + w / 2f, cy = y + h / 2f, hw = w / 2f, hh = h / 2f;
        int x1 = Mathf.Clamp(x + w, 0, _w), y1 = Mathf.Clamp(y + h, 0, _h);
        for (int yy = Mathf.Max(0, y); yy < y1; yy++)
            for (int xx = Mathf.Max(0, x); xx < x1; xx++)
            {
                float qx = Mathf.Abs(xx + 0.5f - cx) - (hw - r);
                float qy = Mathf.Abs(yy + 0.5f - cy) - (hh - r);
                float d = Mathf.Sqrt(Mathf.Max(qx, 0) * Mathf.Max(qx, 0) + Mathf.Max(qy, 0) * Mathf.Max(qy, 0))
                          + Mathf.Min(Mathf.Max(qx, qy), 0) - r;
                float cov = Mathf.Clamp01(0.5f - d);
                if (cov <= 0) continue;
                float t = horizontal ? (xx - x) / (float)w : (yy - y) / (float)h;
                Blend(yy * _w + xx, Color32.Lerp(c0, c1, t), cov);
            }
    }

    internal float Measure(string s, int size, FontStyle style)
    {
        float x = 0;
        foreach (var ch in s)
            if (_font.GetCharacterInfo(ch, out var ci, size, style)) x += ci.advance;
            else x += size * 0.5f;
        return x;
    }

    internal void Text(string s, int size, FontStyle style, int penX, int baselineY, Color32 col)
    {
        float x = penX;
        foreach (var ch in s)
        {
            if (!_font.GetCharacterInfo(ch, out var ci, size, style)) { x += size * 0.5f; continue; }
            int gl = Mathf.RoundToInt(x + ci.minX), gr = Mathf.RoundToInt(x + ci.maxX);
            int gt = baselineY - ci.maxY, gb = baselineY - ci.minY, gw = gr - gl, gh = gb - gt;
            if (gw > 0 && gh > 0)
                for (int py = gt; py < gb; py++)
                {
                    if (py < 0 || py >= _h) continue;
                    float v = (py - gt) / (float)gh;
                    for (int px = gl; px < gr; px++)
                    {
                        if (px < 0 || px >= _w) continue;
                        float u = (px - gl) / (float)gw;
                        var uv = Vector2.Lerp(Vector2.Lerp(ci.uvTopLeft, ci.uvTopRight, u),
                                              Vector2.Lerp(ci.uvBottomLeft, ci.uvBottomRight, u), v);
                        int ax = Mathf.Clamp((int)(uv.x * _aw), 0, _aw - 1);
                        int ay = Mathf.Clamp((int)(uv.y * _ah), 0, _ah - 1);
                        Blend(py * _w + px, col, _atlas[ay * _aw + ax].a / 255f);
                    }
                }
            x += ci.advance;
        }
    }

    internal void TextRight(string s, int size, FontStyle style, int rightX, int baselineY, Color32 col)
        => Text(s, size, style, rightX - Mathf.RoundToInt(Measure(s, size, style)), baselineY, col);

    internal void TextCenter(string s, int size, FontStyle style, int centerX, int baselineY, Color32 col)
        => Text(s, size, style, centerX - Mathf.RoundToInt(Measure(s, size, style) / 2f), baselineY, col);

    // Writes the y-down buffer into the y-up texture (flip rows).
    internal void Blit(Texture2D tex)
    {
        var outp = new Color32[_w * _h];
        for (int y = 0; y < _h; y++) System.Array.Copy(_buf, y * _w, outp, (_h - 1 - y) * _w, _w);
        tex.SetPixels32(outp);
    }
}
