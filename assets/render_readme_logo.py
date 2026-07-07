"""
Generates the README header logo (logo-dark.png + logo-light.png, 512x256,
transparent): the V/F curve mark from the app icon, unboxed from the Windows
rounded-square tile and redrawn compact — a thicker glowing edge and three
pinstripes — so it survives H1 size (~40 px tall). The README swaps the
variants with a <picture> element to match GitHub's light/dark theme.

Requires Python 3 with numpy and pillow:  python render_readme_logo.py
"""

import os

import numpy as np
from PIL import Image

from render_icon import GREEN, HOT, MINT, pinstripe_layers, sd_polyline, smoothstep

OUT_DIR = os.path.dirname(os.path.abspath(__file__))
OW, OH = 384, 256
SS = 2
NX, NY = OW * SS, OH * SS

# V/F curve in logo coordinates (x in [-0.75, 0.75], y in [-0.5, 0.5], y up).
# The README's H1 renders the image at 50px with align="middle", which
# browsers treat as baseline-middle: the text baseline crosses the canvas
# at exactly y = 0. GitHub titles render in Mona Sans (32px, weight 600),
# whose lowercase tops sit 16px above the baseline, so the cap line lands
# 2px above them and the stripes end 2px below the baseline.
X0, XK = -0.69, 0.00
Y0, YC = -0.02, 0.36

# [color, opacity] per element, tuned per background the mark will sit on:
# on dark, the curve is a glowing white-hot edge; on light, a deep green stroke
# (a white core would vanish into a white page).
DEEP = np.array([0.043, 0.42, 0.235])       # #0B6B3C
VARIANTS = {
    "dark": {
        "stripe": (GREEN, 0.75),
        "bloom": (GREEN, 0.42),
        "glow": (MINT, 0.80),
        "core": (HOT, 1.0),
    },
    "light": {
        "stripe": (GREEN * 0.80, 0.65),
        "bloom": (GREEN * 0.90, 0.25),
        "glow": (GREEN * 0.80, 0.55),
        "core": (DEEP, 1.0),
    },
}


def curve_y(x):
    t = np.clip((x - X0) / (XK - X0), 0.0, 1.0)
    return Y0 + (YC - Y0) * t ** 1.6


def lay(col, a, lcol, la):
    """Straight-alpha 'over' of a colored layer onto the accumulated image."""
    ao = la + a * (1.0 - la)
    num = lcol[None, None, :] * la[..., None] + col * (a * (1.0 - la))[..., None]
    return num / np.maximum(ao, 1e-6)[..., None], ao


def render(v):
    xs = np.linspace(-0.75, 0.75, NX)
    ys = np.linspace(0.5, -0.5, NY)
    x, y = np.meshgrid(xs, ys)

    col = np.zeros((*x.shape, 3))
    a = np.zeros_like(x)

    fy = curve_y(x)
    below = smoothstep(0.02, -0.02, y - fy)

    stripe_c, stripe_a = v["stripe"]
    for i, line in pinstripe_layers(x, y, fy, YC, below,
                                    offsets=(0.13, 0.27, 0.40),
                                    width=(0.034, 0.012)):
        col, a = lay(col, a, stripe_c * (1.0 - 0.12 * i), line * stripe_a * 0.72 ** i)

    # the glow spills mostly downward: above the cap line the H1 text needs
    # clean ground, and an upward gradient would hard-crop at the canvas top
    cx = np.linspace(-1.3, 1.3, 90)
    d = sd_polyline(x, y, list(zip(cx, curve_y(cx))))
    spill = 1.0 - 0.75 * smoothstep(0.0, 0.05, y - fy)
    for key, fall in [("bloom", 8.0), ("glow", 16.0)]:
        c, ca = v[key]
        col, a = lay(col, a, c, np.exp(-fall * d) * spill * ca)
    core_c, core_a = v["core"]
    col, a = lay(col, a, core_c, smoothstep(0.055, 0.022, d) * core_a)

    # dissolve at the canvas edges: the mark floats, it isn't cropped
    a = a * smoothstep(0.75, 0.55, np.abs(x)) \
          * smoothstep(-0.30, -0.16, y) * smoothstep(0.50, 0.36, y)

    # supersample down premultiplied, so transparent pixels don't bleed color
    pm = (col * a[..., None]).reshape(OH, SS, OW, SS, 3).mean(axis=(1, 3))
    a = a.reshape(OH, SS, OW, SS).mean(axis=(1, 3))
    col = pm / np.maximum(a, 1e-6)[..., None]

    rgba = np.concatenate([np.clip(col, 0, 1), a[..., None]], axis=2)
    return Image.fromarray((rgba * 255 + 0.5).astype(np.uint8))


def main():
    for name, v in VARIANTS.items():
        out = os.path.join(OUT_DIR, f"logo-{name}.png")
        render(v).save(out)
        print(f"wrote {out}")


if __name__ == "__main__":
    main()
