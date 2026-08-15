using System;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.CombatMeter.LogUpload;

/// <summary>Phase-1 SPIKE: renders a minimal card offscreen (Canvas + Camera → RenderTexture →
/// Texture2D → PNG) to prove offscreen uGUI text (incl. CJK) works under IL2CPP before the rich v2
/// layout is built. Returns <see langword="null"/> on ANY failure so the caller falls back to the text
/// post. MUST run on the Unity main thread. Heavily logged so the first in-game run is diagnostic.</summary>
internal static class DiscordCardRenderer
{
    private static Font? _font;

    internal static byte[]? RenderSpike(string title, string[] lines, Action<string> log)
    {
        GameObject? root = null;
        RenderTexture? rt = null;
        Texture2D? tex = null;
        var prev = RenderTexture.active;
        try
        {
            const int W = 900;
            int H = 64 + Math.Max(1, lines.Length) * 40 + 18;   // content-sized: header + rows + pad (no empty space)
            var font = ResolveFont(log);

            rt = new RenderTexture(W, H, 0, RenderTextureFormat.ARGB32);
            rt.Create();

            root = new GameObject("stellar_card_spike") { hideFlags = HideFlags.HideAndDontSave };

            var camGo = new GameObject("cam");
            camGo.transform.SetParent(root.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = H / 2f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.06f, 0.09f, 1f);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 100f;
            cam.targetTexture = rt;

            var canvasGo = new GameObject("canvas");
            canvasGo.transform.SetParent(root.transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 1f;
            var crt = canvasGo.GetComponent<RectTransform>();
            crt.sizeDelta = new Vector2(W, H);

            AddPanel(canvas.transform, 0, H - 52, W, 52, new Color(0.11f, 0.13f, 0.20f, 1f));
            AddText(canvas.transform, title, font, 22, new Color(0.96f, 0.97f, 0.99f, 1f), 20, H - 46, W - 40, 40);
            for (int i = 0; i < lines.Length; i++)
            {
                float y = H - 64 - (i + 1) * 40f;
                AddText(canvas.transform, lines[i], font, 17, new Color(0.87f, 0.90f, 0.95f, 1f), 20, y, W - 40, 34);
            }

            cam.Render();
            RenderTexture.active = rt;
            tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply(false, false);
            var png = ImageConversion.EncodeToPNG(tex);
            log($"[CombatMeter.SP1] Card spike rendered {W}x{H} -> {(png?.Length ?? 0)} PNG bytes.");
            return png;
        }
        catch (Exception ex)
        {
            log($"[CombatMeter.SP1] Card spike render FAILED: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            RenderTexture.active = prev;
            if (tex != null) UnityEngine.Object.Destroy(tex);
            if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            if (root != null) UnityEngine.Object.Destroy(root);
        }
    }

    /// <summary>Finds a usable font. The builtin Arial is stripped from IL2CPP player builds, so we pick
    /// the loaded game font with the most baked glyphs (the UI font — CJK-capable). Logged so we can pin
    /// the exact font by name after the first run.</summary>
    private static Font ResolveFont(Action<string> log)
    {
        if (_font != null) return _font;
        Font? best = null;
        int bestScore = -1;
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
        log(best != null
            ? $"[CombatMeter.SP1] Card font = '{best.name}' (dynamic={best.dynamic}, glyphs={best.characterInfo?.Length ?? 0})."
            : "[CombatMeter.SP1] Card font = NONE FOUND (text will be blank).");
        return _font!;
    }

    private static void AddPanel(Transform parent, float x, float y, float w, float h, Color c)
    {
        var go = new GameObject("panel");
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = c;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 0);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    private static void AddText(Transform parent, string s, Font? font, int size, Color c, float x, float y, float w, float h)
    {
        var go = new GameObject("txt");
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        t.font = font;
        t.fontSize = size;
        t.color = c;
        t.text = s;
        t.alignment = TextAnchor.MiddleLeft;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 0);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }
}
