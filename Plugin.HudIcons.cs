using UnityEngine;

namespace Stellar.CombatMeter;

// Procedural flat-white glyphs for the meter header's round ↺ Reset and ⋯ More buttons (approved redesign §1). Drawn,
// not typed: "↺" sits in the same Arrows block as "→", which the game font draws BLANK, and "⋯" is just as unproven
// (Stellar-Design-Language §6 glyph list), so both follow the leader-tool icons (Plugin.LocationPinIcon.cs): a 64 px
// white SDF shape the button tints muted / text on hover. Built once at construction (main thread).
public sealed partial class Plugin
{
    private byte[]? _resetPng;
    private byte[]? _morePng;

    // ↺ — a ring open at the top with an arrowhead on its right end, pointing anticlockwise into the gap.
    private static byte[]? BuildResetPng()
    {
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, mipChain: false);
        var px  = new Color[n * n];
        const float aa = 1.5f;

        var   c       = new Vector2(n * 0.50f, n * 0.47f);
        float r       = n * 0.29f;
        float strokeW = n * 0.06f;
        float gapFrom = 60f * Mathf.Deg2Rad, gapTo = 120f * Mathf.Deg2Rad;   // the open slice at the top

        // Arrowhead at the arc's 60° end, pointing along the anticlockwise tangent (into the gap).
        var radial = new Vector2(Mathf.Cos(gapFrom), Mathf.Sin(gapFrom));
        var tangent = new Vector2(-radial.y, radial.x);
        var end  = c + radial * r;
        var tip  = end + tangent * (n * 0.17f);
        var b1   = end + radial * (n * 0.14f) - tangent * (n * 0.03f);
        var b2   = end - radial * (n * 0.14f) - tangent * (n * 0.03f);
        var endA = c + new Vector2(Mathf.Cos(gapTo), Mathf.Sin(gapTo)) * r;   // the other (plain, round-capped) end

        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var   p   = new Vector2(x + 0.5f, y + 0.5f);
            var   v   = p - c;
            float ang = Mathf.Atan2(v.y, v.x);
            if (ang < 0f) ang += 2f * Mathf.PI;
            float dArc = ang > gapFrom && ang < gapTo
                ? Mathf.Min((p - end).magnitude, (p - endA).magnitude) - strokeW   // inside the gap → nearest end cap
                : Mathf.Abs(v.magnitude - r) - strokeW;
            float d = Mathf.Min(dArc, SdfTriangle(p, tip, b1, b2));
            px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d / aa));
        }

        tex.SetPixels(px);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        try   { return ImageConversion.EncodeToPNG(tex); }
        finally { Object.Destroy(tex); }
    }

    // ⋯ — three round dots on the midline.
    private static byte[]? BuildMorePng()
    {
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, mipChain: false);
        var px  = new Color[n * n];
        const float aa = 1.5f;
        float dotR = n * 0.085f;

        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var   p = new Vector2(x + 0.5f, y + 0.5f);
            float d = float.MaxValue;
            for (int i = 0; i < 3; i++)
                d = Mathf.Min(d, (p - new Vector2(n * (0.22f + 0.28f * i), n * 0.5f)).magnitude - dotR);
            px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d / aa));
        }

        tex.SetPixels(px);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        try   { return ImageConversion.EncodeToPNG(tex); }
        finally { Object.Destroy(tex); }
    }
}
