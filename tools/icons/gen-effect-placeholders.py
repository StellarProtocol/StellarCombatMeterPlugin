"""Draw the effect placeholder tiles (approved option B, 2026-11-17): one white flat glyph per effect category on a
green (buff) or red (debuff) tile, for effects that have no Imagine card, no source-skill icon and no icon of their own.

The meter cell stretches its icon over the WHOLE tile body (framework WindowBuilder.MeterDebuffs: frame > inset > RawImage
stretched), so the tile colour is baked into the picture — a transparent glyph would sit on the cell's black inset and
lose the green/red read. Colours = the approved mockup's tile fills (#3f8f5f buff, #b2444a debuff); the framework's
brighter 1 px frame still rings the cell.

House icon rule (stellar-design SKILL): PRE-DRAWN per size and pixel-hinted, never one master scaled. Geometry is the
mockup's 16-unit SVG glyph set (combatmeter-effect-placeholder.html), laid out per size so the glyph box spans 2/3 of
the tile, the stroke is a whole number of pixels, and the glyph's centre lines (x = 8, y = 8) sit on pixel centres for an
odd stroke and on pixel edges for an even one — so straight strokes (arrows, plus, shield sides) are crisp; only curves
are anti-aliased (8x8 supersampled coverage).

Sizes: every pixel size a placeholder is drawn at — meter tile art (cell − 2 units: 18 / 24 / 30), tooltip 28, Settings
› Effects row 20 — at 0.75 / 1 / 1.5 px per unit (1080p / 1440p / 4K), rounded up, plus 16 / 32 / 64.

Usage (conda python with Pillow):
    python tools/icons/gen-effect-placeholders.py [--preview <png>]
Writes Resources/effects/<glyph>-<buff|debuff>_<size>.png. (The hub card glyphs — Settings gear, History clock — are
drawn by gen-hub-icons.py.)
Plugin.EffectIcons.cs's PlaceholderSizes / glyph stems must match SIZES / GLYPHS below.
"""
import argparse
import math
import os

from PIL import Image, ImageDraw

SIZES = [14, 15, 16, 18, 20, 21, 23, 24, 27, 28, 30, 32, 36, 42, 45, 64]
KINDS = {"buff": (0x3F, 0x8F, 0x5F, 255), "debuff": (0xB2, 0x44, 0x4A, 255)}
SS = 8


def arc(cx, cy, r, a0, a1, n=24):
    """Points along a circle arc in SVG (y-down) angle space, degrees."""
    return [(cx + r * math.cos(math.radians(a0 + (a1 - a0) * i / n)),
             cy + r * math.sin(math.radians(a0 + (a1 - a0) * i / n))) for i in range(n + 1)]


def cubic(p0, p1, p2, p3, n=16):
    out = []
    for i in range(n + 1):
        t = i / n
        u = 1 - t
        out.append((u ** 3 * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t ** 3 * p3[0],
                    u ** 3 * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t ** 3 * p3[1]))
    return out


# Shield outline (mockup path "M8 2l5 2v4c0 3-2.2 5-5 6c-2.8-1-5-3-5-6V4z"), closed.
SHIELD = ([(8, 2), (13, 4), (13, 8)] + cubic((13, 8), (13, 11), (10.8, 13), (8, 14))[1:]
          + cubic((8, 14), (5.2, 13), (3, 11), (3, 8))[1:] + [(3, 4), (8, 2)])
# Droplet ("M8 2c3 4 4.5 6 4.5 8a4.5 4.5 0 0 1-9 0C3.5 8 5 6 8 2z"), closed.
DROP = (cubic((8, 2), (11, 6), (12.5, 8), (12.5, 10)) + arc(8, 10, 4.5, 0, 180)[1:]
        + cubic((3.5, 10), (3.5, 8), (5, 6), (8, 2))[1:])

# glyph -> list of ("line", points, stroke_scale) | ("disc", (x, y, r)); 16-unit coords, y down.
GLYPHS = {
    "default":  [("line", arc(8, 8, 5.5, 0, 360, 48), 1.0), ("disc", (8, 8, 1.6))],
    "cc":       [("line", arc(8, 8, 1, 180, 360, 12) + arc(6, 8, 3, 0, 180, 24)[1:] + arc(8, 8, 5, 180, 360, 36)[1:], 1.0)],
    "immunity": [("line", SHIELD, 1.0), ("line", [(5.5, 8), (7.5, 10), (10.5, 6.5)], 1.0)],
    "shield":   [("line", SHIELD, 1.0)],
    "heal":     [("line", [(8, 3), (8, 13)], 1.25), ("line", [(3, 8), (13, 8)], 1.25)],
    "dot":      [("line", DROP, 1.0)],
    "up":       [("line", [(8, 13), (8, 3)], 1.0), ("line", [(4, 7), (8, 3), (12, 7)], 1.0)],
    "down":     [("line", [(8, 3), (8, 13)], 1.0), ("line", [(4, 9), (8, 13), (12, 9)], 1.0)],
}


def layout(size):
    """(scale px-per-unit, stroke px, centre x/y px) for one tile size — whole-pixel stroke, hinted centre."""
    scale = size * (16 / 24) / 16           # glyph box = 2/3 of the tile (mockup: 16 px glyph in a 24 px tile)
    stroke = max(1, round(2 * scale))       # the mockup's 2-unit stroke, whole pixels
    centre = math.floor(size / 2) + 0.5 if stroke % 2 else round(size / 2)
    return scale, stroke, centre


def draw_glyph(d, name, size):
    scale, stroke, c = layout(size)

    def px(p):
        return ((c + (p[0] - 8) * scale) * SS, (c + (p[1] - 8) * scale) * SS)

    for part in GLYPHS[name]:
        if part[0] == "disc":
            x, y, r = part[1]
            (X, Y), R = px((x, y)), max(r * scale, 1.0) * SS
            d.ellipse([X - R, Y - R, X + R, Y + R], fill=255)
            continue
        pts = [px(p) for p in part[1]]
        w = max(1, round(stroke * part[2])) * SS
        d.line(pts, fill=255, width=w, joint="curve")
        for X, Y in (pts[0], pts[-1]):     # round caps
            d.ellipse([X - w / 2, Y - w / 2, X + w / 2, Y + w / 2], fill=255)


def draw(name, kind, size):
    big = Image.new("L", (size * SS, size * SS), 0)
    draw_glyph(ImageDraw.Draw(big), name, size)
    alpha = big.resize((size, size), Image.BOX)
    tile = Image.new("RGBA", (size, size), KINDS[kind])
    white = Image.new("RGBA", (size, size), (255, 255, 255, 255))
    white.putalpha(alpha)
    tile.alpha_composite(white)
    return tile


def preview(path):
    zoom, pad = 4, 8
    bg = (24, 28, 34, 255)
    row_h = max(SIZES) * zoom + pad
    canvas = Image.new("RGBA", (pad + sum(s * zoom + pad for s in SIZES), pad + row_h * len(GLYPHS) * 2), bg)
    y = pad
    for name in GLYPHS:
        for kind in KINDS:
            x = pad
            for s in SIZES:
                canvas.alpha_composite(draw(name, kind, s).resize((s * zoom, s * zoom), Image.NEAREST), (x, y))
                x += s * zoom + pad
            y += row_h
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    canvas.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--preview")
    args = ap.parse_args()
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Resources", "effects")
    os.makedirs(out_dir, exist_ok=True)
    for name in GLYPHS:
        for kind in KINDS:
            for s in SIZES:
                draw(name, kind, s).save(os.path.join(out_dir, f"{name}-{kind}_{s}.png"), optimize=True)
    if args.preview:
        preview(args.preview)


if __name__ == "__main__":
    main()
