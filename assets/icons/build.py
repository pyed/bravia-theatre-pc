"""Build authentic, Sony-app matching tray and UI icons for bravia-tray.

Based on official brand assets and Sony Connect app UI:
- Dolby Suite (Atmos, Atmos TrueHD, TrueHD, DD+, DD): Official [D D] Dolby ATMOS on light card tile
- DTS Suite (DTS:X, DTS-HD, DTS): Exact authentic horizontal marks from Sony app on sleek dark graphite
- LPCM / 360RA / AAC / BRAVIA: High-contrast, clean typography badges

Outputs:
  assets/icons/<kind>.png   (256x256 master, RGBA)
  assets/icons/lpcm.png     (alias for pcm.png)
  assets/icons/_preview.png (contact sheet at 128/64/32/24/16)
"""

from __future__ import annotations

import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(HERE, "raw")

MASTER = 256
RADIUS = 56  # Rounded rectangle corner radius


def _load_font(size: int, bold: bool = True) -> ImageFont.FreeTypeFont:
    candidates = [
        "C:/Windows/Fonts/segoeui-bold.ttf" if bold else "C:/Windows/Fonts/segoeui.ttf",
        "C:/Windows/Fonts/seguiemj.ttf",
        "C:/Windows/Fonts/segoeui.ttf",
        "DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf",
    ]
    for p in candidates:
        try:
            return ImageFont.truetype(p, size)
        except OSError:
            continue
    return ImageFont.load_default()


def _fit_font(text: str, max_w: int, start_size: int, min_size: int = 10) -> ImageFont.FreeTypeFont:
    size = start_size
    while size >= min_size:
        f = _load_font(size, bold=True)
        bb = f.getbbox(text)
        if bb and (bb[2] - bb[0]) <= max_w:
            return f
        size -= 2
    return _load_font(min_size, bold=True)


def _rounded_tile(size: int, color: tuple) -> Image.Image:
    tile = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(tile)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=RADIUS, fill=color)
    return tile


def _place_mark(tile: Image.Image, mark_img: Image.Image, box_w: int, box_h: int, center_y: int = MASTER // 2, color: tuple = (255, 255, 255, 255)) -> Image.Image:
    img = mark_img.convert("RGBA")
    bbox = img.getchannel("A").getbbox()
    if bbox:
        img = img.crop(bbox)
    alpha = img.getchannel("A")
    solid = Image.new("RGBA", img.size, color)
    solid.putalpha(alpha)
    img = solid
    ratio = min(box_w / img.width, box_h / img.height)
    nw, nh = max(1, int(img.width * ratio)), max(1, int(img.height * ratio))
    resized = img.resize((nw, nh), Image.LANCZOS)
    tile.alpha_composite(resized, (MASTER // 2 - nw // 2, center_y - nh // 2))
    return tile


def checker(size: int, sq: int = 16) -> Image.Image:
    base = Image.new("RGBA", (size, size), (235, 235, 235, 255))
    d = ImageDraw.Draw(base)
    for y in range(0, size, sq):
        for x in range(0, size, sq):
            if (x // sq + y // sq) % 2:
                d.rectangle([x, y, x + sq - 1, y + sq - 1], fill=(205, 205, 205, 255))
    return base


def main() -> None:
    # Colors
    DARK_DTS     = (22, 22, 22, 255)     # Sleek Dark Graphite #161616 (Sony App DTS style)
    SLATE_PCM    = (44, 62, 80, 255)     # Studio Slate #2C3E50
    CYAN_IMAX    = (0, 98, 184, 255)     # IMAX Blue #0062B8
    TEAL_360     = (0, 131, 143, 255)    # Sony 360RA Teal #00838F
    CHARCOAL_AAC = (55, 71, 79, 255)     # AAC Charcoal Blue #37474F
    MATTE_IDLE   = (24, 24, 24, 255)     # Idle Black #181818

    # Load source marks
    dolby_master = Image.open(os.path.join(RAW, "_sony_atmos_tile.png")).convert("RGBA") if os.path.exists(os.path.join(RAW, "_sony_atmos_tile.png")) else Image.open(os.path.join(HERE, "atmos.png")).convert("RGBA")
    sony_dtsx  = Image.open(os.path.join(RAW, "_sony_dtsx.png"))
    sony_dtshd = Image.open(os.path.join(RAW, "_sony_dtshd.png"))
    imax_raw   = Image.open(os.path.join(RAW, "imax.png"))

    badges: dict[str, Image.Image] = {}

    # 1. Dolby Family (Atmos, Atmos TrueHD, TrueHD, DD+, DD)
    badges["atmos"] = dolby_master
    badges["atmos_truehd"] = dolby_master
    badges["truehd"] = dolby_master
    badges["ddplus"] = dolby_master
    badges["dd"] = dolby_master

    # 2. dtsx (DTS:X from Sony app): Exact horizontal mark in white on dark graphite
    t_dtsx = _rounded_tile(MASTER, DARK_DTS)
    _place_mark(t_dtsx, sony_dtsx, 210, 80, center_y=MASTER // 2, color=(255, 255, 255, 255))
    badges["dtsx"] = t_dtsx

    # 3. dtshd (DTS-HD from Sony app): Exact horizontal mark in white on dark graphite
    t_dtshd = _rounded_tile(MASTER, DARK_DTS)
    _place_mark(t_dtshd, sony_dtshd, 210, 68, center_y=MASTER // 2, color=(255, 255, 255, 255))
    badges["dtshd"] = t_dtshd

    # 4. dts (Classic DTS): Clean horizontal mark in white on dark graphite
    t_dts = _rounded_tile(MASTER, DARK_DTS)
    d = ImageDraw.Draw(t_dts)
    f_dts = _fit_font("dts", int(MASTER * 0.80), 82)
    bb = f_dts.getbbox("dts")
    tw, th = bb[2] - bb[0], bb[3] - bb[1]
    d.text((MASTER // 2 - tw // 2 - bb[0], MASTER // 2 - th // 2 - bb[1]), "dts", font=f_dts, fill=(255, 255, 255, 255))
    badges["dts"] = t_dts

    # 5. imax (IMAX Enhanced)
    t_imax = _rounded_tile(MASTER, CYAN_IMAX)
    _place_mark(t_imax, imax_raw, 215, 60, center_y=MASTER // 2, color=(255, 255, 255, 255))
    badges["imax"] = t_imax

    # 6. pcm / lpcm
    t_pcm = _rounded_tile(MASTER, SLATE_PCM)
    d = ImageDraw.Draw(t_pcm)
    f = _fit_font("LPCM", int(MASTER * 0.84), 64, min_size=18)
    bb = f.getbbox("LPCM")
    tw, th = bb[2] - bb[0], bb[3] - bb[1]
    d.text((MASTER // 2 - tw // 2 - bb[0], MASTER // 2 - th // 2 - bb[1]), "LPCM", font=f, fill=(255, 255, 255, 255))
    badges["pcm"] = t_pcm

    # 7. aac
    t_aac = _rounded_tile(MASTER, CHARCOAL_AAC)
    d = ImageDraw.Draw(t_aac)
    f = _fit_font("AAC", int(MASTER * 0.84), 64, min_size=18)
    bb = f.getbbox("AAC")
    tw, th = bb[2] - bb[0], bb[3] - bb[1]
    d.text((MASTER // 2 - tw // 2 - bb[0], MASTER // 2 - th // 2 - bb[1]), "AAC", font=f, fill=(255, 255, 255, 255))
    badges["aac"] = t_aac

    # 8. 360ra
    t_360 = _rounded_tile(MASTER, TEAL_360)
    d = ImageDraw.Draw(t_360)
    f = _fit_font("360RA", int(MASTER * 0.84), 58, min_size=18)
    bb = f.getbbox("360RA")
    tw, th = bb[2] - bb[0], bb[3] - bb[1]
    d.text((MASTER // 2 - tw // 2 - bb[0], MASTER // 2 - th // 2 - bb[1]), "360RA", font=f, fill=(255, 255, 255, 255))
    badges["360ra"] = t_360

    # 9. idle
    t_idle = _rounded_tile(MASTER, MATTE_IDLE)
    d = ImageDraw.Draw(t_idle)
    f = _fit_font("BRAVIA", int(MASTER * 0.84), 48, min_size=18)
    bb = f.getbbox("BRAVIA")
    tw, th = bb[2] - bb[0], bb[3] - bb[1]
    d.text((MASTER // 2 - tw // 2 - bb[0], MASTER // 2 - th // 2 - bb[1]), "BRAVIA", font=f, fill=(138, 138, 138, 255))
    badges["idle"] = t_idle

    for name, img in badges.items():
        out = os.path.join(HERE, f"{name}.png")
        img.save(out)
        print("wrote", out, img.size)
        if name == "pcm":
            lpcm_out = os.path.join(HERE, "lpcm.png")
            img.save(lpcm_out)
            print("wrote", lpcm_out, img.size)

    # Save Dolby master reference to raw for reproducibility
    dolby_master.save(os.path.join(RAW, "_sony_atmos_tile.png"))

    # Contact sheet preview
    sizes = [128, 64, 32, 24, 16]
    pad = 8
    cols = len(sizes)
    rows = len(badges)
    sheet = Image.new("RGBA", (cols * (140 + pad), rows * (140 + pad)), (250, 250, 250, 255))
    for r, (name, master_img) in enumerate(badges.items()):
        for c, s in enumerate(sizes):
            cell = checker(140)
            img = master_img.resize((s, s), Image.LANCZOS)
            cell.alpha_composite(img, ((140 - s) // 2, (140 - s) // 2))
            sheet.alpha_composite(cell, (c * (140 + pad) + pad, r * (140 + pad) + pad))

    prev = os.path.join(HERE, "_preview.png")
    sheet.save(prev)
    print("wrote", prev, sheet.size)


if __name__ == "__main__":
    main()
