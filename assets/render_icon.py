"""
Generates the app icon (icon.ico + icon.png next to this script) and the
active-marker variant (icon-active.ico + icon-active.png): the same icon with
a green checkmark badge in the bottom-right corner. The app stamps the badged
icon onto the .lnk of the profile that is currently applied.

The mark is the tool's story: the V/F curve rising and hard-flattening at the
voltage cap, drawn as a glowing edge over pinstripes that relax from
curve-shaped to flat with depth. Rendered per-pixel with numpy at 2x
supersampling, masked to a rounded-square app-icon silhouette.

Requires Python 3 with numpy and pillow:  python render_icon.py
"""

import os

import numpy as np
from PIL import Image

OUT_DIR = os.path.dirname(os.path.abspath(__file__))
SIZE = 512
SS = 2                              # supersample factor
N = SIZE * SS
ICO_SIZES = [16, 24, 32, 48, 64, 256]

# Emerald palette
GREEN = np.array([0.090, 0.698, 0.416])     # #17B26A
MINT = np.array([0.34, 0.93, 0.55])
NEARBLK = np.array([0.035, 0.045, 0.040])
INK = np.array([0.02, 0.028, 0.026])
HOT = np.array([0.80, 1.0, 0.88])           # white-hot core

# V/F curve: rise starts at (X0, Y0), hard-flattens at the knee (XK, YC)
X0, XK = -0.80, 0.10
Y0, YC = -0.54, 0.50
ZOOM = 1.08                         # >1 zooms the composition out slightly


def grid():
    xs = np.linspace(-1.0, 1.0, N)
    x, y = np.meshgrid(xs, xs)
    return x, -y                    # y points up


def smoothstep(a, b, t):
    t = np.clip((t - a) / (b - a), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def sd_polyline(x, y, pts):
    d = np.full_like(x, 1e9)
    for (ax, ay), (bx, by) in zip(pts, pts[1:]):
        pax, pay = x - ax, y - ay
        bax, bay = bx - ax, by - ay
        h = np.clip((pax * bax + pay * bay) / (bax * bax + bay * bay + 1e-9), 0.0, 1.0)
        d = np.minimum(d, np.hypot(pax - bax * h, pay - bay * h))
    return d


def over(base, color, alpha):
    return base * (1 - alpha[..., None]) + color * alpha[..., None]


def curve_y(x):
    """t^1.6 keeps the slope positive right up to the knee, so the flatten
    reads abrupt."""
    t = np.clip((x - X0) / (XK - X0), 0.0, 1.0)
    return Y0 + (YC - Y0) * t ** 1.6


def pinstripe_layers(x, y, fy, yc, below,
                     offsets=(0.16, 0.35, 0.56, 0.80, 1.06),
                     width=(0.016, 0.004)):
    """Pinstripes that relax toward horizontal with depth: the mark morphs
    from capped curve at the top to a calm flat baseline below.
    Yields (index, line alpha map); the caller picks colors and opacity."""
    for i, off in enumerate(offsets):
        relax = (i + 1) / (len(offsets) + 1)
        shape = fy * (1 - relax) + yc * relax
        swell = 0.028 * np.sin(x * 1.8 + i * 1.4) * relax
        sd = np.abs(y - (shape - off) + swell)
        yield i, smoothstep(width[0], width[1], sd) * below


BADGE_GREEN = np.array([0.086, 0.64, 0.30])
BADGE_C = 0.52                      # badge center at (+C, -C): bottom-right
BADGE_R = 0.46


def draw_badge(col, mask, mx, my):
    """Green disc with a white check, ringed in ink to separate it from the
    glow behind it. Drawn in tile coordinates so ZOOM doesn't move it."""
    px = 2.0 / N
    d = np.hypot(mx - BADGE_C, my + BADGE_C)

    ring = smoothstep(BADGE_R * 1.18 + px, BADGE_R * 1.18 - px, d)
    col = over(col, INK[None, None, :], ring)
    disc = smoothstep(BADGE_R + px, BADGE_R - px, d)
    col = over(col, BADGE_GREEN[None, None, :], disc)

    pts = [
        (BADGE_C - 0.48 * BADGE_R, -BADGE_C - 0.02 * BADGE_R),
        (BADGE_C - 0.10 * BADGE_R, -BADGE_C - 0.40 * BADGE_R),
        (BADGE_C + 0.50 * BADGE_R, -BADGE_C + 0.36 * BADGE_R),
    ]
    dchk = sd_polyline(mx, my, pts)
    check = smoothstep(0.16 * BADGE_R + px, 0.16 * BADGE_R - px, dchk)
    col = over(col, np.array([1.0, 1.0, 1.0])[None, None, :], check)

    return col, np.maximum(mask, ring)


def render(badge=False):
    mx, my = grid()                 # tile coordinates (mask, vignette)
    x, y = mx * ZOOM, my * ZOOM     # content coordinates

    # dark ground with a slight radial falloff
    r = np.hypot(mx, my)
    col = INK[None, None, :] * (1.0 - 0.35 * smoothstep(0.2, 1.4, r))[..., None]
    col = col + NEARBLK[None, None, :] * 0.5

    fy = curve_y(x)
    dyc = y - fy                    # signed vertical offset; below curve < 0
    below = smoothstep(0.015, -0.015, dyc)

    # faint fill so the stripes sit in a body of tone, not a void
    col = col + GREEN[None, None, :] * (below * np.exp(dyc * 1.6) * 0.22)[..., None]

    for i, line in pinstripe_layers(x, y, fy, YC, below):
        alpha = 0.62 * (0.80 ** i)                       # fade with depth
        col = over(col, (GREEN * (0.9 - 0.09 * i))[None, None, :], line * alpha)

    # the curve itself: broad green bloom, lime glow, white-hot core
    cx = np.linspace(-1.3, 1.3, 90)
    d = sd_polyline(x, y, list(zip(cx, curve_y(cx))))
    col = col + GREEN[None, None, :] * np.exp(-7.0 * d)[..., None] * 0.65
    col = col + MINT[None, None, :] * np.exp(-24.0 * d)[..., None] * 0.85
    col = over(col, HOT[None, None, :], smoothstep(0.026, 0.008, d))

    # finish: vignette, subtle scanlines and grain, filmic tone curve
    col = col * (1.0 - 0.5 * smoothstep(0.35, 1.15, r))[..., None]
    col = col * (1.0 - 0.03 * (0.5 + 0.5 * np.sin(my * N * 180.0)))[..., None]
    rng = np.random.default_rng(7)
    col = col * (1.0 + 0.012 * (rng.random((N, N)) - 0.5) * 2.0)[..., None]
    col = col / (col + 0.85) * 1.85

    # rounded-square alpha mask (22% corner radius), antialiased
    rad = 0.22 * 2.0
    dx = np.maximum(np.abs(mx) - (1.0 - rad), 0.0)
    dy = np.maximum(np.abs(my) - (1.0 - rad), 0.0)
    mask_d = np.hypot(dx, dy) - rad
    px = 2.0 / N
    mask = np.clip(0.5 - mask_d / (1.5 * px), 0.0, 1.0)

    if badge:
        col, mask = draw_badge(col, mask, mx, my)

    rgba = np.concatenate([np.clip(col, 0, 1), mask[..., None]], axis=2)
    rgba = rgba.reshape(SIZE, SS, SIZE, SS, 4).mean(axis=(1, 3))
    return Image.fromarray((rgba * 255 + 0.5).astype(np.uint8), "RGBA")


def write(img, stem):
    img.save(os.path.join(OUT_DIR, stem + ".png"))
    frames = [img.resize((s, s), Image.LANCZOS) for s in ICO_SIZES]
    frames[-1].save(
        os.path.join(OUT_DIR, stem + ".ico"),
        format="ICO",
        append_images=frames[:-1],
        sizes=[(s, s) for s in ICO_SIZES],
    )
    print(f"wrote {stem}.png and {stem}.ico")


def main():
    write(render(), "icon")
    write(render(badge=True), "icon-active")


if __name__ == "__main__":
    main()
