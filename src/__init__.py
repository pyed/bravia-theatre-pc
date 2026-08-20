"""Sony BRAVIA Theatre PC — Windows System Tray & Quick Controls.

Modules
-------
discovery     -- mDNS/zeroconf auto-discovery with config.json fallback
icon_manager  -- procedural Pillow tray-icon generation + asset overrides
grpc_engine   -- single background worker owning the pybravia-connect client
flyout_ui     -- Windows 11 Fluent Design flyout UI & slider
autostart     -- Windows auto-start & taskbar promotion manager
auth_dialog   -- native first-time setup & Sony OAuth sign-in wizard
app           -- PySide6 system-tray UI and event coordinator (entry point)
"""

__version__ = "1.0.0"

APP_NAME = "BRAVIA Theatre PC"
