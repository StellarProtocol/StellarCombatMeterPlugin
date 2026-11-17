namespace Stellar.CombatMeter;

// The CombatMeter mark (approved icon A, 2026-11-17): three ranked, left-aligned, round-ended meter bars — a flat white
// glyph for a live accent tile. It replaced two vertical Bubble bars that read like "Pause". Shipped PRE-DRAWN and
// pixel-hinted per size (Resources/meter/meter-bars_<size>.png, drawn by tools/icons/gen-meter-bars.py) — never one
// master scaled (SDL icon rule: a scaled 3.5 px bar lands on half pixels and goes soft). Sizes cover the slots it is drawn
// in at 0.75 / 1 / 1.5 px-per-unit (1080p / 1440p / 4K): the 24-unit header glyph → 18 / 24 / 36, the 32-unit launcher
// glyph → 24 / 32 / 48, plus 16 / 20 / 40 / 64 / 128 for smaller slots and the launcher's mipmapped scaling.
public sealed partial class Plugin
{
    // Ascending; must match the files gen-meter-bars.py writes (its SIZES table).
    private static readonly int[] MeterIconSizes = { 16, 18, 20, 24, 32, 36, 40, 48, 64, 128 };

    // Per-size PNG bytes (index matches MeterIconSizes; null = missing). Read once, then the SAME arrays are handed out,
    // so a Live ImageElement's reference compare sees no change between polls (no re-decode). Main thread only.
    private byte[]?[]? _meterIconPngs;

    // The pre-drawn bitmap for a slot `units` canvas units wide at the CURRENT UI scale (exact-else-next-larger, the
    // row-menu picker). Allocation-free after the first call — safe as a per-refresh Live image source.
    private byte[]? MeterIconPng(float units)
    {
        var pngs = _meterIconPngs ??= LoadMeterIconPngs();
        var fw = _services.Framework;
        float pxPerUnit = fw.CanvasWidth > 0 && fw.ScreenWidth > 0 ? (float)fw.ScreenWidth / fw.CanvasWidth : 1f;
        int idx = PickIconSizeIndex(units * pxPerUnit, MeterIconSizes);
        if (pngs[idx] != null) return pngs[idx];
        for (int i = pngs.Length - 1; i >= 0; i--) if (pngs[i] != null) return pngs[i];
        return null;
    }

    private static byte[]?[] LoadMeterIconPngs()
    {
        var pngs = new byte[]?[MeterIconSizes.Length];
        for (int i = 0; i < pngs.Length; i++)
        {
            try
            {
                using var s = typeof(Plugin).Assembly.GetManifestResourceStream(
                    $"Stellar.CombatMeter.meter.meter-bars_{MeterIconSizes[i]}.png");
                if (s is null) continue;
                using var ms = new System.IO.MemoryStream();
                s.CopyTo(ms);
                pngs[i] = ms.ToArray();
            }
            catch { /* a missing/corrupt size falls back to another drawn size */ }
        }
        return pngs;
    }
}
