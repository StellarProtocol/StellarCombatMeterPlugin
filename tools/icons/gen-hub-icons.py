"""Draw the CombatMeter hub card glyphs (approved 2026-11-17, mockup combatmeter-hub-icons.tpl.html):

  S1 "solid gear"    -> Resources/hub/settings-gear_<size>.png   (the Settings card / ... popover Settings card)
  H1 "rewind clock"  -> Resources/hub/history_<size>.png         (the History card, the History window header, the
                                                                  ... popover History card)

Both are flat WHITE glyphs on TRANSPARENT: the plugin draws them on a live accent tile and tints them
(ImageElement.Tint = SolidLabel(accent)), like the meter-bars mark.

House icon rule (Stellar-Design-Language.md): PRE-DRAWN per size and pixel-hinted, never one master scaled. Geometry is
the mockup's 24-unit SVG symbols, re-laid-out per size so straight edges land on pixel boundaries:
  - gear: the centre sits on the pixel grid (pixel edge for an even size, pixel centre for an odd one), the tooth width
    is a whole number of pixels with the size's parity and the tooth tips sit on whole pixels — so the four axis teeth
    are crisp; the diagonal teeth and the round body / hole are anti-aliased (8x8 supersampled coverage).
    Hinting simplification: the mockup's teeth taper ~0.4 units over their 2-unit height; here their sides are straight
    (vertical / horizontal on the axis teeth) so they stay crisp.
  - clock: a whole-pixel round-capped stroke; the centre, the vertical minute hand and the arrowhead's two legs sit on
    pixel centres (odd stroke) or pixel edges (even stroke), the arc radius is whole pixels.
    Small-size simplification: the stroke is never under 2 px (round(2.2 x 15/24) would be 1 px at 15 / 16 px and the
    glyph goes faint on the tile).

Sizes = every slot x 0.75 / 1 / 1.5 px per unit (1080p / 1440p / 4K) + 16 / 32 / 64:
  gear    — the 20-unit hub / popover card glyph               -> 15 20 30 (+16 24 32 64)
  history — the 20-unit hub / popover card glyph + the 24-unit
            History window header glyph                         -> 15 18 20 24 30 36 (+16 32 64)
Plugin.MeterIcon.cs GearIconSizes / HistoryIconSizes must match GEAR_SIZES / HISTORY_SIZES.

Usage (conda python with Pillow):
    python tools/icons/gen-hub-icons.py [--contact <png>]
--contact writes a review sheet: every size on #c8508f tiles, x4 nearest-neighbour.
"""
import argparse
import math
import os

from PIL import Image, ImageDraw

GEAR_SIZES = [15, 16, 20, 24, 30, 32, 64]
HISTORY_SIZES = [15, 16, 18, 20, 24, 30, 32, 36, 64]
SS = 8


def rnd(v):
    """Round half up (Python's round() is banker's: round(2.5) = 2 made the 30 px gear the same size as the 32 px one)."""
    return math.floor(v + 0.5)


def to_rgba(big, size):
    img = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    img.putalpha(big.resize((size, size), Image.BOX))
    return img


def draw_gear(size):
    """S1: 8 teeth (tips r 10, width 3.4), body r 8, hole r 3.5 on a 24 grid."""
    k = size / 24
    c = size / 2                                  # pixel edge (even size) / pixel centre (odd size)
    tip = c - rnd(2 * k)                        # tooth tip distance from the centre: tips land on whole pixels
    tw = rnd(3.4 * k)
    if tw % 2 != size % 2:                        # tooth sides on whole pixels: width parity = size parity
        tw += 1 if 3.4 * k > tw else -1
    tw = max(tw, 2 if size % 2 == 0 else 3)
    body = 8 * k
    hole = max(3.5 * k, 2.2)                      # keep a readable hole at 15 / 16 px
    S = SS
    big = Image.new("L", (size * S, size * S), 0)
    d = ImageDraw.Draw(big)
    C = c * S
    d.ellipse([C - body * S, C - body * S, C + body * S, C + body * S], fill=255)
    half = tw / 2
    base = body - 1.5 * k                         # tooth root, buried inside the body
    for i in range(8):
        a = math.radians(45 * i)
        ca, sa = math.cos(a), math.sin(a)
        pts = []
        for r, t in ((base, -half), (tip, -half), (tip, half), (base, half)):
            # radial r along (ca, sa), tangential t along (-sa, ca); snap the axis teeth exactly
            x, y = c + r * ca - t * sa, c + r * sa + t * ca
            if i % 2 == 0:
                x, y = rnd(x * 2) / 2, rnd(y * 2) / 2
            pts.append((x * S, y * S))
        d.polygon(pts, fill=255)
    d.ellipse([C - hole * S, C - hole * S, C + hole * S, C + hole * S], fill=0)
    return to_rgba(big, size)


def stamp_path(d, pts, w, step):
    """Round-capped, round-joined stroke: disks stamped densely along the polyline (supersampled space)."""
    r = w / 2
    for (x0, y0), (x1, y1) in zip(pts, pts[1:]):
        n = max(1, int(math.hypot(x1 - x0, y1 - y0) / step))
        for i in range(n + 1):
            x, y = x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * i / n
            d.ellipse([x - r, y - r, x + r, y + r], fill=255)


def draw_history(size):
    """H1: arc r 7.5 from 180 deg round (via the bottom and right) to 225 deg (a ~315 deg open circle, gap at the top-left),
    an L arrowhead (M4 3.5 v4 h4) at the gap, hands (12,8)->(12,12.3)->(15,14.1). 2.2 stroke, round caps."""
    k = size / 24
    stroke = max(2, rnd(2.2 * k))
    on = (lambda v: math.floor(v) + 0.5) if stroke % 2 else (lambda v: float(rnd(v)))   # stroke-centre grid
    c = on(size / 2)
    def X(u): return c + (u - 12) * k
    r = max(1, rnd(7.5 * k))
    S = SS
    big = Image.new("L", (size * S, size * S), 0)
    d = ImageDraw.Draw(big)
    w = stroke * S
    step = S / 4
    # Arc: SVG sweep-flag 0 = decreasing angle (y-down) from 180 deg to -135 deg.
    arc = [(c + r * math.cos(math.radians(180 - 315 * i / 96)), c + r * math.sin(math.radians(180 - 315 * i / 96)))
           for i in range(97)]
    stamp_path(d, [(x * S, y * S) for x, y in arc], w, step)
    # Arrowhead: vertical leg x = 4 (y 3.5 -> 7.5), horizontal leg y = 7.5 (x 4 -> 8), both on the stroke-centre grid.
    ax, ay = on(X(4)), on(X(7.5))
    leg = max(stroke + 1, rnd(4 * k))
    stamp_path(d, [(ax * S, (ay - leg) * S), (ax * S, ay * S), ((ax + leg) * S, ay * S)], w, step)
    # Hands: 12 o'clock (vertical, crisp) then towards ~4 o'clock.
    top = on(X(8))
    hub = (c, c + rnd(0.3 * k))
    end = (X(15), X(14.1))
    stamp_path(d, [(c * S, top * S), (hub[0] * S, hub[1] * S), (end[0] * S, end[1] * S)], w, step)
    return to_rgba(big, size)


def contact(path):
    zoom, pad = 4, 6
    accent, bg = (0xC8, 0x50, 0x8F, 255), (24, 28, 34, 255)
    rows = [(draw_gear, GEAR_SIZES), (draw_history, HISTORY_SIZES)]
    def tile(s): return s + 2 * max(3, rnd(s * 0.4))
    width = max(sum(tile(s) * zoom + pad for s in sizes) for _, sizes in rows) + pad
    height = sum(max(tile(s) for s in sizes) * zoom + pad for _, sizes in rows) + pad
    sheet = Image.new("RGBA", (width, height), bg)
    y = pad
    for fn, sizes in rows:
        x = pad
        for s in sizes:
            t = tile(s)
            im = Image.new("RGBA", (t, t), accent)
            im.alpha_composite(fn(s), ((t - s) // 2, (t - s) // 2))
            sheet.alpha_composite(im.resize((t * zoom, t * zoom), Image.NEAREST), (x, y))
            x += t * zoom + pad
        y += max(tile(s) for s in sizes) * zoom + pad
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    sheet.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--contact")
    args = ap.parse_args()
    hub_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Resources", "hub")
    os.makedirs(hub_dir, exist_ok=True)
    for s in GEAR_SIZES:
        draw_gear(s).save(os.path.join(hub_dir, f"settings-gear_{s}.png"), optimize=True)
    for s in HISTORY_SIZES:
        draw_history(s).save(os.path.join(hub_dir, f"history_{s}.png"), optimize=True)
    if args.contact:
        contact(args.contact)


if __name__ == "__main__":
    main()
