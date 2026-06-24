using System;
using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.CombatMeter;

// Team-voice indicators on the meter row:
//   • name-line icon shows the mic MODE — mic (Speak Mode) / headphone (Listen) / muted (Speaker Mute)
//   • a green row border shows who is currently TALKING
// Framework decodes GrpcTeamNtf 25/26 + voice_is_open and exposes it via IPartyRoster.
// The icon is user-toggleable. Glyphs are drawn once as white textures (tinted by the binding).
public sealed partial class Plugin
{
    private Texture2D? _micTex, _headTex, _mutedTex;

    private static readonly ColorRgba TalkBorderCol = new(0.36f, 0.85f, 0.46f, 1f); // green

    // Name-line voice icon for a row: party members get a mic/headphone/muted glyph by mic state;
    // non-party rows and the toggle-off state get none (null → the cell stays hidden).
    private object? VoiceIconFor(EntityId id)
    {
        long charId = id.Uid;
        if (!IsPartyMember(charId)) return null;
        EnsureVoiceTextures();
        return _services.PartyRoster.GetMicStatus(charId) switch
        {
            MicrophoneStatus.Closed      => _micTex,   // Speak Mode
            MicrophoneStatus.OpenSpeaker => _mutedTex, // Speaker Mute
            _                            => _headTex,  // Listen (Opened)
        };
    }

    // Green box border around the row while a member is talking; no border otherwise.
    private ColorRgba TalkBorderFor(EntityId id)
        => _services.PartyRoster.IsSpeaking(id.Uid) ? TalkBorderCol : default;

    private void EnsureVoiceTextures()
    {
        // Prefer an embedded PNG resource (Stellar.CombatMeter.<name>); fall back to the drawn glyph.
        _micTex   ??= LoadEmbeddedPng("mic.png")       ?? RenderGlyph(MicSdf);
        _headTex  ??= LoadEmbeddedPng("headphone.png") ?? RenderGlyph(HeadSdf);
        _mutedTex ??= LoadEmbeddedPng("mute.png")      ?? RenderGlyph(MutedSdf);
    }

    // Loads a PNG packed as an embedded resource into a Texture2D; null if absent/unreadable.
    private static Texture2D? LoadEmbeddedPng(string name)
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream($"Stellar.CombatMeter.{name}");
            if (s is null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = UnityEngine.FilterMode.Bilinear };
            if (!ImageConversion.LoadImage(tex, ms.ToArray())) { UnityEngine.Object.Destroy(tex); return null; }
            return tex;
        }
        catch { return null; }
    }

    // ── glyph SDFs (white on transparent, 64×64) ─────────────────────────────────
    private static Texture2D RenderGlyph(Func<Vector2, int, float> sdf)
    {
        const int n = 64; const float aa = 1.6f;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
        { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = UnityEngine.FilterMode.Bilinear };
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f);
            px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - sdf(p, n) / aa));
        }
        tex.SetPixels(px); tex.Apply(false, false);
        return tex;
    }

    // Dynamic handheld microphone, tilted ~45° (ball grille upper-right, handle lower-left) with a
    // little cable curling off the base — the classic mic-icon silhouette.
    private static float MicSdf(Vector2 p, int n)
    {
        Vector2 V(float a, float b) => new(n * a, n * b);
        float ball = (p - V(0.64f, 0.64f)).magnitude - n * 0.165f;                  // round grille
        float body = DistToSegment(p, V(0.58f, 0.58f), V(0.36f, 0.36f)) - n * 0.10f; // tapered handle
        float w1   = DistToSegment(p, V(0.36f, 0.36f), V(0.28f, 0.28f)) - n * 0.022f; // cable
        float w2   = DistToSegment(p, V(0.28f, 0.28f), V(0.36f, 0.20f)) - n * 0.022f;
        float w3   = DistToSegment(p, V(0.36f, 0.20f), V(0.30f, 0.13f)) - n * 0.022f;
        return Mathf.Min(Mathf.Min(ball, body), Mathf.Min(w1, Mathf.Min(w2, w3)));
    }

    // Headphone: top half-ring band + two ear pads.
    private static float HeadSdf(Vector2 p, int n)
    {
        var c = new Vector2(n * 0.5f, n * 0.44f);
        float band = p.y >= c.y ? Mathf.Abs((p - c).magnitude - n * 0.28f) - n * 0.045f : 999f;
        float padL = DistToSegment(p, new(n * 0.22f, n * 0.44f), new(n * 0.22f, n * 0.26f)) - n * 0.075f;
        float padR = DistToSegment(p, new(n * 0.78f, n * 0.44f), new(n * 0.78f, n * 0.26f)) - n * 0.075f;
        return Mathf.Min(band, Mathf.Min(padL, padR));
    }

    // Muted: headphone + a diagonal slash.
    private static float MutedSdf(Vector2 p, int n)
    {
        float slash = DistToSegment(p, new(n * 0.16f, n * 0.18f), new(n * 0.84f, n * 0.82f)) - n * 0.045f;
        return Mathf.Min(HeadSdf(p, n), slash);
    }
}
