using UnityEngine;

namespace Stellar.CombatMeter;

/// <summary>
/// Procedural flat-white location-pin icon for the Marking button in the header bar.
/// Drawn as a filled circle (pin head) + downward triangle tail, white fill, soft AA edge.
/// Baked at 2× the on-screen footprint so it stays crisp after the framework's downsample.
/// </summary>
public sealed partial class Plugin
{
    private const int PinIconTexPx = 64;

    private byte[]? _locationPinPng;

    private static byte[]? BuildLocationPinPng()
    {
        const int n = PinIconTexPx;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, mipChain: false);
        var px  = new Color[n * n];

        // Pin circle: upper portion of the icon
        var circleC = new Vector2(n * 0.50f, n * 0.63f);
        float circleR = n * 0.29f;

        // Pin tail: downward-pointing triangle below the circle
        var   apex      = new Vector2(n * 0.50f, n * 0.08f);
        float tailHalfW = n * 0.16f;
        float tailBaseY = circleC.y - circleR * 0.25f;
        var   triB      = new Vector2(circleC.x - tailHalfW, tailBaseY);
        var   triC      = new Vector2(circleC.x + tailHalfW, tailBaseY);

        const float aa = 1.5f;

        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var   p       = new Vector2(x + 0.5f, y + 0.5f);
            float dCircle = (p - circleC).magnitude - circleR;
            float dTail   = SdfTriangle(p, apex, triB, triC);
            float d       = Mathf.Min(dCircle, dTail);
            float alpha   = Mathf.Clamp01(0.5f - d / aa);
            px[y * n + x] = new Color(1f, 1f, 1f, alpha);
        }

        tex.SetPixels(px);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        try   { return ImageConversion.EncodeToPNG(tex); }
        finally { Object.Destroy(tex); }
    }

    // ── Checkmark icon ───────────────────────────────────────────────────────

    private byte[]? _checkmarkPng;

    private static byte[]? BuildCheckmarkPng()
    {
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, mipChain: false);
        var px  = new Color[n * n];

        // Two segments forming a ✓: left foot → valley → top-right tip
        var foot   = new Vector2(n * 0.14f, n * 0.50f);
        var valley = new Vector2(n * 0.38f, n * 0.18f);
        var tip    = new Vector2(n * 0.84f, n * 0.76f);
        float strokeW = n * 0.09f;
        const float aa = 1.5f;

        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var   p     = new Vector2(x + 0.5f, y + 0.5f);
            float dL    = DistToSegment(p, foot, valley) - strokeW;
            float dR    = DistToSegment(p, valley, tip)  - strokeW;
            float d     = Mathf.Min(dL, dR);
            float alpha = Mathf.Clamp01(0.5f - d / aa);
            px[y * n + x] = new Color(1f, 1f, 1f, alpha);
        }

        tex.SetPixels(px);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        try   { return ImageConversion.EncodeToPNG(tex); }
        finally { Object.Destroy(tex); }
    }

    // ── Megaphone icon (Convene) ─────────────────────────────────────────────

    private byte[]? _megaphonePng;

    private static byte[]? BuildMegaphonePng()
    {
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, mipChain: false);
        var px  = new Color[n * n];
        const float aa = 1.5f;

        // Body: trapezoid wider on right (mouthpiece), narrower on left
        var bTL = new Vector2(n * 0.32f, n * 0.62f);
        var bTR = new Vector2(n * 0.82f, n * 0.82f);
        var bBR = new Vector2(n * 0.82f, n * 0.18f);
        var bBL = new Vector2(n * 0.32f, n * 0.38f);
        // Handle: rectangle on left — two CW triangles
        var hTL = new Vector2(n * 0.08f, n * 0.60f);
        var hTR = new Vector2(n * 0.32f, n * 0.60f);
        var hBR = new Vector2(n * 0.32f, n * 0.40f);
        var hBL = new Vector2(n * 0.08f, n * 0.40f);

        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var   p  = new Vector2(x + 0.5f, y + 0.5f);
            float d1 = SdfTriangle(p, bTL, bTR, bBR);
            float d2 = SdfTriangle(p, bTL, bBR, bBL);
            float d3 = SdfTriangle(p, hTL, hTR, hBR);
            float d4 = SdfTriangle(p, hTL, hBR, hBL);
            float d  = Mathf.Min(Mathf.Min(d1, d2), Mathf.Min(d3, d4));
            px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d / aa));
        }

        tex.SetPixels(px);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        try   { return ImageConversion.EncodeToPNG(tex); }
        finally { Object.Destroy(tex); }
    }

    // Shared segment-distance helper (also used by InspectIcon, defined there).

    private static float SdfTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        var e0 = b - a; var e1 = c - b; var e2 = a - c;
        var v0 = p - a; var v1 = p - b; var v2 = p - c;

        var pq0 = v0 - e0 * Mathf.Clamp01(Vector2.Dot(v0, e0) / Vector2.Dot(e0, e0));
        var pq1 = v1 - e1 * Mathf.Clamp01(Vector2.Dot(v1, e1) / Vector2.Dot(e1, e1));
        var pq2 = v2 - e2 * Mathf.Clamp01(Vector2.Dot(v2, e2) / Vector2.Dot(e2, e2));

        float s = Mathf.Sign(e0.x * e2.y - e0.y * e2.x);
        var   d = new Vector2(
            Mathf.Min(Mathf.Min(pq0.sqrMagnitude, pq1.sqrMagnitude), pq2.sqrMagnitude),
            Mathf.Min(Mathf.Min(s * (v0.x * e0.y - v0.y * e0.x),
                                s * (v1.x * e1.y - v1.y * e1.x)),
                                s * (v2.x * e2.y - v2.y * e2.x)));

        return -Mathf.Sqrt(d.x) * Mathf.Sign(d.y);
    }
}
