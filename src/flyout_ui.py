"""Windows 11 Fluent Design Quick Settings Flyout for BRAVIA Theatre.

Features:
- Modern dark-themed acrylic/mica-styled flyout with 12px rounded corners
- Top Codec Hero Card showing live codec brand logo, device name, and channels
- 2x2 Quick Action Tiles (Power, Night Mode, 360 Spatial Sound, Mute)
- Fluent volume slider with live dragging, mute toggle, direct jump, and numeric feedback
- Auto-positioning above the system tray and click-away auto-dismissal
"""

from __future__ import annotations

import ctypes
import logging
from pathlib import Path
from typing import Any, Callable, Optional

from PIL import Image
from PySide6.QtCore import (
    QEvent,
    QObject,
    QPoint,
    QPointF,
    QRect,
    QRectF,
    QSize,
    Qt,
    QTimer,
    Signal,
)
from PySide6.QtGui import (
    QBrush,
    QColor,
    QFont,
    QGuiApplication,
    QIcon,
    QImage,
    QMouseEvent,
    QPaintEvent,
    QPainter,
    QPainterPath,
    QPen,
    QPixmap,
    QWheelEvent,
)
from PySide6.QtWidgets import (
    QFrame,
    QGraphicsDropShadowEffect,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QSlider,
    QStyle,
    QStyleOptionSlider,
    QVBoxLayout,
    QWidget,
)

from . import APP_NAME
from .icon_manager import IconManager, classify_codec, human_readable_codec

_LOGGER = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# Windows 11 DWM Styling Helpers
# ---------------------------------------------------------------------------

def apply_windows_dark_mode(hwnd: int) -> None:
    """Ask Windows DWM for immersive dark mode and rounded corners."""
    try:
        dwm = ctypes.windll.dwmapi
        # DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 11 / Windows 10 20H1+)
        value = ctypes.c_int(1)
        dwm.DwmSetWindowAttribute(
            hwnd, 20, ctypes.byref(value), ctypes.sizeof(value)
        )
        # DWMWA_WINDOW_CORNER_PREFERENCE = 33 (2 = Rounded)
        corner_pref = ctypes.c_int(2)
        dwm.DwmSetWindowAttribute(
            hwnd, 33, ctypes.byref(corner_pref), ctypes.sizeof(corner_pref)
        )
    except Exception:  # noqa: BLE001
        pass


def pil_to_qpixmap(pil_img: Image.Image) -> QPixmap:
    """Convert a Pillow RGBA image to a QPixmap with a guaranteed deep copy."""
    img = pil_img.convert("RGBA")
    data = img.tobytes("raw", "RGBA")
    qimg = QImage(data, img.width, img.height, img.width * 4, QImage.Format.Format_RGBA8888).copy()
    return QPixmap.fromImage(qimg)


def pil_to_qicon(icons: IconManager, kind: str) -> QIcon:
    """Create a multi-resolution QIcon (16, 20, 24, 32, 48, 64) for sharp tray rendering."""
    qicon = QIcon()
    for size in (16, 20, 24, 32, 48, 64):
        pil_img = icons.image_for(kind, size).convert("RGBA")
        data = pil_img.tobytes("raw", "RGBA")
        qimg = QImage(data, pil_img.width, pil_img.height, pil_img.width * 4, QImage.Format.Format_RGBA8888).copy()
        qicon.addPixmap(QPixmap.fromImage(qimg))
    return qicon


# ---------------------------------------------------------------------------
# Fluent Slider with Direct Click Jump & Windows 11 Native Halo Thumb
# ---------------------------------------------------------------------------

class FluentSlider(QSlider):
    """Windows 11 style volume slider with direct-to-click jump, halo thumb, and smooth drag."""

    def __init__(self, orientation: Qt.Orientation = Qt.Orientation.Horizontal, parent: Optional[QWidget] = None) -> None:
        super().__init__(orientation, parent)
        self.setRange(0, 100)
        self.setFixedHeight(24)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setFocusPolicy(Qt.FocusPolicy.NoFocus)
        self.setStyleSheet("background: transparent; border: none;")
        self._hovered = False
        self._pressed = False

    def enterEvent(self, event: QEvent) -> None:
        self._hovered = True
        self.update()
        super().enterEvent(event)

    def leaveEvent(self, event: QEvent) -> None:
        self._hovered = False
        self.update()
        super().leaveEvent(event)

    def _val_from_x(self, x: float) -> int:
        margin = 10.0
        w = float(self.width()) - 2.0 * margin
        if w <= 0:
            return self.minimum()
        rel_x = max(0.0, min(w, x - margin))
        ratio = rel_x / w
        return int(round(self.minimum() + ratio * (self.maximum() - self.minimum())))

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self._pressed = True
            val = self._val_from_x(event.position().x())
            self.setValue(val)
            self.update()
            event.accept()
        super().mousePressEvent(event)

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        self._pressed = False
        self.update()
        super().mouseReleaseEvent(event)

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if event.buttons() & Qt.MouseButton.LeftButton:
            val = self._val_from_x(event.position().x())
            self.setValue(val)
            self.update()
            event.accept()
        super().mouseMoveEvent(event)

    def wheelEvent(self, event: QWheelEvent) -> None:
        delta = event.angleDelta().y()
        if delta != 0:
            step = 1 if delta > 0 else -1
            new_val = max(self.minimum(), min(self.maximum(), self.value() + step))
            self.setValue(new_val)
            self.update()
            event.accept()

    def paintEvent(self, event: QPaintEvent) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        margin = 10.0
        cy = self.height() / 2.0
        w = float(self.width()) - 2.0 * margin
        val_range = max(1, self.maximum() - self.minimum())
        val_ratio = (self.value() - self.minimum()) / float(val_range)
        handle_x = margin + val_ratio * w

        track_h = 4.5
        track_r = track_h / 2.0

        # 1. Unfilled Track (Right) - Windows 11 Soft Grey Track
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(QColor(150, 150, 150, 180))
        right_rect = QRectF(handle_x, cy - track_r, (margin + w) - handle_x, track_h)
        painter.drawRoundedRect(right_rect, track_r, track_r)

        # 2. Filled Track (Left) - Windows 11 Fluent Blue
        accent_color = QColor("#4CC2FF") if not self._pressed else QColor("#38AEE6")
        painter.setBrush(accent_color)
        left_rect = QRectF(margin, cy - track_r, handle_x - margin, track_h)
        painter.drawRoundedRect(left_rect, track_r, track_r)

        # 3. Outer Thumb Ring (Dark Halo Collar)
        outer_r = 10.0 if not self._pressed else 9.0
        outer_color = QColor("#333333") if not self._hovered else QColor("#3E3E3E")
        painter.setBrush(outer_color)
        painter.setPen(QPen(QColor(25, 25, 25, 200), 1.0))
        painter.drawEllipse(QPointF(handle_x, cy), outer_r, outer_r)

        # 4. Inner Thumb Core (Fluent Blue Circle)
        inner_r = 6.0 if not self._pressed else 5.0
        core_color = QColor("#4CC2FF") if not self._hovered else QColor("#70D3FF")
        if self._pressed:
            core_color = QColor("#38AEE6")
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(core_color)
        painter.drawEllipse(QPointF(handle_x, cy), inner_r, inner_r)


# ---------------------------------------------------------------------------
# Custom Quick Action Tile Widget
# ---------------------------------------------------------------------------

class QuickTile(QPushButton):
    """Windows 11 Quick Settings Tile Button with icon and status."""

    def __init__(
        self,
        icon_text: str,
        title: str,
        subtitle: str = "Off",
        on_click: Optional[Callable[[], None]] = None,
        parent: Optional[QWidget] = None,
    ) -> None:
        super().__init__(parent)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setFixedHeight(56)
        self._icon_text = icon_text
        self._title = title
        self._subtitle = subtitle
        self._is_active = False

        if on_click:
            self.clicked.connect(on_click)

        # Inner layout
        layout = QHBoxLayout(self)
        layout.setContentsMargins(12, 6, 12, 6)
        layout.setSpacing(10)

        # Icon Label using native Windows 11 Segoe Fluent Icons / Segoe MDL2
        self.icon_label = QLabel(self._icon_text)
        fluent_font = QFont("Segoe Fluent Icons", 16)
        if fluent_font.family() != "Segoe Fluent Icons":
            fluent_font = QFont("Segoe MDL2 Assets", 16)
        self.icon_label.setFont(fluent_font)
        self.icon_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.icon_label.setFixedSize(28, 28)
        layout.addWidget(self.icon_label)

        # Text container
        text_col = QVBoxLayout()
        text_col.setSpacing(1)
        text_col.setContentsMargins(0, 0, 0, 0)
        text_col.setAlignment(Qt.AlignmentFlag.AlignVCenter)

        self.title_label = QLabel(self._title)
        self.title_label.setFont(QFont("Segoe UI", 10, QFont.Weight.DemiBold))
        text_col.addWidget(self.title_label)

        self.sub_label = QLabel(self._subtitle)
        self.sub_label.setFont(QFont("Segoe UI", 8))
        text_col.addWidget(self.sub_label)

        layout.addLayout(text_col)
        layout.addStretch()

        self.update_style()

    def set_state(self, active: bool, subtitle: str = "") -> None:
        self._is_active = active
        if subtitle:
            self._subtitle = subtitle
        else:
            self._subtitle = "On" if active else "Off"
        self.sub_label.setText(self._subtitle)
        self.update_style()

    def update_style(self) -> None:
        if self._is_active:
            # Active accent style
            self.setStyleSheet(
                """
                QuickTile {
                    background-color: #0078D7;
                    border: 1px solid #0086F0;
                    border-radius: 8px;
                }
                QuickTile:hover {
                    background-color: #1084E3;
                }
                QuickTile:pressed {
                    background-color: #006CBE;
                }
                """
            )
            self.icon_label.setStyleSheet("color: #FFFFFF;")
            self.title_label.setStyleSheet("color: #FFFFFF;")
            self.sub_label.setStyleSheet("color: rgba(255, 255, 255, 0.85);")
        else:
            # Inactive dark style
            self.setStyleSheet(
                """
                QuickTile {
                    background-color: #2D2D2D;
                    border: 1px solid #383838;
                    border-radius: 8px;
                }
                QuickTile:hover {
                    background-color: #383838;
                    border: 1px solid #484848;
                }
                QuickTile:pressed {
                    background-color: #252525;
                }
                """
            )
            self.icon_label.setStyleSheet("color: #E0E0E0;")
            self.title_label.setStyleSheet("color: #FFFFFF;")
            self.sub_label.setStyleSheet("color: #9E9E9E;")


# ---------------------------------------------------------------------------
# Main Fluent Flyout Window
# ---------------------------------------------------------------------------

class FlyoutWindow(QWidget):
    """Windows 11 Quick Settings Flyout Panel."""

    state_changed_signal = Signal(dict, bool, str)

    def __init__(
        self,
        engine: Any,
        icons: IconManager,
        device_label: str = "Sony BRAVIA Theatre Bar 9",
        on_exit: Optional[Callable[[], None]] = None,
    ) -> None:
        super().__init__(None)
        self.engine = engine
        self.icons = icons
        self.device_label = device_label
        self.on_exit_callback = on_exit

        self._user_adjusting = False
        self._pending_volume_cmd: Optional[int] = None
        self._throttle_timer = QTimer(self)
        self._throttle_timer.setSingleShot(True)
        self._throttle_timer.setInterval(150)  # max 1 gRPC command per 150ms during active drag
        self._throttle_timer.timeout.connect(self._send_throttled_volume)

        self._setup_window()
        self._init_ui()

        self.state_changed_signal.connect(self._apply_state_safe)

        # Initialize immediately with current live engine snapshot
        initial_snap = self.engine.snapshot()
        self._apply_state_safe(initial_snap, conn_up=True, conn_why="")

    def _setup_window(self) -> None:
        self.setWindowFlags(
            Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
            | Qt.WindowType.Tool
        )
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.setFixedWidth(360)

    def _init_ui(self) -> None:
        # Outer container layout for drop shadow
        outer_layout = QVBoxLayout(self)
        outer_layout.setContentsMargins(12, 12, 12, 12)

        # Acrylic/Dark Background Container Frame
        self.container = QFrame(self)
        self.container.setObjectName("FlyoutContainer")
        self.container.setStyleSheet(
            """
            QFrame#FlyoutContainer {
                background-color: rgba(32, 32, 32, 0.96);
                border: 1px solid rgba(255, 255, 255, 0.12);
                border-radius: 12px;
            }
            """
        )

        # Window Drop Shadow
        shadow = QGraphicsDropShadowEffect(self)
        shadow.setBlurRadius(28)
        shadow.setColor(QColor(0, 0, 0, 160))
        shadow.setOffset(0, 8)
        self.container.setGraphicsEffect(shadow)

        # Inner Content Layout
        content_layout = QVBoxLayout(self.container)
        content_layout.setContentsMargins(16, 16, 16, 16)
        content_layout.setSpacing(12)

        # -------------------------------------------------------------
        # 1. Top Codec Hero Card
        # -------------------------------------------------------------
        hero_card = QFrame()
        hero_card.setStyleSheet(
            """
            QFrame {
                background-color: #262626;
                border: 1px solid #333333;
                border-radius: 8px;
            }
            """
        )
        hero_layout = QHBoxLayout(hero_card)
        hero_layout.setContentsMargins(12, 10, 12, 10)
        hero_layout.setSpacing(12)

        # Large Codec Badge Icon
        self.hero_icon = QLabel()
        self.hero_icon.setFixedSize(48, 48)
        self.hero_icon.setScaledContents(True)
        self.hero_icon.setPixmap(pil_to_qpixmap(self.icons.image_for("idle", 96)))
        hero_layout.addWidget(self.hero_icon)

        # Hero Text Column
        hero_text_col = QVBoxLayout()
        hero_text_col.setSpacing(2)
        hero_text_col.setContentsMargins(0, 0, 0, 0)

        self.device_title = QLabel(self.device_label)
        self.device_title.setFont(QFont("Segoe UI", 10, QFont.Weight.Bold))
        self.device_title.setStyleSheet("color: #FFFFFF; border: none; background: transparent;")
        hero_text_col.addWidget(self.device_title)

        self.codec_label = QLabel("Unknown Codec")
        self.codec_label.setFont(QFont("Segoe UI", 9))
        self.codec_label.setStyleSheet("color: #CCCCCC; border: none; background: transparent;")
        hero_text_col.addWidget(self.codec_label)

        # Live Status Tag
        status_row = QHBoxLayout()
        status_row.setSpacing(6)
        status_row.setContentsMargins(0, 2, 0, 0)

        self.status_dot = QLabel("●")
        self.status_dot.setFont(QFont("Segoe UI", 8))
        self.status_dot.setStyleSheet("color: #4CAF50; border: none; background: transparent;")
        status_row.addWidget(self.status_dot)

        self.status_text = QLabel("Connected")
        self.status_text.setFont(QFont("Segoe UI", 8))
        self.status_text.setStyleSheet("color: #9E9E9E; border: none; background: transparent;")
        status_row.addWidget(self.status_text)
        status_row.addStretch()

        hero_text_col.addLayout(status_row)
        hero_layout.addLayout(hero_text_col)
        content_layout.addWidget(hero_card)

        # -------------------------------------------------------------
        # 2. Quick Action Tiles Grid (2x2)
        # -------------------------------------------------------------
        tiles_grid = QVBoxLayout()
        tiles_grid.setSpacing(8)

        # Row 1: Power & Mute
        row1 = QHBoxLayout()
        row1.setSpacing(8)
        self.tile_power = QuickTile(
            "\uE7E8", "Power", "Off", on_click=self.engine.cmd_toggle_power
        )
        self.tile_mute = QuickTile(
            "\uE767", "Mute", "Unmuted", on_click=self.engine.cmd_toggle_mute
        )
        row1.addWidget(self.tile_power)
        row1.addWidget(self.tile_mute)
        tiles_grid.addLayout(row1)

        # Row 2: Night Mode & Sound Field (Music note with arches \uE8D6)
        row2 = QHBoxLayout()
        row2.setSpacing(8)
        self.tile_night = QuickTile(
            "\uEC46", "Night mode", "Off", on_click=self.engine.cmd_toggle_night_mode
        )
        self.tile_sf = QuickTile(
            "\uE8D6", "Sound field", "Off", on_click=self.engine.cmd_toggle_sound_field
        )
        row2.addWidget(self.tile_night)
        row2.addWidget(self.tile_sf)
        tiles_grid.addLayout(row2)

        content_layout.addLayout(tiles_grid)

        # -------------------------------------------------------------
        # 3. Modern Windows 11 Volume Slider
        # -------------------------------------------------------------
        volume_card = QFrame()
        volume_card.setStyleSheet(
            """
            QFrame {
                background-color: #262626;
                border: 1px solid #333333;
                border-radius: 8px;
            }
            """
        )
        volume_layout = QHBoxLayout(volume_card)
        volume_layout.setContentsMargins(12, 10, 12, 10)
        volume_layout.setSpacing(10)

        # Mute Icon Button using Segoe Fluent Icons
        self.volume_icon_btn = QPushButton("\uE767")
        fluent_vol_font = QFont("Segoe Fluent Icons", 14)
        if fluent_vol_font.family() != "Segoe Fluent Icons":
            fluent_vol_font = QFont("Segoe MDL2 Assets", 14)
        self.volume_icon_btn.setFont(fluent_vol_font)
        self.volume_icon_btn.setFixedSize(28, 28)
        self.volume_icon_btn.setCursor(Qt.CursorShape.PointingHandCursor)
        self.volume_icon_btn.setStyleSheet(
            """
            QPushButton {
                background: transparent;
                border: none;
                color: #FFFFFF;
            }
            QPushButton:hover {
                color: #60CDFF;
            }
            """
        )
        self.volume_icon_btn.clicked.connect(self.engine.cmd_toggle_mute)
        volume_layout.addWidget(self.volume_icon_btn)

        # Fluent Slider (Custom Windows 11 Fluent paint with halo thumb)
        self.volume_slider = FluentSlider(Qt.Orientation.Horizontal)
        self.volume_slider.sliderPressed.connect(self._on_slider_pressed)
        self.volume_slider.sliderReleased.connect(self._on_slider_released)
        self.volume_slider.valueChanged.connect(self._on_slider_moved)
        volume_layout.addWidget(self.volume_slider)

        # Numeric Volume Label
        self.volume_num = QLabel("0")
        self.volume_num.setFont(QFont("Segoe UI", 10, QFont.Weight.Bold))
        self.volume_num.setFixedWidth(32)
        self.volume_num.setAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        self.volume_num.setStyleSheet("color: #FFFFFF; border: none; background: transparent;")
        volume_layout.addWidget(self.volume_num)

        content_layout.addWidget(volume_card)

        # -------------------------------------------------------------
        # 4. Footer Row
        # -------------------------------------------------------------
        footer_layout = QHBoxLayout()
        footer_layout.setContentsMargins(4, 2, 4, 0)

        self.footer_info = QLabel(f"{self.engine.cfg.host}:{self.engine.cfg.port}")
        self.footer_info.setFont(QFont("Segoe UI", 8))
        self.footer_info.setStyleSheet("color: #777777; border: none; background: transparent;")
        footer_layout.addWidget(self.footer_info)

        footer_layout.addStretch()

        self.exit_btn = QPushButton("Exit App")
        self.exit_btn.setFont(QFont("Segoe UI", 8))
        self.exit_btn.setCursor(Qt.CursorShape.PointingHandCursor)
        self.exit_btn.setStyleSheet(
            """
            QPushButton {
                background-color: transparent;
                color: #A0A0A0;
                border: 1px solid #3D3D3D;
                border-radius: 4px;
                padding: 3px 10px;
            }
            QPushButton:hover {
                background-color: #383838;
                color: #FFFFFF;
                border: 1px solid #555555;
            }
            """
        )
        if self.on_exit_callback:
            self.exit_btn.clicked.connect(self.on_exit_callback)
        footer_layout.addWidget(self.exit_btn)

        content_layout.addLayout(footer_layout)
        outer_layout.addWidget(self.container)

    # ------------------------------------------------------------- Volume Drag
    def _on_slider_pressed(self) -> None:
        self._user_adjusting = True
        self._pending_volume_cmd = self.volume_slider.value()

    def _on_slider_released(self) -> None:
        self._throttle_timer.stop()
        final_val = self.volume_slider.value()
        self.engine.cmd_set_volume(final_val)
        # Keep user_adjusting True for 400ms to ignore stale server echo deltas
        QTimer.singleShot(400, self._clear_user_adjusting)

    def _clear_user_adjusting(self) -> None:
        self._user_adjusting = False

    def _on_slider_moved(self, value: int) -> None:
        self.volume_num.setText(str(value))
        if self._user_adjusting:
            self._pending_volume_cmd = value
            if not self._throttle_timer.isActive():
                self._throttle_timer.start(150)

    def _send_throttled_volume(self) -> None:
        if self._user_adjusting and self._pending_volume_cmd is not None:
            self.engine.cmd_set_volume(self._pending_volume_cmd)

    # ----------------------------------------------------------- State Updates
    def push_state(self, snap: dict[str, Any], conn_up: bool = True, conn_why: str = "") -> None:
        """Thread-safe state update called from background threads."""
        self.state_changed_signal.emit(snap, conn_up, conn_why)

    def _apply_state_safe(self, snap: dict[str, Any], conn_up: bool, conn_why: str) -> None:
        powered = bool(snap.get("power"))
        codec_raw = snap.get("codec")
        channel = snap.get("channel")
        vol = snap.get("volume")
        muted = bool(snap.get("mute"))
        night = bool(snap.get("night_mode"))
        sf = bool(snap.get("sound_field"))

        # 1. Connection & Power Status
        if not conn_up:
            self.status_dot.setStyleSheet("color: #F44336; border: none; background: transparent;")
            self.status_text.setText("Disconnected" + (f" ({conn_why})" if conn_why else ""))
            self.hero_icon.setPixmap(pil_to_qpixmap(self.icons.image_for("idle", 96)))
            self.codec_label.setText("Offline")
            self.tile_power.set_state(False, "Offline")
            self.tile_mute.setEnabled(False)
            self.tile_night.setEnabled(False)
            self.tile_sf.setEnabled(False)
            self.volume_slider.setEnabled(False)
            return

        if not powered:
            self.status_dot.setStyleSheet("color: #757575; border: none; background: transparent;")
            self.status_text.setText("Standby")
            self.hero_icon.setPixmap(pil_to_qpixmap(self.icons.image_for("idle", 96)))
            self.codec_label.setText("Unit is in Standby")
            self.tile_power.set_state(False, "Standby")
            self.tile_mute.setEnabled(False)
            self.tile_night.setEnabled(False)
            self.tile_sf.setEnabled(False)
            self.volume_slider.setEnabled(False)
        else:
            self.status_dot.setStyleSheet("color: #4CAF50; border: none; background: transparent;")
            self.status_text.setText("Active" if not muted else "Muted")
            kind = classify_codec(codec_raw)
            self.hero_icon.setPixmap(pil_to_qpixmap(self.icons.image_for(kind, 96)))
            self.codec_label.setText(human_readable_codec(codec_raw, channel))

            self.tile_power.set_state(True, "On")
            self.tile_mute.setEnabled(True)
            self.tile_mute.set_state(muted, "Muted" if muted else "Unmuted")
            self.tile_mute.icon_label.setText("\uE74F" if muted else "\uE767")
            self.tile_night.setEnabled(True)
            self.tile_night.set_state(night, "On" if night else "Off")
            self.tile_sf.setEnabled(True)
            self.tile_sf.set_state(sf, "On" if sf else "Off")
            self.volume_slider.setEnabled(True)

        # Volume slider sync (only update when user is NOT currently dragging)
        if not self._user_adjusting and vol is not None:
            v = int(vol)
            self.volume_slider.blockSignals(True)
            self.volume_slider.setValue(v)
            self.volume_slider.blockSignals(False)
            self.volume_num.setText(str(v))

        if muted:
            self.volume_icon_btn.setText("\uE74F")
            self.volume_icon_btn.setStyleSheet("QPushButton { color: #FF9800; border: none; background: transparent; }")
        else:
            self.volume_icon_btn.setText("\uE767")
            self.volume_icon_btn.setStyleSheet("QPushButton { color: #FFFFFF; border: none; background: transparent; }")

    # ------------------------------------------------------------- Positioning
    def position_at_tray(self) -> None:
        """Position the flyout right above the taskbar notification tray."""
        if self.layout():
            self.layout().activate()
        self.adjustSize()
        screen = QGuiApplication.primaryScreen()
        if not screen:
            return
        geom = screen.availableGeometry()
        margin = 12
        x = geom.right() - self.width() - margin
        y = geom.bottom() - self.height() - margin
        self.move(x, y)

    def show_at_tray(self) -> None:
        # Refresh state from engine right before showing
        snap = self.engine.snapshot()
        self._apply_state_safe(snap, conn_up=True, conn_why="")
        self.position_at_tray()
        self.show()
        self.position_at_tray()  # Re-assert exact taskbar coordinates on first realization
        self.raise_()
        self.activateWindow()
        apply_windows_dark_mode(int(self.winId()))

    def toggle_at_tray(self) -> None:
        if self.isVisible():
            self.hide()
        else:
            self.show_at_tray()

    def changeEvent(self, event: QEvent) -> None:
        # Auto-hide when user clicks outside the flyout window
        if event.type() == QEvent.Type.ActivationChange and not self.isActiveWindow():
            self.hide()
        super().changeEvent(event)
