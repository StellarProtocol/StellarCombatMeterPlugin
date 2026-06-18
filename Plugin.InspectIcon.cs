using UnityEngine;

namespace Stellar.CombatMeter;

/// <summary>
/// Procedural magnifier icon for the Combat History row "Inspect" button (opens the frozen Session Snapshot).
/// Encoded to PNG bytes once at plugin construction and handed to <c>ButtonElement.Icon</c> — the framework
/// rasterises it back to a texture via its icon cache. Replaces the old abstract ○/◉ glyph with the same
/// magnifier affordance the Entity Inspector's profile-card "Inspect" action uses (mirrors
/// <c>Stellar.EntityInspector.Plugin.InspectIcon</c>). A white ring (circle outline) upper-left + a short
/// diagonal handle lower-right, drawn with a soft 1px distance-band edge so it scales cleanly; baked
/// supersampled (4× the on-screen footprint) so the framework's mip downsample stays crisp.
/// </summary>
public sealed partial class Plugin
{
    private const int InspectIconTexPx = 112;   // ~4× the ~28px ColInspect footprint — supersampled

    // Cached PNG bytes (baked once at construction on the main thread); null if the bake failed.
    private byte[]? _inspectIconPng;

    private static byte[]? BuildInspectMagnifierPng()
    {
        var tex = BakeInspectMagnifier();
        try { return ImageConversion.EncodeToPNG(tex); }
        finally { Object.Destroy(tex); }
    }

    // Draw a white magnifying-glass glyph (ring + diagonal handle) to an RGBA32 texture with a soft
    // distance-band edge (antialias). White; alpha is coverage — the framework tints it. Origin bottom-left.
    private static Texture2D BakeInspectMagnifier()
    {
        const int n = InspectIconTexPx;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, mipChain: false);
        var px = new Color[n * n];
        var ringC = new Vector2(n * 0.40f, n * 0.60f);   // ring centre, upper-left
        float ringR = n * 0.26f;                          // ring centre radius
        float ringW = n * 0.075f;                         // ring stroke half-width
        float handW = n * 0.06f;                          // handle half-width
        var handA = new Vector2(ringC.x + ringR * 0.707f, ringC.y - ringR * 0.707f);   // touches ring at 45° down-right
        var handB = new Vector2(n * 0.86f, n * 0.14f);                                   // toward bottom-right corner

        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f);
            float ringD = Mathf.Abs((p - ringC).magnitude - ringR) - ringW;   // signed dist to ring stroke
            float handD = DistToSegment(p, handA, handB) - handW;             // signed dist to handle stroke
            float d = Mathf.Min(ringD, handD);
            const float aa = 1.5f;                                            // edge softness (px)
            float alpha = Mathf.Clamp01(0.5f - d / aa);                       // 1 inside, 0 outside, soft band
            px[y * n + x] = new Color(1f, 1f, 1f, alpha);
        }
        tex.SetPixels(px);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        return tex;
    }

    // Shortest distance from point p to the segment [a,b] (handle stroke skeleton).
    private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.sqrMagnitude;
        float t = len2 <= 1e-4f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return (p - (a + ab * t)).magnitude;
    }
}
