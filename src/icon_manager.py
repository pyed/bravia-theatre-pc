"""Tray icon generation and management.

Zero external image assets are required: every codec state has a crisp,
procedurally rendered RGBA badge drawn with Pillow at 2x supersampling
(256px master -> 64px tray size) for clean HiDPI edges.

If the user drops custom artwork into ``assets/icons/`` it takes priority:

    assets/icons/atmos.png   -> dolby_atmos_* codecs
    assets/icons/truehd.png  -> dolby_digital_truehd
    assets/icons/ddplus.png  -> dolby_digital_plus, dolby_mat
    assets/icons/dd.png      -> dolby_digital
    assets/icons/dtsx.png    -> dts_x, dts_x_master_audio
    assets/icons/dtshd.png   -> dts_hd_master_audio, dts_hd_high_resolution
    assets/icons/dts.png     -> standard dts, dts_es_*, dts_96_24, dts_express
    assets/icons/imax.png    -> imax_dts, imax_dts_x
    assets/icons/pcm.png     -> lpcm, multichannel_pcm (also accepts lpcm.png)
    assets/icons/aac.png     -> mpeg-2_aac, mpeg-4_aac
    assets/icons/360ra.png   -> 360ra (Sony 360 Reality Audio)
    assets/icons/idle.png    -> unknown / standby / idle

Asset files are cached after first load. A missing or unreadable asset
silently falls back to the generated badge, so the app always works.
"""

from __future__ import annotations

import logging
from functools import lru_cache
from pathlib import Path
from typing import Any, Optional, Tuple

from PIL import Image, ImageDraw, ImageFont

_LOGGER = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Codec taxonomy & brand colours
# ---------------------------------------------------------------------------

DOLBY_ATMOS_BLUE    = (0, 0x78, 0xD7, 255)     # #0078D7 (Official Dolby Blue)
DOLBY_DEEP_BLUE     = (0, 75, 155, 255)        # #004B9B (Deep Dolby Navy)
DTS_OBSIDIAN        = (22, 22, 22, 255)        # #161616 (Sony App Dark Graphite)
DTS_ORANGE          = (0xFF, 0x7A, 0x00, 255)  # #FF7A00
IMAX_CYAN           = (0x00, 0x62, 0xB8, 255)  # #0062B8
PCM_SLATE           = (44, 62, 80, 255)        # #2C3E50
AAC_BLUE_GRAY       = (55, 71, 79, 255)        # #37474F
RA360_TEAL          = (0x00, 0x83, 0x8F, 255)  # #00838F
IDLE_CHARCOAL       = (24, 24, 24, 255)        # #181818

#: Taxonomy: kind -> (raw wire tokens, generated label, badge colour, asset filename candidates)
CODEC_KINDS: dict[str, tuple[tuple[str, ...], str, tuple[int, int, int, int], tuple[str, ...]]] = {
    "atmos_truehd": (
        ("dolby_atmos_truehd",),
        "ATMOS TrueHD", DOLBY_ATMOS_BLUE, ("atmos_truehd.png", "truehd.png", "atmos.png"),
    ),
    "atmos": (
        ("dolby_atmos_mat", "dolby_atmos_digital_plus"),
        "ATMOS", DOLBY_ATMOS_BLUE, ("atmos.png",),
    ),
    "truehd": (
        ("dolby_digital_truehd",),
        "TrueHD", DOLBY_DEEP_BLUE, ("truehd.png", "atmos_truehd.png", "atmos.png"),
    ),
    "ddplus": (
        ("dolby_digital_plus", "dolby_mat"),
        "DD+", DOLBY_DEEP_BLUE, ("ddplus.png", "atmos.png"),
    ),
    "dd": (
        ("dolby_digital",),
        "DIGITAL", DOLBY_DEEP_BLUE, ("dd.png", "atmos.png"),
    ),
    "dtsx": (
        ("dts_x", "dts_x_master_audio", "imax_dts_x"),
        "DTS:X", DTS_OBSIDIAN, ("dtsx.png",),
    ),
    "dtshd": (
        ("dts_hd_master_audio", "dts_hd_high_resolution"),
        "DTS-HD", DTS_OBSIDIAN, ("dtshd.png", "dts.png"),
    ),
    "dts": (
        ("dts", "dts_es_6.1_matrix", "dts_es_6.1_discrete",
         "dts_es_8ch_discrete", "dts_96_24", "dts_express", "dts_unknown"),
        "DTS", DTS_OBSIDIAN, ("dts.png",),
    ),
    "imax": (
        ("imax_dts",),
        "IMAX", IMAX_CYAN, ("imax.png",),
    ),
    "pcm": (
        ("lpcm", "multichannel_pcm"),
        "LPCM", PCM_SLATE, ("lpcm.png", "pcm.png"),
    ),
    "aac": (
        ("mpeg-2_aac", "mpeg-4_aac"),
        "AAC", AAC_BLUE_GRAY, ("aac.png",),
    ),
    "360ra": (
        ("360ra",),
        "360RA", RA360_TEAL, ("360ra.png",),
    ),
    "idle": (
        ("unknown", "imax_off", ""),
        "BRAVIA", IDLE_CHARCOAL, ("idle.png",),
    ),
}


def normalize_codec(value: Any) -> str:
    """Coerce a raw wire codec value into its canonical lowercase token."""
    if value is None:
        return "unknown"
    token = str(value).strip().lower()
    return token or "unknown"


def classify_codec(value: Any) -> str:
    """Map a raw codec value to one of the canonical CODEC_KINDS."""
    token = normalize_codec(value)
    for kind, (values, _label, _color, _assets) in CODEC_KINDS.items():
        if token in values:
            return kind
    return "idle"


def human_readable_codec(value: Any, channel: Optional[str] = None) -> str:
    """Human-friendly label for tooltips and context menus."""
    token = normalize_codec(value)
    table = {
        "dolby_atmos_truehd": "Dolby Atmos (TrueHD)",
        "dolby_atmos_mat": "Dolby Atmos (MAT)",
        "dolby_atmos_digital_plus": "Dolby Atmos (DD+)",
        "dolby_digital_truehd": "Dolby TrueHD",
        "dolby_mat": "Dolby MAT",
        "dolby_digital_plus": "Dolby Digital Plus",
        "dolby_digital": "Dolby Digital",
        "dts_x_master_audio": "DTS:X Master Audio",
        "dts_x": "DTS:X",
        "dts_hd_master_audio": "DTS-HD Master Audio",
        "dts_hd_high_resolution": "DTS-HD High Resolution",
        "dts_es_6.1_matrix": "DTS-ES Matrix 6.1",
        "dts_es_6.1_discrete": "DTS-ES Discrete 6.1",
        "dts_es_8ch_discrete": "DTS-ES Discrete 8ch",
        "dts_96_24": "DTS 96/24",
        "dts_express": "DTS Express",
        "dts": "DTS",
        "dts_unknown": "DTS",
        "imax_dts_x": "IMAX Enhanced DTS:X",
        "imax_dts": "IMAX Enhanced DTS",
        "imax_off": "IMAX Off",
        "lpcm": "LPCM",
        "multichannel_pcm": "Multichannel PCM",
        "mpeg-2_aac": "MPEG-2 AAC",
        "mpeg-4_aac": "MPEG-4 AAC",
        "360ra": "360 Reality Audio",
        "unknown": "Unknown",
        "": "Unknown",
    }
    base = table.get(token, token.replace("_", " ").title() or "Unknown")
    if channel and str(channel).strip() and str(channel).lower() not in ("unknown", "none", ""):
        ch = str(channel).strip()
        # Append channel info if not redundant (e.g. "LPCM (2.0 ch)" or "Dolby Digital (5.1 ch)")
        if ch not in base:
            return f"{base} ({ch} ch)"
    return base


def _font(size: int, bold: bool = True) -> ImageFont.ImageFont:
    """Load a clean system font; degrades gracefully to the PIL default."""
    candidates = [
        "C:/Windows/Fonts/segoeui-bold.ttf" if bold else "C:/Windows/Fonts/segoeui.ttf",
        "C:/Windows/Fonts/seguiemj.ttf",
        "C:/Windows/Fonts/segoeui.ttf",
        "DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf",
    ]
    for path in candidates:
        try:
            return ImageFont.truetype(path, size)
        except OSError:
            continue
    try:
        return ImageFont.load_default(size=size)  # Pillow >= 9.5
    except TypeError:  # older Pillow
        return ImageFont.load_default()


@lru_cache(maxsize=64)
def _fit_font(text: str, max_width_px: int, start_size: int, min_size: int = 12):
    """Pick the largest bold font size whose rendered text fits max_width_px."""
    size = start_size
    while size >= min_size:
        font = _font(size)
        bbox = font.getbbox(text)
        if bbox is not None and (bbox[2] - bbox[0]) <= max_width_px:
            return font
        size -= 2
    return _font(min_size)


def _draw_badge(label: str, color: Tuple[int, int, int, int], size: int = 256) -> Image.Image:
    """Render a rounded-square badge with centred bold text at *size* px."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    radius = int(size * 0.22)
    draw.rounded_rectangle(
        [0, 0, size - 1, size - 1],
        radius=radius,
        fill=color,
        outline=(255, 255, 255, 220),
        width=max(2, size // 32),
    )
    text = label.upper()
    font = _fit_font(text, int(size * 0.82), int(size * 0.32))
    bbox = font.getbbox(text)
    w, h = bbox[2] - bbox[0], bbox[3] - bbox[1]
    draw.text(
        (size / 2 - w / 2 - bbox[0], size / 2 - h / 2 - bbox[1]),
        text,
        font=font,
        fill=(255, 255, 255, 255),
    )
    return img


class IconManager:
    """Loads custom assets when present, otherwise generates badges on demand.

    All public methods are safe to call from any thread (Pillow rendering is
    done at construction for all kinds; assets are probed once and cached).
    """

    SIZES = (16, 24, 32, 48, 64, 128, 256)

    def __init__(self, assets_dir: str | Path, base_size: int = 64) -> None:
        self._assets_dir = Path(assets_dir)
        self._base_size = base_size
        self._asset_cache: dict[str, Optional[Image.Image]] = {}
        self._asset_paths: dict[str, Optional[Path]] = {}
        self._generated: dict[str, Image.Image] = {}
        self._sizes: dict[int, dict[str, Image.Image]] = {
            s: {} for s in self.SIZES
        }
        for kind, (_values, label, color, _assets) in CODEC_KINDS.items():
            self._generated[kind] = _draw_badge(label, color, 256)

        # Pre-render every kind at every supported tray size
        for s in self.SIZES:
            for kind in self._generated:
                self._sizes[s][kind] = self._generated[kind].resize(
                    (s, s), Image.LANCZOS
                )
        self._probe_assets()

    # -- custom asset overrides ------------------------------------------------

    def _probe_assets(self) -> None:
        for kind, (_values, _label, _color, candidate_filenames) in CODEC_KINDS.items():
            loaded_img: Optional[Image.Image] = None
            found_path: Optional[Path] = None
            for filename in candidate_filenames:
                path = self._assets_dir / filename
                if path.exists():
                    loaded_img = self._load_asset(path)
                    if loaded_img is not None:
                        found_path = path
                        break
            self._asset_cache[kind] = loaded_img
            self._asset_paths[kind] = found_path

    @staticmethod
    def _load_asset(path: Path) -> Optional[Image.Image]:
        if not path.exists():
            return None
        try:
            img = Image.open(path).convert("RGBA")
            img.load()
            return img
        except Exception as exc:  # noqa: BLE001
            _LOGGER.warning(
                "Custom icon %s unreadable (%s) — using generated badge", path, exc
            )
            return None

    def reload_assets(self) -> None:
        """Re-scan assets/icons (e.g. after the user adds custom artwork)."""
        self._probe_assets()

    # -- public API ------------------------------------------------------------

    def image_for(self, kind: str, size: int = 64) -> Image.Image:
        """Return the RGBA icon for *kind* at *size* px.

        Custom asset wins if present, else the procedurally generated badge.
        """
        kind = kind if kind in CODEC_KINDS else "idle"
        custom = self._asset_cache.get(kind)
        if custom is not None:
            return custom.resize((size, size), Image.LANCZOS)

        if size in self._sizes:
            return self._sizes[size][kind]

        base = self._generated[kind]
        return base.resize((size, size), Image.LANCZOS)

    def export_master(self, kind: str, path: Path) -> Path:
        """Write the 256px master (custom or generated) to *path*."""
        img = self.image_for(kind, 256)
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        if path.suffix.lower() == ".ico":
            img.save(path, format="ICO", sizes=[(s, s) for s in (16, 24, 32, 48, 64, 128)])
        else:
            img.save(path, format="PNG")
        return path

    @property
    def active_assets(self) -> dict[str, Optional[Path]]:
        """Which kinds currently use a custom asset (for diagnostics/README)."""
        return dict(self._asset_paths)
