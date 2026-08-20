"""Native Windows 11 First-Time Setup & Sony Account Sign-In Dialog.

Provides a clean, friendly GUI wizard so users never have to run terminal
commands or manually craft JSON files to authenticate with Sony Cloud.
"""

from __future__ import annotations

import logging
import webbrowser
from pathlib import Path
from typing import Optional

from PySide6.QtCore import Qt
from PySide6.QtGui import QFont
from PySide6.QtWidgets import (
    QCheckBox,
    QDialog,
    QFrame,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QPushButton,
    QVBoxLayout,
    QWidget,
)

from pybravia_connect import (
    complete_oauth_flow,
    start_oauth_login,
    write_credentials,
)
from .autostart import open_taskbar_settings, promote_tray_icon, set_autostart

_LOGGER = logging.getLogger("bravia.auth_dialog")


class SonyAuthDialog(QDialog):
    """First-time setup and Sony Cloud OAuth authentication wizard."""

    def __init__(self, output_path: Path, parent: Optional[QWidget] = None) -> None:
        super().__init__(parent)
        self.output_path = output_path
        self._auth_url = ""
        self._code_verifier = ""
        self._expected_state = ""

        self.setWindowTitle("Sony BRAVIA Theatre — Account Setup")
        self.setFixedSize(560, 560)
        self.setWindowFlags(self.windowFlags() & ~Qt.WindowType.WindowContextHelpButtonHint)

        self._setup_ui()
        self._apply_theme()

    def _setup_ui(self) -> None:
        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(24, 20, 24, 20)
        main_layout.setSpacing(14)

        # Header
        title = QLabel("Sony Account Setup")
        title.setFont(QFont("Segoe UI", 13, QFont.Weight.Bold))
        main_layout.addWidget(title)

        desc = QLabel(
            "To control your BRAVIA Theatre soundbar over your local network, "
            "sign in once with your Sony account to generate local authentication keys."
        )
        desc.setWordWrap(True)
        desc.setStyleSheet("color: #CCCCCC; font-size: 9.5pt;")
        main_layout.addWidget(desc)

        # Step 1 Card
        step1_box = QFrame()
        step1_box.setStyleSheet("background: #282828; border-radius: 8px; padding: 6px;")
        s1_layout = QVBoxLayout(step1_box)
        s1_layout.setSpacing(6)

        s1_label = QLabel("Step 1: Open Sony Sign-In Page")
        s1_label.setFont(QFont("Segoe UI", 10, QFont.Weight.DemiBold))
        s1_layout.addWidget(s1_label)

        self.btn_open_browser = QPushButton("Open Sony Sign-In in Browser")
        self.btn_open_browser.setFixedHeight(34)
        self.btn_open_browser.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_open_browser.clicked.connect(self._on_open_browser)
        s1_layout.addWidget(self.btn_open_browser)
        main_layout.addWidget(step1_box)

        # Step 2 Card (Detailed DevTools Guide)
        step2_box = QFrame()
        step2_box.setStyleSheet("background: #282828; border-radius: 8px; padding: 6px;")
        s2_layout = QVBoxLayout(step2_box)
        s2_layout.setSpacing(6)

        s2_label = QLabel("Step 2: Obtain & Paste Redirect URL")
        s2_label.setFont(QFont("Segoe UI", 10, QFont.Weight.DemiBold))
        s2_layout.addWidget(s2_label)

        guide_text = (
            "1. In your browser, press <b>F12</b> to open <b>Developer Tools</b>.<br>"
            "2. Switch to the <b>Network</b> tab and check <b>Preserve log</b>.<br>"
            "3. Sign into your Sony account in the web page.<br>"
            "4. In Network filter box, type <b><code>ssh</code></b> or <b><code>signin</code></b>.<br>"
            "5. Copy the request URL (starts with <b><code>ssh-app://signin?code=...</code></b>) and paste below:"
        )
        s2_guide = QLabel(guide_text)
        s2_guide.setTextFormat(Qt.TextFormat.RichText)
        s2_guide.setStyleSheet("color: #BDBDBD; font-size: 8.5pt; line-height: 140%;")
        s2_layout.addWidget(s2_guide)

        self.input_code = QLineEdit()
        self.input_code.setPlaceholderText("ssh-app://signin?code=... or authorization code")
        self.input_code.setFixedHeight(34)
        s2_layout.addWidget(self.input_code)
        main_layout.addWidget(step2_box)

        # Preferences Card
        pref_box = QFrame()
        pref_box.setStyleSheet("background: #282828; border-radius: 8px; padding: 6px;")
        p_layout = QVBoxLayout(pref_box)
        p_layout.setSpacing(6)

        self.chk_promote = QCheckBox("Pin icon directly to Taskbar (always visible, outside arrow)")
        self.chk_promote.setChecked(True)
        self.chk_promote.setStyleSheet("color: #FFFFFF; font-size: 9pt;")
        p_layout.addWidget(self.chk_promote)

        self.chk_autostart = QCheckBox("Start Bravia Controller automatically on Windows boot")
        self.chk_autostart.setChecked(True)
        self.chk_autostart.setStyleSheet("color: #FFFFFF; font-size: 9pt;")
        p_layout.addWidget(self.chk_autostart)

        main_layout.addWidget(pref_box)

        # Status / Error Label
        self.status_label = QLabel("")
        self.status_label.setWordWrap(True)
        self.status_label.setStyleSheet("color: #FF5252; font-size: 9pt;")
        main_layout.addWidget(self.status_label)

        main_layout.addStretch()

        # Action Buttons
        btn_row = QHBoxLayout()

        self.btn_taskbar_settings = QPushButton("Taskbar Settings…")
        self.btn_taskbar_settings.setFixedSize(130, 32)
        self.btn_taskbar_settings.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_taskbar_settings.clicked.connect(open_taskbar_settings)
        btn_row.addWidget(self.btn_taskbar_settings)

        btn_row.addStretch()

        self.btn_cancel = QPushButton("Cancel")
        self.btn_cancel.setFixedSize(90, 32)
        self.btn_cancel.clicked.connect(self.reject)
        btn_row.addWidget(self.btn_cancel)

        self.btn_complete = QPushButton("Complete & Connect")
        self.btn_complete.setFixedSize(160, 32)
        self.btn_complete.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_complete.clicked.connect(self._on_complete)
        btn_row.addWidget(self.btn_complete)

        main_layout.addLayout(btn_row)

    def _apply_theme(self) -> None:
        self.setStyleSheet(
            """
            QDialog {
                background-color: #1F1F1F;
                color: #FFFFFF;
            }
            QLabel {
                color: #FFFFFF;
            }
            QLineEdit {
                background-color: #181818;
                border: 1px solid #3D3D3D;
                border-radius: 6px;
                color: #FFFFFF;
                padding: 4px 10px;
                font-size: 9pt;
            }
            QLineEdit:focus {
                border: 1px solid #0078D7;
            }
            QPushButton {
                background-color: #2D2D2D;
                color: #FFFFFF;
                border: 1px solid #3D3D3D;
                border-radius: 6px;
                font-weight: 600;
                font-size: 9pt;
            }
            QPushButton:hover {
                background-color: #383838;
            }
            QPushButton#primary {
                background-color: #0078D7;
                border: 1px solid #0086F0;
            }
            QPushButton#primary:hover {
                background-color: #1084E3;
            }
            """
        )
        self.btn_open_browser.setObjectName("primary")
        self.btn_complete.setObjectName("primary")

    def _on_open_browser(self) -> None:
        try:
            self._auth_url, self._code_verifier, self._expected_state = start_oauth_login()
            webbrowser.open(self._auth_url)
            self.status_label.setStyleSheet("color: #60CDFF; font-size: 9pt;")
            self.status_label.setText("Browser opened. Sign in and copy the 'ssh-app://...' request from Developer Tools Network tab.")
        except Exception as exc:  # noqa: BLE001
            _LOGGER.exception("Failed to start OAuth login")
            self.status_label.setStyleSheet("color: #FF5252; font-size: 9pt;")
            self.status_label.setText(f"Error launching sign-in: {exc}")

    def _on_complete(self) -> None:
        code_input = self.input_code.text().strip()
        if not code_input:
            self.status_label.setStyleSheet("color: #FF5252; font-size: 9pt;")
            self.status_label.setText("Please paste the authorization code or redirect URL.")
            return

        if not self._code_verifier:
            self.status_label.setStyleSheet("color: #FF5252; font-size: 9pt;")
            self.status_label.setText("Please click 'Open Sony Sign-In in Browser' first to start a session.")
            return

        self.status_label.setStyleSheet("color: #60CDFF; font-size: 9pt;")
        self.status_label.setText("Exchanging authentication keys with Sony...")
        self.btn_complete.setEnabled(False)

        try:
            creds = complete_oauth_flow(
                code_input,
                self._code_verifier,
                expected_state=self._expected_state or None,
            )
            write_credentials(self.output_path, creds)
            _LOGGER.info("Session keys saved successfully to %s", self.output_path)

            # Apply user preferences
            if self.chk_promote.isChecked():
                promote_tray_icon()
            if self.chk_autostart.isChecked():
                set_autostart(True)

            self.accept()
        except Exception as exc:  # noqa: BLE001
            _LOGGER.exception("Token exchange failed")
            self.btn_complete.setEnabled(True)
            self.status_label.setStyleSheet("color: #FF5252; font-size: 9pt;")
            self.status_label.setText(f"Authentication failed: {exc}")
