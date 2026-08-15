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
                    float v = (py - gt + 0.5f) / gh;          // sample at pixel CENTRE
                    for (int px = gl; px < gr; px++)
                    {
                        if (px < 0 || px >= _w) continue;
                        float u = (px - gl + 0.5f) / gw;      // sample at pixel CENTRE
                        var uv = Vector2.Lerp(Vector2.Lerp(ci.uvTopLeft, ci.uvTopRight, u),
                                              Vector2.Lerp(ci.uvBottomLeft, ci.uvBottomRight, u), v);
                        // BILINEAR atlas sample. Nearest-neighbour truncation biased ROTATED wide glyphs
                        // (M/m get packed rotated 90°) ~1 texel down the diagonal → they rendered 1px low.
                        Blend(py * _w + px, col, SampleAtlasA(uv.x, uv.y));
                    }
                }
            x += ci.advance;
        }
    }

    // Bilinear alpha (coverage) sample of the atlas at normalised (u,v). v is y-up (0=bottom), matching
    // GetPixels32 row order. Sub-texel accuracy removes the nearest-neighbour quantisation that shifted
    // rotated glyphs (M/m) by a texel.
    private float SampleAtlasA(float u, float v)
    {
        float fx = u * _aw - 0.5f, fy = v * _ah - 0.5f;
        int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
        float tx = fx - x0, ty = fy - y0;
        int cx0 = Mathf.Clamp(x0, 0, _aw - 1), cy0 = Mathf.Clamp(y0, 0, _ah - 1);
        int cx1 = Mathf.Clamp(x0 + 1, 0, _aw - 1), cy1 = Mathf.Clamp(y0 + 1, 0, _ah - 1);
        float a00 = _atlas[cy0 * _aw + cx0].a, a10 = _atlas[cy0 * _aw + cx1].a;
        float a01 = _atlas[cy1 * _aw + cx0].a, a11 = _atlas[cy1 * _aw + cx1].a;
        float a0 = a00 + (a10 - a00) * tx, a1 = a01 + (a11 - a01) * tx;
        return (a0 + (a1 - a0) * ty) / 255f;
    }

    internal void TextRight(string s, int size, FontStyle style, int rightX, int baselineY, Color32 col)
        => Text(s, size, style, rightX - Mathf.RoundToInt(Measure(s, size, style)), baselineY, col);

    internal void TextCenter(string s, int size, FontStyle style, int centerX, int baselineY, Color32 col)
        => Text(s, size, style, centerX - Mathf.RoundToInt(Measure(s, size, style) / 2f), baselineY, col);

    // Composites an external RGBA icon (px, w×h, y-UP bottom-left origin like GetPixels32) into the
    // y-DOWN card buffer, bilinear-scaled to the dstSize×dstSize box at (dstX,dstY). Uses the icon's own
    // per-pixel alpha. The y flip (source y-up → buffer y-down) happens in the sample coordinate.
    internal void DrawIcon(Color32[] px, int w, int h, int dstX, int dstY, int dstW, int dstH, bool cover = false, float radius = 0f)
    {
        if (px == null || w <= 0 || h <= 0 || dstW <= 0 || dstH <= 0) return;
        // Source region to sample. Default = whole texture (stretch). cover = centred crop matching the
        // dest aspect (fills the box with the middle of a wide/tall source, no distortion, no edge bleed).
        float sx0 = 0f, sy0 = 0f, sw = w, sh = h;
        if (cover)
        {
            float da = (float)dstW / dstH, sa = (float)w / h;
            if (sa > da) { sw = h * da; sx0 = (w - sw) / 2f; }   // source wider → crop width
            else { sh = w / da; sy0 = (h - sh) / 2f; }           // source taller → crop height
        }
        float cx = dstX + dstW / 2f, cy = dstY + dstH / 2f, hw = dstW / 2f, hh = dstH / 2f;
        for (int dy = 0; dy < dstH; dy++)
        {
            int by = dstY + dy; if (by < 0 || by >= _h) continue;
            float sy = sy0 + (1f - (dy + 0.5f) / dstH) * sh - 0.5f;   // y-down dest → y-up source
            for (int dx = 0; dx < dstW; dx++)
            {
                int bx = dstX + dx; if (bx < 0 || bx >= _w) continue;
                float mask = 1f;
                if (radius > 0f)   // rounded-rect corner mask (AA), so a square crest keeps rounded corners
                {
                    float qx = Mathf.Abs(bx + 0.5f - cx) - (hw - radius);
                    float qy = Mathf.Abs(by + 0.5f - cy) - (hh - radius);
                    float d = Mathf.Sqrt(Mathf.Max(qx, 0) * Mathf.Max(qx, 0) + Mathf.Max(qy, 0) * Mathf.Max(qy, 0))
                              + Mathf.Min(Mathf.Max(qx, qy), 0) - radius;
                    mask = Mathf.Clamp01(0.5f - d);
                    if (mask <= 0f) continue;
                }
                float fx = sx0 + (dx + 0.5f) / dstW * sw - 0.5f;
                var c = SampleRgba(px, w, h, fx, sy);
                if (c.a == 0) continue;
                Blend(by * _w + bx, new Color32(c.r, c.g, c.b, 255), c.a / 255f * mask);
            }
        }
    }

    private static Color32 SampleRgba(Color32[] px, int w, int h, float fx, float fy)
    {
        int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
        float tx = fx - x0, ty = fy - y0;
        int cx0 = Mathf.Clamp(x0, 0, w - 1), cy0 = Mathf.Clamp(y0, 0, h - 1);
        int cx1 = Mathf.Clamp(x0 + 1, 0, w - 1), cy1 = Mathf.Clamp(y0 + 1, 0, h - 1);
        Color32 p00 = px[cy0 * w + cx0], p10 = px[cy0 * w + cx1], p01 = px[cy1 * w + cx0], p11 = px[cy1 * w + cx1];
        return new Color32(
            Bi(p00.r, p10.r, p01.r, p11.r, tx, ty), Bi(p00.g, p10.g, p01.g, p11.g, tx, ty),
            Bi(p00.b, p10.b, p01.b, p11.b, tx, ty), Bi(p00.a, p10.a, p01.a, p11.a, tx, ty));
    }

    private static byte Bi(byte a00, byte a10, byte a01, byte a11, float tx, float ty)
    {
        float a0 = a00 + (a10 - a00) * tx, a1 = a01 + (a11 - a01) * tx;
        return (byte)Mathf.Clamp(a0 + (a1 - a0) * ty, 0f, 255f);
    }

    // Writes the y-down buffer into the y-up texture (flip rows).
    internal void Blit(Texture2D tex)
    {
        var outp = new Color32[_w * _h];
        for (int y = 0; y < _h; y++) System.Array.Copy(_buf, y * _w, outp, (_h - 1 - y) * _w, _w);
        tex.SetPixels32(outp);
    }
}
