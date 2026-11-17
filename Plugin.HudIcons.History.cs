using UnityEngine;

namespace Stellar.CombatMeter;

// Procedural flat-white glyphs for the Combat History run panel's round Copy link and Delete buttons (addendum "run
// panel B", 2026-11-17). Same recipe and protection as the header glyphs (Plugin.HudIcons.Encounter.cs): a 64 px white
// SDF encoded to PNG once at construction by EncodeGlyphPng, whose scratch Texture2D is HideAndDontSave and destroyed in
// `finally` — the framework decodes the PNG into its own protected texture, so nothing here outlives the call. The
// copied-flash reuses the Ready Check ✓ (_checkmarkPng). Straight edges sit on the 4 px grid (whole pixels at 16 px).
// Texture rows run bottom-up, so y = 64 is the TOP of the glyph.
public sealed partial class Plugin
{
    private byte[]? _linkPng, _trashPng;

    // Called from BuildEncounterIcons (so the `nonewicons` crash-bisect switch covers these too).
    private void BuildHistoryIcons()
    {
        _linkPng  = EncodeGlyphPng(LinkSdf);
        _trashPng = EncodeGlyphPng(TrashSdf);
    }

    // Chain link — two rounded-capsule rings on the rising diagonal, overlapping in the middle.
    private static float LinkSdf(Vector2 p)
    {
        const float r = 9f, halfStroke = 3f;
        float a = Mathf.Abs(SdfSegment(p, new Vector2(15f, 15f), new Vector2(29f, 29f)) - r) - halfStroke;
        float b = Mathf.Abs(SdfSegment(p, new Vector2(35f, 35f), new Vector2(49f, 49f)) - r) - halfStroke;
        return Mathf.Min(a, b);
    }

    // Trash can — lid bar with a handle on top, an open-topped body outline below with two inner ribs.
    private static float TrashSdf(Vector2 p)
    {
        float lid    = SdfBox(p, 12f, 44f, 52f, 48f);
        float handle = Mathf.Max(SdfBox(p, 24f, 48f, 40f, 56f), -SdfBox(p, 28f, 47f, 36f, 52f));
        float body   = Mathf.Max(SdfBox(p, 16f, 8f, 48f, 40f), -SdfBox(p, 20f, 12f, 44f, 41f));
        float ribs   = Mathf.Min(SdfBox(p, 26f, 16f, 30f, 36f), SdfBox(p, 34f, 16f, 38f, 36f));
        return Mathf.Min(Mathf.Min(lid, handle), Mathf.Min(body, ribs));
    }

    // Distance from p to the segment a–b.
    private static float SdfSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
        return (p - (a + ab * t)).magnitude;
    }
}
