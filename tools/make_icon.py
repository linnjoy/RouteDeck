"""Draws the RouteDeck icon (concept A: forked path) and writes assets/RouteDeck.ico + PNGs.

Each size is drawn separately at 4x and downsampled, with fewer details and thicker strokes
for small sizes so it stays legible in the title bar and tray. Run: python tools/make_icon.py
"""
from pathlib import Path
from PIL import Image, ImageDraw

BLUE = (24, 95, 165, 255)        # #185FA5 tile
WHITE = (255, 255, 255, 255)
TEAL = (93, 202, 165, 255)       # #5DCAA5 chosen destination
FADED = (181, 212, 244, 140)     # #B5D4F4 at ~55%: the road not taken
SS = 4
ASSETS = Path(__file__).resolve().parent.parent / "assets"


def bezier(p0, p1, p2, p3, steps=48):
    pts = []
    for i in range(steps + 1):
        t = i / steps
        u = 1 - t
        pts.append((u**3 * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t**3 * p3[0],
                    u**3 * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t**3 * p3[1]))
    return pts


def stroke(draw, pts, width, color, k):
    """Round-capped stroke drawn by stamping discs along the path: no join artifacts at any size."""
    r = width * k / 2
    scaled = [(x * k, y * k) for x, y in pts]
    for (x0, y0), (x1, y1) in zip(scaled, scaled[1:]):
        steps = max(1, int(((x1 - x0) ** 2 + (y1 - y0) ** 2) ** 0.5 / 0.75))
        for i in range(steps + 1):
            x = x0 + (x1 - x0) * i / steps
            y = y0 + (y1 - y0) * i / steps
            draw.ellipse((x - r, y - r, x + r, y + r), fill=color)


def dot(draw, cx, cy, r, color, k):
    draw.ellipse(((cx - r) * k, (cy - r) * k, (cx + r) * k, (cy + r) * k), fill=color)


def render(size):
    full = size >= 48
    medium = 24 <= size < 48
    width = 20 if full else 28 if medium else 40
    target_r = 26 if full else 34 if medium else 44
    start_y = 196 if size > 16 else 200

    canvas = size * SS
    k = canvas / 256
    img = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    inset = 8 if size >= 32 else 4
    d.rounded_rectangle((inset * k, inset * k, (256 - inset) * k, (256 - inset) * k), radius=56 * k, fill=BLUE)

    if full:
        faded = Image.new("RGBA", img.size, (0, 0, 0, 0))
        fd = ImageDraw.Draw(faded)
        stroke(fd, [(70, 150), (70, 70)], width, FADED, k)
        dot(fd, 70, 66, 16, FADED, k)
        img = Image.alpha_composite(img, faded)
        d = ImageDraw.Draw(img)

    path = [(70, start_y), (70, 150)] + bezier((70, 150), (70, 120), (92, 104), (122, 104))[1:] + [(150, 104)]
    stroke(d, path, width, WHITE, k)
    if full:
        dot(d, 70, 196, 18, WHITE, k)
    dot(d, 186, 104, target_r, TEAL, k)
    if full:
        dot(d, 186, 104, 11, BLUE, k)

    return img.resize((size, size), Image.LANCZOS)


def main():
    ASSETS.mkdir(exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = {s: render(s) for s in sizes}
    images[256].save(ASSETS / "RouteDeck.ico", sizes=[(s, s) for s in sizes],
                     append_images=[images[s] for s in sizes if s != 256])
    for s in (32, 64, 256):
        images[s].save(ASSETS / f"RouteDeck-{s}.png")
    render(1024).save(ASSETS / "RouteDeck-1024.png")
    # Preview sheet: every size on light and dark backgrounds.
    sheet = Image.new("RGBA", (sum(sizes) + 20 * len(sizes) + 20, 2 * 296), (243, 243, 243, 255))
    dark = Image.new("RGBA", (sheet.width, 296), (32, 32, 32, 255))
    sheet.paste(dark, (0, 296))
    x = 20
    for s in sizes:
        for row in (0, 296):
            sheet.alpha_composite(images[s], (x, row + 148 - s // 2))
        x += s + 20
    sheet.save(ASSETS / "icon-preview.png")
    print("RouteDeck.ico sizes:", sorted(Image.open(ASSETS / "RouteDeck.ico").ico.sizes()))


if __name__ == "__main__":
    main()
