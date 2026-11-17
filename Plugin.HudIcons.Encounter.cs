using System;
using UnityEngine;

namespace Stellar.CombatMeter;

// Procedural flat-white glyphs for the header's round Pause / Resume and Archive buttons (approved redesign addendum
// 2026-11-17 "Pause + Archive header icons"). Same recipe as ↺ / ⋯
// (Plugin.HudIcons.cs): a 64 px white SDF shape encoded to PNG once at construction (main thread); the button tints it.
// The archive flash reuses the Ready Check ✓ (_checkmarkPng, Plugin.LocationPinIcon.cs).
//
// Pixel hinting (SDL "pre-draw per size"): the glyphs show at 16 px (HudIconPx), so every straight edge sits on a
// multiple of 4 in the 64 px master — whole pixels at 16 (and 32), no soft grey half-pixel strokes.
//
// The scratch Texture2D only lives inside EncodeGlyphPng: it is HideAndDontSave (a scene unload's
// UnloadUnusedAssets can never reap it mid-encode) and destroyed in `finally`. The framework decodes the PNG into its
// own protected texture, so nothing here outlives the call.
public sealed partial class Plugin
{
    private byte[]? _pausePng, _resumePng, _archivePng;

    // One call from the constructor (keeps Plugin.cs under the 500-LoC cap).
    private void BuildEncounterIcons()
    {
        if (CmDiag.NoNewIcons) return;   // crash-bisect switch (Plugin.Diag.cs): the buttons draw blank
        _pausePng   = EncodeGlyphPng(PauseSdf);
        _resumePng  = EncodeGlyphPng(ResumeSdf);
        _archivePng = EncodeGlyphPng(ArchiveSdf);
        BuildHistoryIcons();                              // History run panel Copy link / Delete (Plugin.HudIcons.History.cs)
    }

    // ‖ — two 2 × 10 px bars (at 16 px) with a 2 px gap.
    private static float PauseSdf(Vector2 p)
        => Mathf.Min(SdfBox(p, 20f, 12f, 28f, 52f), SdfBox(p, 36f, 12f, 44f, 52f));

    // ▶ — right-pointing triangle; the vertical back edge on the 4 px grid.
    private static float ResumeSdf(Vector2 p)
        => SdfTriangle(p, new Vector2(20f, 12f), new Vector2(52f, 32f), new Vector2(20f, 52f));

    // Tray (an open-topped U) with a down arrow dropping into it.
    private static float ArchiveSdf(Vector2 p)
    {
        float tray = Mathf.Min(SdfBox(p, 12f, 8f, 52f, 16f),                          // floor
                     Mathf.Min(SdfBox(p, 12f, 8f, 20f, 32f), SdfBox(p, 44f, 8f, 52f, 32f)));   // walls
        float shaft = SdfBox(p, 28f, 36f, 36f, 56f);
        float head  = SdfTriangle(p, new Vector2(18f, 38f), new Vector2(46f, 38f), new Vector2(32f, 22f));
        return Mathf.Min(tray, Mathf.Min(shaft, head));
    }

    // Signed distance to an axis-aligned box [x0,x1] × [y0,y1] (negative inside).
    private static float SdfBox(Vector2 p, float x0, float y0, float x1, float y1)
    {
        float dx = Mathf.Max(x0 - p.x, p.x - x1), dy = Mathf.Max(y0 - p.y, p.y - y1);
        return new Vector2(Mathf.Max(dx, 0f), Mathf.Max(dy, 0f)).magnitude + Mathf.Min(Mathf.Max(dx, dy), 0f);
    }

    private static byte[]? EncodeGlyphPng(Func<Vector2, float> sdf)
    {
        const int n = 64;
        const float aa = 1.5f;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, mipChain: false) { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - sdf(new Vector2(x + 0.5f, y + 0.5f)) / aa));
            tex.SetPixels(px);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return ImageConversion.EncodeToPNG(tex);
        }
        finally { UnityEngine.Object.Destroy(tex); }
    }
}
