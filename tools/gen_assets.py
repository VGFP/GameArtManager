"""Generates all UWP tile/logo/splash assets required by Package.appxmanifest.

Run:  python tools/gen_assets.py
Output: GameArtManager/Assets/*.png
"""
import os
from PIL import Image, ImageDraw

OUT = os.path.join(os.path.dirname(__file__), "..", "GameArtManager", "Assets")
os.makedirs(OUT, exist_ok=True)

DARK = (20, 26, 34, 255)
TEAL = (90, 169, 230, 255)
GREEN = (79, 176, 109, 255)
AMBER = (217, 161, 59, 255)


def rounded(draw, box, radius, fill):
    draw.rounded_rectangle(box, radius=radius, fill=fill)


def make_icon(size: int) -> Image.Image:
    """Square app icon: dark tile with a 2x2 art grid, one tile accent."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    m = size * 0.04
    # dark rounded background
    rounded(d, (0, 0, size - 1, size - 1), size * 0.18, DARK)
    # 2x2 grid of tiles
    pad = size * 0.10
    tile = (size - pad * 3) / 2
    r = tile * 0.25
    positions = [
        (pad, pad, TEAL),
        (pad * 2 + tile, pad, GREEN),
        (pad, pad * 2 + tile, AMBER),
    ]
    for x, y, color in positions:
        rounded(d, (x, y, x + tile, y + tile), r, color)
    # fourth tile: image glyph (mountain + sun placeholder)
    x, y = pad * 2 + tile, pad * 2 + tile
    rounded(d, (x, y, x + tile, y + tile), r, (52, 60, 70, 255))
    cx, cy = x + tile * 0.5, y + tile * 0.42
    sun_r = tile * 0.14
    d.ellipse((cx - sun_r, cy - sun_r, cx + sun_r, cy + sun_r), fill=(230, 236, 242, 255))
    d.polygon(
        [
            (x + tile * 0.18, y + tile * 0.86),
            (x + tile * 0.42, y + tile * 0.52),
            (x + tile * 0.62, y + tile * 0.80),
            (x + tile * 0.72, y + tile * 0.66),
            (x + tile * 0.88, y + tile * 0.86),
        ],
        fill=(230, 236, 242, 255),
    )
    return img


def save(img: Image.Image, name: str):
    img.save(os.path.join(OUT, name))
    print("wrote", name)


icon = make_icon(512)

# Square44x44 logo (+ targetsize + unplated for taskbar/widget icons)
for scale, px in [(100, 44), (125, 55), (150, 66), (200, 88), (400, 176)]:
    save(icon.resize((px, px), Image.LANCZOS), f"Square44x44Logo.scale-{scale}.png")
for px in (16, 24, 30, 32, 36, 40, 44, 48, 256):
    save(icon.resize((px, px), Image.LANCZOS), f"Square44x44Logo.targetsize-{px}.png")
    if px in (16, 24, 32, 48, 256):
        save(icon.resize((px, px), Image.LANCZOS), f"Square44x44Logo.altform-unplated_targetsize-{px}.png")
save(icon.resize((88, 88), Image.LANCZOS), "LockScreenLogo.scale-200.png")

# Standard tiles
for scale, px in [(100, 150), (125, 188), (150, 225), (200, 300), (400, 600)]:
    save(icon.resize((px, px), Image.LANCZOS), f"Square150x150Logo.scale-{scale}.png")
for scale, px in [(100, 71), (125, 89), (150, 107), (200, 142), (400, 284)]:
    save(icon.resize((px, px), Image.LANCZOS), f"SmallTile.scale-{scale}.png")
for scale, px in [(100, 310), (125, 388), (150, 465), (200, 620), (400, 1240)]:
    save(icon.resize((px, px), Image.LANCZOS), f"LargeTile.scale-{scale}.png")

# Wide tiles: 310x150 canvas, dark bg + centered icon (exact required pixel sizes)
wide_base = Image.new("RGBA", (1240, 600), DARK)
inner = icon.resize((400, 400), Image.LANCZOS)
wide_base.alpha_composite(inner, (420, 100))
for scale, w, h in [(100, 310, 150), (125, 388, 188), (150, 465, 225), (200, 620, 300), (400, 1240, 600)]:
    save(wide_base.resize((w, h), Image.LANCZOS), f"Wide310x150Logo.scale-{scale}.png")

# Store logo
for scale, px in [(100, 50), (125, 63), (150, 75), (200, 100), (400, 200)]:
    save(icon.resize((px, px), Image.LANCZOS), f"StoreLogo.scale-{scale}.png")

# Splash: 620x300 dark with icon
splash_base = Image.new("RGBA", (2480, 1200), DARK)
inner = icon.resize((420, 420), Image.LANCZOS)
splash_base.alpha_composite(inner, (1030, 390))
for scale, w in [(100, 620), (125, 775), (150, 930), (200, 1240), (400, 2480)]:
    h = int(w * 300 / 620)
    save(splash_base.resize((w, h), Image.LANCZOS), f"SplashScreen.scale-{scale}.png")

print("done")
