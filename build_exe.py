"""Automated Standalone Executable Builder for Windows.

Packages Bravia PC Controller into a single, standalone Windows .exe
containing all dependencies (PySide6, gRPC, zeroconf, Pillow, assets, etc.)
so end users can run it without installing Python.

Usage:
  python3 build_exe.py
"""

from __future__ import annotations

import os
import shutil
import sys
from pathlib import Path
from PIL import Image

ROOT_DIR = Path(__file__).resolve().parent


def ensure_ico() -> Path:
    """Generate multi-resolution Windows .ico from idle.png (BRAVIA logo)."""
    ico_path = ROOT_DIR / "assets" / "app.ico"
    src_png = ROOT_DIR / "assets" / "icons" / "idle.png"
    if src_png.exists():
        im = Image.open(src_png)
        sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
        im.save(ico_path, format="ICO", sizes=sizes)
        print(f"Generated application icon (BRAVIA): {ico_path}")
    return ico_path


def build() -> None:
    import PyInstaller.__main__

    # Terminate any running instances first
    if os.name == "nt":
        os.system("taskkill /F /IM BraviaTheatrePC.exe >nul 2>&1")
        os.system("taskkill /F /IM BraviaTrayController.exe >nul 2>&1")

    # Clean previous build artifacts
    for d in [ROOT_DIR / "build", ROOT_DIR / "dist"]:
        if d.exists():
            shutil.rmtree(d, ignore_errors=True)

    ico_path = ensure_ico()
    entry_script = ROOT_DIR / "src" / "app.py"

    assets_sep = ";" if os.name == "nt" else ":"
    assets_icons = f"{ROOT_DIR / 'assets' / 'icons'}{assets_sep}assets/icons"

    args = [
        str(entry_script),
        "--name=BraviaTheatrePC",
        "--onefile",
        "--windowed",
        f"--icon={ico_path}",
        f"--add-data={assets_icons}",
        "--hidden-import=PySide6.QtCore",
        "--hidden-import=PySide6.QtGui",
        "--hidden-import=PySide6.QtWidgets",
        "--hidden-import=zeroconf",
        "--hidden-import=grpc",
        "--hidden-import=pybravia_connect",
        "--hidden-import=PIL",
        "--clean",
        "-y",
    ]

    print("=" * 60)
    print("Building standalone BraviaTheatrePC.exe with PyInstaller...")
    print("=" * 60)

    PyInstaller.__main__.run(args)

    dist_exe = ROOT_DIR / "dist" / "BraviaTheatrePC.exe"
    if dist_exe.exists():
        size_mb = dist_exe.stat().st_size / (1024 * 1024)
        print("\n" + "=" * 60)
        print(f"SUCCESS! Standalone Executable created:")
        print(f"  {dist_exe} ({size_mb:.1f} MB)")
        print("=" * 60)


if __name__ == "__main__":
    build()
