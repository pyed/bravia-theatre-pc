"""Windows Auto-Start & Taskbar Tray Promotion Manager.

Manages:
1. Automatic startup of BRAVIA Theatre PC on Windows logon
   (Registry: HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run)
2. Promoting / pinning the tray icon directly next to the clock
   (Registry: HKCU\\Control Panel\\NotifyIconSettings -> IsPromoted=1)
"""

from __future__ import annotations

import logging
import os
import sys
import webbrowser
import winreg
from pathlib import Path
from typing import Optional

_LOGGER = logging.getLogger("bravia.autostart")
RUN_KEY_PATH = r"Software\Microsoft\Windows\CurrentVersion\Run"
NOTIFY_KEY_PATH = r"Control Panel\NotifyIconSettings"
APP_REG_NAME = "BraviaTheatrePC"


def get_launch_command() -> str:
    """Return the exact command needed to launch the app quietly."""
    # If running as a frozen PyInstaller executable:
    if getattr(sys, "frozen", False):
        exe_path = Path(sys.executable).resolve()
        return f'"{exe_path}"'

    # If running from Python source:
    app_py = Path(__file__).resolve().parent / "app.py"
    # Prefer pythonw.exe over python.exe to run without a console window
    py_exec = Path(sys.executable).resolve()
    pyw_exec = py_exec.with_name("pythonw.exe")
    launcher = pyw_exec if pyw_exec.exists() else py_exec
    return f'"{launcher}" "{app_py}"'


def is_autostart_enabled() -> bool:
    """Check if the app is configured to start with Windows."""
    try:
        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER, RUN_KEY_PATH, 0, winreg.KEY_READ
        ) as key:
            val, _ = winreg.QueryValueEx(key, APP_REG_NAME)
            return bool(val)
    except OSError:
        return False


def set_autostart(enable: bool) -> bool:
    """Enable or disable starting with Windows."""
    try:
        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER, RUN_KEY_PATH, 0, winreg.KEY_SET_VALUE
        ) as key:
            if enable:
                cmd = get_launch_command()
                winreg.SetValueEx(key, APP_REG_NAME, 0, winreg.REG_SZ, cmd)
                _LOGGER.info("Enabled auto-start with Windows: %s", cmd)
            else:
                try:
                    winreg.DeleteValue(key, APP_REG_NAME)
                    _LOGGER.info("Disabled auto-start with Windows")
                except FileNotFoundError:
                    pass
        return True
    except OSError as exc:
        _LOGGER.warning("Could not update auto-start registry key: %s", exc)
        return False


def is_tray_promoted(target_exe: Optional[str] = None) -> bool:
    """Check if the tray icon is set to always show on the taskbar."""
    target = (target_exe or sys.executable).lower()
    target_name = os.path.basename(target)
    try:
        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER, NOTIFY_KEY_PATH, 0, winreg.KEY_READ
        ) as root_key:
            count, _, _ = winreg.QueryInfoKey(root_key)
            for i in range(count):
                sub_name = winreg.EnumKey(root_key, i)
                try:
                    with winreg.OpenKey(
                        root_key, sub_name, 0, winreg.KEY_READ
                    ) as sub_key:
                        try:
                            exe_path, _ = winreg.QueryValueEx(sub_key, "ExecutablePath")
                            if exe_path:
                                exe_lower = exe_path.lower()
                                if (
                                    target == exe_lower
                                    or target_name == os.path.basename(exe_lower)
                                    or "bravia" in exe_lower
                                ):
                                    try:
                                        prom, _ = winreg.QueryValueEx(sub_key, "IsPromoted")
                                        if int(prom) == 1:
                                            return True
                                    except OSError:
                                        pass
                        except OSError:
                            pass
                except OSError:
                    pass
    except OSError:
        pass
    return False


def set_tray_promoted(enable: bool, target_exe: Optional[str] = None) -> bool:
    """Set whether the tray icon should always be shown next to the clock."""
    target = (target_exe or sys.executable).lower()
    target_name = os.path.basename(target)
    val = 1 if enable else 0
    updated = False
    try:
        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER, NOTIFY_KEY_PATH, 0, winreg.KEY_READ | winreg.KEY_WRITE
        ) as root_key:
            count, _, _ = winreg.QueryInfoKey(root_key)
            for i in range(count):
                sub_name = winreg.EnumKey(root_key, i)
                try:
                    with winreg.OpenKey(
                        root_key, sub_name, 0, winreg.KEY_READ | winreg.KEY_WRITE
                    ) as sub_key:
                        try:
                            exe_path, _ = winreg.QueryValueEx(sub_key, "ExecutablePath")
                            if exe_path:
                                exe_lower = exe_path.lower()
                                if (
                                    target == exe_lower
                                    or target_name == os.path.basename(exe_lower)
                                    or "bravia" in exe_lower
                                ):
                                    winreg.SetValueEx(sub_key, "IsPromoted", 0, winreg.REG_DWORD, val)
                                    updated = True
                                    _LOGGER.info("Set IsPromoted=%d for %s", val, exe_path)
                        except OSError:
                            pass
                except OSError:
                    pass
    except OSError as exc:
        _LOGGER.debug("Could not update NotifyIconSettings: %s", exc)
    return updated


def promote_tray_icon(target_exe: Optional[str] = None) -> bool:
    """Convenience alias to promote the tray icon."""
    return set_tray_promoted(True, target_exe)


def open_taskbar_settings() -> None:
    """Open Windows 11 Taskbar notification area settings page."""
    webbrowser.open("ms-settings:taskbar")
