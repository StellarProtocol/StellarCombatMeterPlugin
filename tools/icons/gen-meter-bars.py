"""Draw the CombatMeter mark (approved icon A, 2026-11-17): three ranked, left-aligned, round-ended meter bars.

Pure-white glyph on transparent — the plugin draws it on a live accent Bubble tile (art can't follow the theme).

House icon rule (stellar-design SKILL): PRE-DRAWN per size and pixel-hinted, never one master scaled. Every size has
its own hand-tuned geometry in SIZES below, so bar tops/bottoms, gaps and left edges all land on whole pixels; only the
round ends are anti-aliased (8x8 supersampled coverage). The 24 px design: bars 18 / 13 / 8 wide, ~3.5 tall, 2 px gaps.

Usage (conda python with Pillow):
    python tools/icons/gen-meter-bars.py [--preview <png>]
Writes Resources/meter/meter-bars_<size>.png. Plugin.MeterIcon.cs's MeterIconSizes must list the same sizes.
"""
import argparse
import os

from PIL import Image, ImageDraw

# size: (bar height, gap, left x, top y, (w1, w2, w3)) — all whole pixels.
SIZES = {
    16:  (2, 2, 2, 3, (12, 9, 5)),
    18:  (3, 2, 2, 2, (14, 10, 6)),
    20:  (3, 2, 2, 3, (16, 11, 7)),
    24:  (4, 2, 3, 4, (18, 13, 8)),
    32:  (5, 3, 4, 5, (24, 17, 11)),
    36:  (5, 3, 4, 7, (28, 20, 12)),
    40:  (6, 3, 5, 8, (30, 22, 13)),
    48:  (7, 4, 6, 9, (36, 26, 16)),
    64:  (9, 5, 8, 13, (48, 35, 21)),
    128: (19, 11, 16, 24, (96, 69, 43)),
}
SS = 8  # supersampling factor for the round ends


def draw(size):
    h, gap, left, top, widths = SIZES[size]
    assert top + 3 * h + 2 * gap <= size and left + widths[0] <= size, size
    big = Image.new("L", (size * SS, size * SS), 0)
    d = ImageDraw.Draw(big)
    for i, w in enumerate(widths):
        y = top + i * (h + gap)
        # Pill: radius = half the bar height, so both ends are fully round.
        d.rounded_rectangle([left * SS, y * SS, (left + w) * SS - 1, (y + h) * SS - 1],
                            radius=h * SS / 2, fill=255)
    alpha = big.resize((size, size), Image.BOX)
    img = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    img.putalpha(alpha)
    return img


def preview(icons, path):
    accent = (52, 196, 186, 255)   # a stand-in accent for the tile
    bg = (24, 28, 34, 255)
    zoom = 6
    pad = 16
    total_w = pad + sum(max(s * zoom, 40) + pad for s in icons)
    canvas = Image.new("RGBA", (total_w, 128 + 128 * zoom // 2 + 3 * pad), bg)
    x = pad
    for s, img in icons.items():
        # Native size on an accent tile (tile = 40/24 of the glyph, like the 40 px header tile with a 24 px glyph).
        t = round(s * 40 / 24)
        tile = Image.new("RGBA", (t, t), (0, 0, 0, 0))
        ImageDraw.Draw(tile).rounded_rectangle([0, 0, t - 1, t - 1], radius=round(t * 0.22), fill=accent)
        tile.alpha_composite(img, ((t - s) // 2, (t - s) // 2))
        canvas.alpha_composite(tile, (x, pad))
        # Nearest-neighbour zoom of the bare glyph on the accent, so the pixel grid is visible.
        z = Image.new("RGBA", (s, s), accent)
        z.alpha_composite(img)
        canvas.alpha_composite(z.resize((s * zoom, s * zoom), Image.NEAREST), (x, pad * 2 + 128))
        ImageDraw.Draw(canvas).text((x, pad * 2 + 128 + s * zoom + 4), f"{s}px", fill=(220, 220, 220, 255))
        x += max(s * zoom, 40) + pad
    os.makedirs(os.path.dirname(path), exist_ok=True)
    canvas.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--preview")
    args = ap.parse_args()
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Resources", "meter")
    os.makedirs(out_dir, exist_ok=True)
    icons = {}
    for s in SIZES:
        icons[s] = draw(s)
        icons[s].save(os.path.join(out_dir, f"meter-bars_{s}.png"), optimize=True)
    if args.preview:
        preview({s: icons[s] for s in (16, 18, 20, 24, 32, 36, 40, 48, 64)}, args.preview)
        # 128 is too wide for the zoomed strip; show it at native size in a second file next to it.
        big = Image.new("RGBA", (213, 213), (24, 28, 34, 255))
        ImageDraw.Draw(big).rounded_rectangle([0, 0, 212, 212], radius=47, fill=(52, 196, 186, 255))
        big.alpha_composite(icons[128], (42, 42))
        big.save(os.path.join(os.path.dirname(args.preview), "preview-128.png"))


if __name__ == "__main__":
    main()
