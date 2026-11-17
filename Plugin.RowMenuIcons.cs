using System;
using System.Collections.Generic;

namespace Stellar.CombatMeter;

// Row context-menu icons (approved icon set 2026-11-17). Each glyph ships PRE-DRAWN at 16 / 24 / 32 px
// (Resources/rowmenu/<stem>_<size>.png) — never one master scaled down (SDL icon rule: thin strokes go soft grey) — and
// the menu picks the exact-size bitmap for the live UI scale. Bytes are read once per (stem, size) and cached, so the
// ImageElement's reference-keyed texture cache sees the same array on every open (no per-open allocation or re-bake).
public sealed partial class Plugin
{
    private static readonly int[] RowMenuIconSizes = { 16, 24, 32 };
    private const float RowMenuIconUnits = 16f;   // the icon slot, in canvas units

    // stem → per-size PNG bytes (index matches RowMenuIconSizes; null = resource missing/unreadable). Main thread only.
    private readonly Dictionary<string, byte[]?[]> _rowMenuIconCache = new(StringComparer.Ordinal);

    // Index into RowMenuIconSizes for the 16-unit slot at the current UI scale, re-evaluated once per menu open.
    // Screen px per canvas unit = ScreenWidth / CanvasWidth (the CanvasScaler scaleFactor: 0.75 at 1080p, 1 at 1440p,
    // 1.5 at 4K). Exact-else-next-larger, like the framework's IconSizePicker: 12 px at 1080p → the 16 drawing (no 12
    // is drawn), 16 at 1440p → 16, 24 at 4K → 24; past the largest → 32.
    private int RowMenuIconSizeIndex()
    {
        var fw = _services.Framework;
        float pxPerUnit = fw.CanvasWidth > 0 && fw.ScreenWidth > 0 ? (float)fw.ScreenWidth / fw.CanvasWidth : 1f;
        return PickIconSizeIndex(RowMenuIconUnits * pxPerUnit, RowMenuIconSizes);
    }

    // Pure (unit-testable): the exact size within ±0.05 px, else the smallest larger, else the largest.
    internal static int PickIconSizeIndex(float targetPx, int[] sizes)
    {
        for (int i = 0; i < sizes.Length; i++)
            if (sizes[i] >= targetPx - 0.05f) return i;
        return sizes.Length - 1;
    }

    // Cached PNG bytes for <stem> at sizes[sizeIdx]; falls back to the other drawn sizes, then null.
    private byte[]? RowMenuIconPng(string stem, int sizeIdx)
    {
        if (!_rowMenuIconCache.TryGetValue(stem, out var bySize))
        {
            bySize = new byte[]?[RowMenuIconSizes.Length];
            for (int i = 0; i < bySize.Length; i++) bySize[i] = ReadRowMenuIcon($"{stem}_{RowMenuIconSizes[i]}.png");
            _rowMenuIconCache[stem] = bySize;
        }
        if (bySize[sizeIdx] != null) return bySize[sizeIdx];
        for (int i = bySize.Length - 1; i >= 0; i--) if (bySize[i] != null) return bySize[i];
        return null;
    }

    private static byte[]? ReadRowMenuIcon(string file)
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream($"Stellar.CombatMeter.rowmenu.{file}");
            if (s is null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    // The meter's own items → icon stem, matched by the label they were registered with (Plugin.TeamActions.cs registers
    // with _loc.T of these keys and re-registers on a language change, so the strings match). Destructive items use the
    // red-drawn variants. Null = not one of ours (the caller then uses the item's own IconPng, else "generic").
    private string? OwnRowMenuIconStem(string label, out bool danger)
    {
        danger = false;
        if (label == _loc.T("menu.kickFromParty"))  { danger = true; return "kick-red"; }
        if (label == _loc.T("menu.leaveParty"))     { danger = true; return "leave-red"; }
        if (label == _loc.T("menu.sendMessage"))    return "message";
        if (label == _loc.T("menu.transferLeader")) return "leader";
        if (label == _loc.T("menu.inviteToParty"))  return "invite";
        if (label == _loc.T("menu.createParty"))    return "create-party";
        if (label == _loc.T("menu.muteVoice"))      return "mute";
        if (label == _loc.T("menu.enableVoice"))    return "unmute";
        if (label == _loc.T("menu.reportVoice"))    return "report";
        return null;
    }
}
