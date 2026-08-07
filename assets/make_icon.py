"""Generates InventoryTracker.ico: flat crate + data grid + checkmark on slate navy."""
from PIL import Image, ImageDraw

SIZES = [16, 24, 32, 48, 64, 128, 256]

BG = (14, 20, 32)          # deep slate navy
BORDER = (6, 9, 14)
CYAN = (77, 224, 255)      # HUD cyan
CYAN_DIM = (58, 120, 140)  # grid lines
AMBER = (255, 176, 64)     # accent / checkmark


def rounded_mask(size, radius):
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return mask


def draw_crate(draw, s):
    # Flat frontal container: square outline with a 3x3 data grid, cyan on navy.
    cx, cy = s * 0.5, s * 0.5
    half = s * 0.30
    x0, y0 = cx - half, cy - half
    x1, y1 = cx + half, cy + half

    lw = max(1, round(s * 0.045))
    grid_lw = max(1, round(s * 0.018))

    # container face, flat fill (slightly lighter than bg, no gradient)
    draw.rectangle([x0, y0, x1, y1], fill=(22, 32, 48))

    # data grid: two interior verticals + horizontals
    for t in (1 / 3, 2 / 3):
        gx = x0 + (x1 - x0) * t
        draw.line([(gx, y0), (gx, y1)], fill=CYAN_DIM, width=grid_lw)
        gy = y0 + (y1 - y0) * t
        draw.line([(x0, gy), (x1, gy)], fill=CYAN_DIM, width=grid_lw)

    # outline last, on top of grid, clean edge
    draw.rectangle([x0, y0, x1, y1], outline=CYAN, width=lw)

    # amber checkmark, intersecting the container's lower-right corner
    ck_lw = max(1, round(s * 0.055))
    p1 = (cx - half * 0.55, cy + half * 0.10)
    p2 = (cx - half * 0.05, cy + half * 0.65)
    p3 = (cx + half * 1.05, cy - half * 0.55)
    draw.line([p1, p2, p3], fill=AMBER, width=ck_lw, joint="curve")
    # round the joints/caps so the stroke reads clean at small sizes
    for pt in (p1, p2, p3):
        r = ck_lw / 2
        draw.ellipse([pt[0] - r, pt[1] - r, pt[0] + r, pt[1] + r], fill=AMBER)


def build(size):
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    mask = rounded_mask(size, radius=int(size * 0.22))
    bg = Image.new("RGBA", (size, size), BG)
    canvas.paste(bg, (0, 0), mask)

    draw = ImageDraw.Draw(canvas)
    draw_crate(draw, size)

    ImageDraw.Draw(canvas).rounded_rectangle(
        [0, 0, size - 1, size - 1], radius=int(size * 0.22),
        outline=BORDER, width=max(1, int(size * 0.02)))
    return canvas


images = [build(s) for s in SIZES]
images[-1].save(
    "InventoryTracker.ico",
    format="ICO",
    sizes=[(s, s) for s in SIZES],
    append_images=images[:-1],
)
print("wrote InventoryTracker.ico")
