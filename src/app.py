"""Windows system-tray front end and Fluent Flyout for the BRAVIA Theatre.

Run:  python3 src/app.py   (or:  python3 -m src.app)

Responsibilities
----------------
* Resolve configuration (config.json, CLI overrides, session_keys.json)
* Auto-discover the soundbar (mDNS -> config.json fallback)
* Spin up the gRPC engine (background daemon thread)
* Own the native Windows 11 system tray icon + tooltip + context menu
* Launch the modern Windows 11 Fluent Design Quick Settings Flyout (volume slider, action tiles, codec card)
* Translate user interactions into thread-safe soundbar gRPC commands
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import sys
import threading
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Optional

# --- Direct-execution bootstrap ------------------------------------------
if __name__ == "__main__" and not __package__:
    sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), os.pardir)))
    from src import app as _pkg_app
    _pkg_app.main()
    raise SystemExit()

from PySide6.QtCore import QObject, Qt, Signal
from PySide6.QtGui import QAction, QIcon
from PySide6.QtWidgets import QApplication, QMenu, QSystemTrayIcon

from . import APP_NAME
from .discovery import DEFAULT_PORT, DiscoveryResult, discover_one
from .flyout_ui import FlyoutWindow, pil_to_qicon, pil_to_qpixmap
from .grpc_engine import EngineConfig, GrpcEngine
from .icon_manager import (
    IconManager,
    classify_codec,
    human_readable_codec,
)

_LOGGER = logging.getLogger("bravia.app")

if getattr(sys, "frozen", False) and hasattr(sys, "_MEIPASS"):
    BUNDLE_DIR = Path(sys._MEIPASS)
    ROOT_DIR = Path(sys.executable).resolve().parent
    ASSETS_DIR = BUNDLE_DIR / "assets" / "icons"
else:
    ROOT_DIR = Path(__file__).resolve().parent.parent
    ASSETS_DIR = ROOT_DIR / "assets" / "icons"


# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(
        prog="bravia-theatre-pc",
        description="Sony BRAVIA Theatre PC Controller (HT-A9000 and friends)",
    )
    p.add_argument("--host", default=None, help="Soundbar IP (overrides config.json / mDNS)")
    p.add_argument("--port", type=int, default=None, help="gRPC port (default 55051)")
    p.add_argument("--keys", default=None, help="Path to session_keys.json")
    p.add_argument("--config", default=None, help="Path to config.json")
    p.add_argument(
        "--no-discovery", action="store_true",
        help="Skip mDNS; use the configured IP only",
    )
    p.add_argument("--verbose", action="store_true", help="Debug logging to console")
    return p.parse_args()


def _read_config(path: Path) -> dict[str, Any]:
    if path.exists():
        try:
            with path.open(encoding="utf-8") as fh:
                data = json.load(fh)
            if isinstance(data, dict):
                return data
        except (json.JSONDecodeError, OSError) as exc:
            _LOGGER.warning("Could not read %s: %s", path, exc)
    return {}


@dataclass
class AppConfig:
    raw: dict
    host: str
    port: int
    keys_path: Path
    log_file: Path
    discovery_timeout: float
    reconnect_min: float
    reconnect_max: float
    menu_refresh_ms: int


def resolve_config(args: argparse.Namespace) -> AppConfig:
    config_path = Path(args.config) if args.config else ROOT_DIR / "config.json"
    raw = _read_config(config_path)

    host = (args.host or raw.get("host") or "").strip()
    port = int(args.port or raw.get("port") or DEFAULT_PORT)
    keys_path = (
        Path(args.keys)
        if args.keys
        else (ROOT_DIR / "session_keys.json")
    )
    return AppConfig(
        raw=raw,
        host=host,
        port=port,
        keys_path=keys_path,
        log_file=ROOT_DIR / "bravia_theatre_pc.log",
        discovery_timeout=float(raw.get("discovery_timeout", 6)),
        reconnect_min=float(raw.get("reconnect_min_seconds", 5)),
        reconnect_max=float(raw.get("reconnect_max_seconds", 60)),
        menu_refresh_ms=int(raw.get("menu_refresh_ms", 500)),
    )


def load_keys(keys_path: Path) -> dict:
    from pybravia_connect import load_credentials

    if not keys_path.exists():
        raise FileNotFoundError(
            f"Session keys not found at {keys_path}\n"
            "\n"
            "Generate them first (required, one-time per account):\n"
            f"  bravia-connect-keys --login --open -o {keys_path.name}\n"
            "\n"
            "Complete the Sony sign-in in the browser window that opens, "
            "then run this app again."
        )
    creds = load_credentials(keys_path)
    missing = [k for k in ("device_id", "hmac_key") if not creds.get(k)]
    if missing:
        raise ValueError(
            f"session_keys.json is missing required field(s): {', '.join(missing)}. "
            "Re-run: bravia-connect-keys --login --open -o session_keys.json"
        )
    return creds


# ---------------------------------------------------------------------------
# Qt Tray & Flyout UI Controller
# ---------------------------------------------------------------------------

class TrayBridge(QObject):
    """Bridge for receiving engine updates on background threads and emitting Qt signals."""

    state_updated = Signal(dict, bool, str)


class TrayUI:
    """Manages QSystemTrayIcon, the context menu, and the Flyout window."""

    def __init__(self, app: QApplication, engine: GrpcEngine, cfg: AppConfig, device_label: str) -> None:
        self.app = app
        self.engine = engine
        self.cfg = cfg
        self.device_label = device_label
        self.icons = IconManager(ASSETS_DIR, base_size=64)

        self._conn_up = True
        self._conn_why = ""

        # Bridge for safe cross-thread signals
        self.bridge = TrayBridge()
        self.bridge.state_updated.connect(self._on_state_signal)

        # Create Flyout Window
        self.flyout = FlyoutWindow(
            engine=self.engine,
            icons=self.icons,
            device_label=self.device_label,
            on_exit=self.on_quit,
        )

        # Initial live snapshot setup
        snap = self.engine.snapshot()
        powered = bool(snap.get("power"))
        if powered:
            kind = classify_codec(snap.get("codec"))
            initial_icon = pil_to_qicon(self.icons, kind)
            codec_str = human_readable_codec(snap.get("codec"), snap.get("channel"))
            vol = snap.get("volume", 0)
            initial_title = f"Playing: {codec_str} | Vol: {vol}"
        else:
            initial_icon = pil_to_qicon(self.icons, "idle")
            initial_title = f"{APP_NAME} — starting…"

        # Create System Tray Icon
        self.tray = QSystemTrayIcon()
        self.tray.setIcon(initial_icon)
        self.tray.setToolTip(initial_title)

        # Context Menu
        self.menu = QMenu()
        self._build_context_menu()
        self.tray.setContextMenu(self.menu)

        # Tray Click Activation: Left-click toggles Fluent flyout
        self.tray.activated.connect(self._on_tray_activated)

        self.tray.show()

    def _build_context_menu(self) -> None:
        self.menu.clear()
        check_path = (ASSETS_DIR / "check.png").as_posix()
        self.menu.setStyleSheet(
            f"""
            QMenu {{
                background-color: #202020;
                color: #FFFFFF;
                border: 1px solid #383838;
                border-radius: 8px;
                padding: 6px;
            }}
            QMenu::item {{
                padding: 6px 24px 6px 28px;
                border-radius: 4px;
            }}
            QMenu::item:selected {{
                background-color: #0078D7;
            }}
            QMenu::indicator {{
                width: 14px;
                height: 14px;
                left: 8px;
            }}
            QMenu::indicator:checked {{
                image: url("{check_path}");
            }}
            QMenu::indicator:unchecked {{
                image: none;
            }}
            QMenu::separator {{
                height: 1px;
                background: #333333;
                margin: 4px 8px;
            }}
            """
        )

        snap = self.engine.snapshot()
        powered = snap.get("power", False)
        codec = snap.get("codec") or "unknown"
        channel = snap.get("channel")
        vol = snap.get("volume", 0)

        # Header status row
        if powered:
            status_text = f"{human_readable_codec(codec, channel)} | Vol: {vol}"
        else:
            status_text = "Standby"

        action_header = QAction(status_text, self.menu)
        action_header.setEnabled(False)
        self.menu.addAction(action_header)

        # Quick Settings / Flyout trigger
        action_flyout = QAction("Open Quick Controls", self.menu)
        action_flyout.triggered.connect(self.flyout.toggle_at_tray)
        self.menu.addAction(action_flyout)

        self.menu.addSeparator()

        # Windows Auto-Start Toggle
        from .autostart import (
            is_autostart_enabled,
            is_tray_promoted,
            set_autostart,
            set_tray_promoted,
        )
        act_autostart = QAction("Start with Windows", self.menu)
        act_autostart.setCheckable(True)
        act_autostart.setChecked(is_autostart_enabled())
        act_autostart.toggled.connect(set_autostart)
        self.menu.addAction(act_autostart)

        # Always Show on Taskbar (next to clock) Toggle
        act_promote = QAction("Always show on taskbar", self.menu)
        act_promote.setCheckable(True)
        act_promote.setChecked(is_tray_promoted())
        act_promote.toggled.connect(self._on_toggle_promote)
        self.menu.addAction(act_promote)

        # Sony Account Re-Authentication Dialog
        act_auth = QAction("Sony Account Setup…", self.menu)
        act_auth.triggered.connect(self._on_open_auth_dialog)
        self.menu.addAction(act_auth)

        self.menu.addSeparator()

        act_exit = QAction("Exit", self.menu)
        act_exit.triggered.connect(self.on_quit)
        self.menu.addAction(act_exit)

    def _on_toggle_promote(self, enabled: bool) -> None:
        from .autostart import set_tray_promoted
        set_tray_promoted(enabled)

    def _on_open_auth_dialog(self) -> None:
        from .auth_dialog import SonyAuthDialog
        dlg = SonyAuthDialog(self.cfg.keys_path, parent=self.flyout)
        dlg.exec()

    def _on_tray_activated(self, reason: QSystemTrayIcon.ActivationReason) -> None:
        # Left-click or double-click opens the Windows 11 Fluent Flyout
        if reason in (
            QSystemTrayIcon.ActivationReason.Trigger,
            QSystemTrayIcon.ActivationReason.DoubleClick,
        ):
            self.flyout.toggle_at_tray()

    def _on_state_signal(self, snap: dict[str, Any], conn_up: bool, conn_why: str) -> None:
        self._conn_up = conn_up
        self._conn_why = conn_why

        # Update Flyout
        self.flyout.push_state(snap, conn_up, conn_why)

        # Update Tray Icon & Tooltip
        if not conn_up:
            self.tray.setIcon(pil_to_qicon(self.icons, "idle"))
            self.tray.setToolTip(f"{APP_NAME} — disconnected" + (f" ({conn_why})" if conn_why else ""))
        else:
            powered = bool(snap.get("power"))
            if powered:
                kind = classify_codec(snap.get("codec"))
                self.tray.setIcon(pil_to_qicon(self.icons, kind))
                codec_str = human_readable_codec(snap.get("codec"), snap.get("channel"))
                vol = snap.get("volume", 0)
                muted_str = " (Muted)" if snap.get("mute") else ""
                self.tray.setToolTip(f"Playing: {codec_str} | Vol: {vol}{muted_str}")
            else:
                self.tray.setIcon(pil_to_qicon(self.icons, "idle"))
                self.tray.setToolTip(f"Standby | {APP_NAME}")

        # Rebuild right-click context menu
        self._build_context_menu()

    def on_update(self, snap: dict[str, Any]) -> None:
        self.bridge.state_updated.emit(snap, self._conn_up, self._conn_why)

    def on_connection_state(self, up: bool, why: str) -> None:
        self._conn_up = up
        self._conn_why = why
        snap = self.engine.snapshot()
        self.bridge.state_updated.emit(snap, up, why)

    def on_quit(self) -> None:
        self.tray.hide()
        self.flyout.hide()
        self.engine.stop()
        self.app.quit()


# ---------------------------------------------------------------------------
# Application Entry Point
# ---------------------------------------------------------------------------

class BraviaApp:
    def __init__(self, args: argparse.Namespace) -> None:
        self.cfg = resolve_config(args)
        self._args = args

    def _resolve_device(self, keys: dict) -> tuple[DiscoveryResult, str]:
        if self._args.no_discovery or self.cfg.host:
            host = self.cfg.host
            return (
                DiscoveryResult(
                    name="configured",
                    hostname=host,
                    ip=host,
                    port=self.cfg.port,
                    txt={},
                ),
                "config",
            )
        result, source = discover_one(
            timeout=self.cfg.discovery_timeout,
            fallback_host=self.cfg.host or None,
            fallback_port=self.cfg.port,
        )
        return result, source

    def run(self) -> None:
        logging.basicConfig(
            level=logging.DEBUG if self._args.verbose else logging.INFO,
            format="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
            stream=sys.stderr,
        )

        # Initialize Qt Application upfront
        app = QApplication.instance() or QApplication(sys.argv)
        app.setQuitOnLastWindowClosed(False)

        # Promote system tray icon in Windows taskbar
        from .autostart import promote_tray_icon
        promote_tray_icon()

        # Load session keys or launch GUI setup wizard if missing
        try:
            keys = load_keys(self.cfg.keys_path)
        except Exception:
            from PySide6.QtWidgets import QDialog
            from .auth_dialog import SonyAuthDialog
            auth_dlg = SonyAuthDialog(self.cfg.keys_path)
            if auth_dlg.exec() != QDialog.DialogCode.Accepted:
                print("Setup cancelled by user.", file=sys.stderr)
                sys.exit(0)
            keys = load_keys(self.cfg.keys_path)

        print("BRAVIA Theatre PC")
        print("-" * 40)
        print(f"Discovering soundbar via mDNS ({self.cfg.discovery_timeout:g}s window) …")
        result, source = self._resolve_device(keys)
        if result is None:
            print(
                "\nNo soundbar found.\n"
                "  1. Confirm the BRAVIA Theatre is powered on and on your LAN.\n"
                f"  2. Set its IP in {self.cfg.keys_path.parent / 'config.json'} "
                '("host": "192.168.x.x").\n'
                "  3. Re-run."
            )
            raise SystemExit(2)

        device_name = result.name if result.name and result.name != "configured" else "Sony BRAVIA Theatre"
        if source.startswith("config"):
            print(f"Using configured IP {result.ip}:{result.port}")
        else:
            print(f"Discovered via mDNS: {result.label}")

        ui_holder: dict[str, Optional[TrayUI]] = {"ui": None}

        def on_update(snap: dict) -> None:
            ui = ui_holder.get("ui")
            if ui is not None:
                ui.on_update(snap)

        def on_connection_state(up: bool, why: str) -> None:
            ui = ui_holder.get("ui")
            if ui is not None:
                ui.on_connection_state(up, why)

        engine = GrpcEngine(
            EngineConfig(
                host=result.ip or result.hostname.split(".")[0],
                port=result.port,
                credentials=keys,
                log_file=self.cfg.log_file,
                reconnect_min=self.cfg.reconnect_min,
                reconnect_max=self.cfg.reconnect_max,
            ),
            on_update=on_update,
            on_connection_state=on_connection_state,
        )

        ui = TrayUI(app, engine, self.cfg, device_name)
        ui_holder["ui"] = ui

        print(f"Connected target: {result.ip or result.hostname}:{result.port}")
        print("Tray icon active — left-click for Windows 11 Quick Controls, right-click for menu.\n")

        # Run Qt Event Loop
        try:
            sys.exit(app.exec())
        finally:
            engine.stop()


def main() -> None:
    args = parse_args()
    try:
        BraviaApp(args).run()
    except FileNotFoundError as exc:
        print(f"\nERROR: {exc}", file=sys.stderr)
        raise SystemExit(1)
    except ValueError as exc:
        print(f"\nERROR: {exc}", file=sys.stderr)
        raise SystemExit(1)


if __name__ == "__main__":
    main()
