"""
Generates the GitHub social preview card (social-preview.png, 1280x640) in the
icon's visual language. The V/F curve rises from the bottom-left and
hard-flattens into a glowing cap line across the lower third; the project name
and tagline sit large in the dark space above it, underlined by the cap.

Upload manually: repo Settings -> Social preview (GitHub has no API for it).

Requires Python 3 with numpy and pillow:  python render_social_preview.py
"""

import os

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from render_icon import (
    GREEN,
    HOT,
    INK,
    MINT,
    NEARBLK,
    over,
    pinstripe_layers,
    sd_polyline,
    smoothstep,
)

OUT_DIR = os.path.dirname(os.path.abspath(__file__))
W, H = 1280, 640
SS = 2
NX, NY = W * SS, H * SS

TITLE = "simple-nvidia-undervolt"
TAGLINE = "Overclock & undervolt NVIDIA GPUs from the Windows command line"

# V/F curve in card coordinates (x in [-2, 2], y in [-1, 1], y up):
# rise starts at (X0, Y0) near the bottom-left corner, hard-flattens at the
# knee (XK, YC) into the cap line that underlines the text
X0, XK = -2.05, -1.15
Y0, YC = -1.02, -0.20


def curve_y(x):
    t = np.clip((x - X0) / (XK - X0), 0.0, 1.0)
    return Y0 + (YC - Y0) * t ** 1.6


def render_background():
    xs = np.linspace(-2.0, 2.0, NX)
    ys = np.linspace(1.0, -1.0, NY)
    x, y = np.meshgrid(xs, ys)

    # dark ground with a slight radial falloff
    r = np.hypot(x * 0.5, y)
    col = INK[None, None, :] * (1.0 - 0.35 * smoothstep(0.2, 1.4, r))[..., None]
    col = col + NEARBLK[None, None, :] * 0.5

    fy = curve_y(x)
    dyc = y - fy
    below = smoothstep(0.015, -0.015, dyc)

    # the stripe field dissolves before the bottom edge instead of being cut
    fade = smoothstep(-1.02, -0.80, y)

    # faint fill so the stripes sit in a body of tone, not a void
    col = col + GREEN[None, None, :] * (below * fade * np.exp(dyc * 1.6) * 0.18)[..., None]

    for i, line in pinstripe_layers(x, y, fy, YC, below,
                                    offsets=(0.13, 0.28, 0.45, 0.64, 0.86)):
        alpha = 0.45 * (0.80 ** i)
        col = over(col, (GREEN * (0.9 - 0.09 * i))[None, None, :], line * fade * alpha)

    # the curve itself: broad green bloom, lime glow, white-hot core; the
    # bloom spills mostly downward so it stays off the tagline above
    cx = np.linspace(-2.2, 2.2, 140)
    d = sd_polyline(x, y, list(zip(cx, curve_y(cx))))
    spill = 1.0 - 0.60 * smoothstep(0.0, 0.06, dyc)
    col = col + GREEN[None, None, :] * (np.exp(-7.0 * d) * spill * 0.60)[..., None]
    col = col + MINT[None, None, :] * (np.exp(-24.0 * d) * spill * 0.80)[..., None]
    col = over(col, HOT[None, None, :], smoothstep(0.022, 0.007, d))

    # finish: vignette, subtle scanlines and grain, filmic tone curve
    col = col * (1.0 - 0.45 * smoothstep(0.55, 1.35, r))[..., None]
    col = col * (1.0 - 0.03 * (0.5 + 0.5 * np.sin(y * NY * 90.0)))[..., None]
    rng = np.random.default_rng(7)
    col = col * (1.0 + 0.012 * (rng.random((NY, NX)) - 0.5) * 2.0)[..., None]
    col = col / (col + 0.85) * 1.85

    col = np.clip(col, 0, 1).reshape(H, SS, W, SS, 3).mean(axis=(1, 3))
    return Image.fromarray((col * 255 + 0.5).astype(np.uint8))


def font(path, size):
    return ImageFont.truetype(os.path.join(os.environ["WINDIR"], "Fonts", path), size)


def main():
    img = render_background()
    d = ImageDraw.Draw(img)

    # text block in the dark field above the cap line
    for top, text, f, fill in [
        (84, TITLE, font("consolab.ttf", 88), (240, 255, 247)),
        (226, TAGLINE, font("segoeui.ttf", 36), (198, 234, 212)),
    ]:
        w = d.textlength(text, font=f)
        d.text(((W - w) / 2, top), text, font=f, fill=fill)

    out = os.path.join(OUT_DIR, "social-preview.png")
    img.save(out)
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
