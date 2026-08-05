"""Generates InventoryTracker.ico: a crate glyph on a dark HUD-blue rounded square."""
from PIL import Image, ImageDraw

SIZES = [16, 24, 32, 48, 64, 128, 256]

BG_TOP = (16, 24, 38)
BG_BOTTOM = (26, 38, 58)
ACCENT = (86, 214, 255)      # HUD cyan
ACCENT_DIM = (54, 140, 176)
EDGE = (10, 14, 22)


def rounded_mask(size, radius):
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return mask


def vgradient(size, top, bottom):
    img = Image.new("RGB", (size, size))
    for y in range(size):
        t = y / max(size - 1, 1)
        row = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
        for x in range(size):
            img.putpixel((x, y), row)
    return img


def draw_crate(draw, s):
    # Isometric crate: top diamond + two side faces, drawn in a cyan wire/fill style
    cx, cy = s * 0.5, s * 0.5
    w, h = s * 0.62, s * 0.62
    top = (cx, cy - h * 0.5)
    right = (cx + w * 0.5, cy - h * 0.08)
    bottom = (cx, cy + h * 0.5)
    left = (cx - w * 0.5, cy - h * 0.08)
    mid_r = (cx + w * 0.5, cy + h * 0.14)
    mid_l = (cx - w * 0.5, cy + h * 0.14)

    # left face (dim), right face (bright), top face (brightest)
    draw.polygon([left, cx_bottom_pt(cx, cy, h), bottom, mid_l], fill=blend(ACCENT_DIM, 0.55))
    draw.polygon([right, mid_r, bottom, cx_bottom_pt(cx, cy, h)], fill=blend(ACCENT, 0.75))
    draw.polygon([top, right, cx_bottom_pt(cx, cy, h), left], fill=blend(ACCENT, 1.0))

    outline = EDGE
    lw = max(1, int(s * 0.018))
    draw.line([top, right, mid_r, bottom, mid_l, left, top], fill=outline, width=lw, joint="curve")
    draw.line([left, cx_bottom_pt(cx, cy, h)], fill=outline, width=lw)
    draw.line([right, cx_bottom_pt(cx, cy, h)], fill=outline, width=lw)
    draw.line([top, cx_bottom_pt(cx, cy, h)], fill=outline, width=max(1, int(s * 0.012)))


def cx_bottom_pt(cx, cy, h):
    return (cx, cy + h * 0.02)


def blend(color, factor):
    return tuple(min(255, int(c * factor + 20 * (1 - factor))) for c in color)


def build(size):
    base = vgradient(size, BG_TOP, BG_BOTTOM).convert("RGBA")
    mask = rounded_mask(size, radius=int(size * 0.22))
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.paste(base, (0, 0), mask)

    draw = ImageDraw.Draw(canvas)
    draw_crate(draw, size)

    # subtle border
    ImageDraw.Draw(canvas).rounded_rectangle(
        [0, 0, size - 1, size - 1], radius=int(size * 0.22),
        outline=(6, 9, 14, 255), width=max(1, int(size * 0.02)))
    return canvas


images = [build(s) for s in SIZES]
images[-1].save(
    "InventoryTracker.ico",
    format="ICO",
    sizes=[(s, s) for s in SIZES],
    append_images=images[:-1],
)
print("wrote InventoryTracker.ico")
