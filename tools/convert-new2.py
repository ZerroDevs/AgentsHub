"""Cut the second batch of loose 1024x1024 stickers (assets/AgAssets/New2) into place.

Same background removal as convert-new.py; this batch is the redraws and missing pieces listed
in assets/icons/WANTED.md. Every source file is one icon, so the only work is cut, trim, scale.

    python tools/convert-new2.py
"""
import os
import re

import numpy as np
from PIL import Image, ImageDraw

SRC = "assets/AgAssets/New2"
UI = "ui/src/assets/icons"
SOFT_LO, SOFT_HI = 16, 44  # channel distance band that becomes a feathered alpha edge

# index -> (destinations, output size, hard alpha). Hard alpha keeps a flat white mark white
# instead of letting the feathered edge turn grey when it is drawn at 16 px.
MAP = {
    1: (["tools/document.png"], 128, False),
    2: (["objects/package.png"], 128, False),
    3: (["states/running.png"], 128, False),
    4: (["sections/appearance.png"], 128, False),
    5: (["actions/close.png"], 128, False),
    6: (["tools/wifi.png"], 128, False),
    7: (["brand/logo-white.png"], 256, True),
}


def index_of(name):
    m = re.search(r"photo_(\d+)", name)
    return int(m.group(1)) if m else 0


def source_files():
    found = {}
    for n in os.listdir(SRC):
        if n.lower().endswith((".jpg", ".jpeg", ".png")) and not n.startswith("review"):
            found.setdefault(index_of(n), os.path.join(SRC, n))
    return found


def background_color(arr):
    corners = []
    for ys in (slice(0, 24), slice(-24, None)):
        for xs in (slice(0, 24), slice(-24, None)):
            corners.append(arr[ys, xs].reshape(-1, 3))
    return np.median(np.concatenate(corners, axis=0), axis=0)


def cut(path, hard):
    im = Image.open(path).convert("RGB")
    arr = np.asarray(im).astype(np.int16)
    bg = background_color(arr)
    dist = np.abs(arr - bg).max(axis=2).astype(np.float32)
    alpha = np.clip((dist - SOFT_LO) / (SOFT_HI - SOFT_LO), 0.0, 1.0)
    if hard:
        alpha = np.where(alpha > 0.45, 1.0, 0.0)

    rgba = np.dstack([arr.astype(np.uint8), (alpha * 255).astype(np.uint8)])
    out = Image.fromarray(rgba, "RGBA")
    if hard:
        flat = Image.new("RGBA", out.size, (255, 255, 255, 255))
        flat.putalpha(out.getchannel("A"))
        out = flat

    box = out.getchannel("A").point(lambda v: 255 if v > 12 else 0).getbbox()
    if not box:
        raise SystemExit(f"nothing left after cut: {path}")
    out = out.crop(box)

    pad = int(max(out.size) * 0.04)
    canvas = Image.new("RGBA", (out.width + pad * 2, out.height + pad * 2), (0, 0, 0, 0))
    canvas.paste(out, (pad, pad))
    return canvas


def square(img, size):
    side = max(img.size)
    plate = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    plate.paste(img, ((side - img.width) // 2, (side - img.height) // 2))
    return plate.resize((size, size), Image.LANCZOS)


ICO_SIZES = (16, 20, 24, 32, 48, 64, 128, 256)

# fileName -> dot colour. The tray answers "is the hub up" without opening the menu.
TRAY_STATES = {
    "tray-running.ico": (34, 197, 94),
    "tray-warn.ico": (245, 158, 11),
    "tray-offline.ico": (239, 68, 68),
}


def save_ico(png_path, ico_path):
    """A tray icon needs every small size RGBa-crisp, not one bitmap scaled by Windows."""
    src = Image.open(png_path).convert("RGBA")
    base = square(src, 256)
    base.save(ico_path, sizes=[(s, s) for s in ICO_SIZES])
    print(f"ico  {ico_path} from {png_path}")


def save_state_ico(png_path, ico_path, colour):
    """The same mark with one dot in the bottom-right corner, ringed in ink so it reads at 16 px."""
    base = square(Image.open(png_path).convert("RGBA"), 256)
    draw = ImageDraw.Draw(base)
    dot, margin, ring = 86, 6, 10
    x1 = 256 - dot - margin
    draw.ellipse((x1 - ring, x1 - ring, x1 + dot + ring - 1, x1 + dot + ring - 1), fill=(11, 32, 48, 255))
    draw.ellipse((x1, x1, x1 + dot - 1, x1 + dot - 1), fill=(*colour, 255))
    base.save(ico_path, sizes=[(s, s) for s in ICO_SIZES])
    print(f"ico  {ico_path} {colour}")


def contact_sheet(items, path):
    cell, cols = 140, 8
    rows = (len(items) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * cell, rows * (cell + 18)), (10, 10, 10))
    draw = ImageDraw.Draw(sheet)
    for i, (label, im) in enumerate(items):
        x, y = (i % cols) * cell, (i // cols) * (cell + 18)
        sheet.paste(im.convert("RGB"), (x + (cell - im.width) // 2, y + 8))
        draw.text((x + 3, y + cell - 4), label, fill=(235, 235, 235))
    sheet.save(path)
    print("sheet:", path, sheet.size)


def main():
    files = source_files()
    made = []
    for idx, (dests, size, hard) in sorted(MAP.items()):
        src = files.get(idx)
        if not src:
            print(f"missing index {idx}")
            continue
        art = square(cut(src, hard), size)
        for rel in dests:
            dest = os.path.join(UI, rel)
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            art.save(dest)
            made.append((rel, art))
            print(f"{idx:2} {os.path.basename(src):30} -> {dest:56} {art.width}x{art.height}")

    # The installer and desktop shortcut graphics are 512 px plates, not UI icons, so they stay
    # out of the contact sheet.
    plate = files.get(8)
    if plate:
        art = square(cut(plate, False), 512)
        for p in ("assets/app-3d.png", "assets/cli-3d.png"):
            art.save(p)
            print(f" 8 {os.path.basename(plate):30} -> {p:56} {art.width}x{art.height}")

    white = os.path.join(UI, "brand", "logo-white.png")
    if os.path.exists(white):
        save_ico(white, "assets/tray.ico")
        for name, colour in TRAY_STATES.items():
            save_state_ico(white, os.path.join("assets", name), colour)

    contact_sheet(made, "assets/icons/review/new2.png")


if __name__ == "__main__":
    main()
