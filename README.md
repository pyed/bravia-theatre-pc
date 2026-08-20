# BRAVIA Theatre PC

A native Windows 11 system tray and Fluent Quick Settings controller for modern Sony Home Audio systems, including the BRAVIA Theatre Bar 9 (HT-A9000), BRAVIA Theatre Bar 8 (HT-A8000), BRAVIA Theatre Quad (HT-A9M2), and compatible AV receivers.

BRAVIA Theatre PC provides real-time audio format badges directly in the Windows taskbar, paired with a modern Windows 11 Quick Settings flyout panel featuring interactive volume controls, an audio codec hero card, and quick toggles for Sound Field, Night Mode, Power, and Mute.

All communication occurs locally over Sony's encrypted gRPC protocol (port 55051), providing instantaneous response times without routing commands through external cloud servers during active playback.

---

## Screenshots

<div align="center">
  <img src="docs/screenshots/flyout_atmos.png" width="31%" alt="Dolby Atmos Live Flyout" />
  <img src="docs/screenshots/flyout_dts.png" width="31%" alt="DTS Live Flyout" />
  <img src="docs/screenshots/flyout_lpcm.png" width="31%" alt="LPCM 7.1ch Live Flyout" />
</div>

---

## Features

- **Dynamic Tray Codec Badges:** Real-time taskbar icon updates reflecting the active audio bitstream:
  - **Dolby Atmos:** TrueHD, Digital Plus, and MAT containers
  - **Dolby Audio:** Dolby TrueHD, Dolby Digital Plus, and Dolby Digital (AC-3)
  - **DTS:** DTS:X, DTS-HD Master Audio, DTS-HD High Resolution, DTS 96/24, and DTS Express
  - **IMAX Enhanced:** IMAX Enhanced DTS bitstreams
  - **Linear PCM:** Multichannel and stereo uncompressed LPCM
  - **Sony 360 Reality Audio & AAC**
  - **Standby & Idle Badges**
- **Windows 11 Fluent Quick Settings Flyout:**
  - Interactive volume slider with smooth drag tracking, custom halo thumb, and real-time numeric indicator.
  - Active audio format hero card detailing the detected bitstream and audio channel layout (e.g., 7.1, 5.1.2, 2.0).
  - Quick action tiles for Sound Field (360 Spatial Sound Mapping), Night Mode, Power, and Mute.
  - Automatic positioning above the taskbar and click-away dismissal.
- **Built-in First-Time Setup Wizard:** Native graphical setup dialog that guides users step-by-step through Sony account authentication without manual command-line execution.
- **Taskbar & Startup Integration:**
  - Option to pin the tray icon directly next to the Windows clock (preventing Windows from hiding it inside the overflow arrow).
  - Option to start automatically on Windows logon via the Windows Registry.
- **Local Network Auto-Discovery:** Automatic discovery via mDNS (`_sonysmarthome._tcp.local.`) with exponential reconnection backoff and optional static IP configuration.

---

## Supported Devices

- Sony BRAVIA Theatre Bar 9 (HT-A9000)
- Sony BRAVIA Theatre Bar 8 (HT-A8000)
- Sony BRAVIA Theatre Quad (HT-A9M2)
- Compatible 2024+ Sony BRAVIA Theatre soundbars and AV receivers utilizing the Sony BRAVIA Connect protocol

---

## Installation

### Option 1: Standalone Executable (Recommended)

1. Download `BraviaTheatrePC.exe` from the latest [Releases](../../releases) page.
2. Place the executable in a directory of your choice and run it (no Python installation required).
3. On first launch, the **Sony Account Setup** wizard will open automatically.

### Option 2: Running from Source

**Prerequisites:** Python 3.10 or higher.

1. Clone the repository and install required dependencies:
   ```bat
   git clone https://github.com/USERNAME/bravia-theatre-pc.git
   cd bravia-theatre-pc
   pip install -r requirements.txt
   ```

2. Run the application:
   ```bat
   python src/app.py
   ```

   Alternatively, use the included helper scripts:
   - `start_tray.bat`: Launches the app with console output for troubleshooting.
   - `start_silent.vbs`: Launches the app silently in the background tray with no console window.

---

## First-Time Sony Account Setup

Sony BRAVIA Theatre devices use encrypted local gRPC authentication derived from Sony Cloud OAuth tokens. The application includes a graphical setup wizard to generate your local `session_keys.json` file.

### Step-by-Step Authentication Guide:

1. Launch `BraviaTheatrePC.exe` (or `python src/app.py`).
2. When prompted by the setup dialog, click **Open Sony Sign-In in Browser**.
3. In the browser window that opens, press `F12` (or right-click anywhere and select **Inspect**) to open **Developer Tools**.
4. Navigate to the **Network** tab in Developer Tools and check the **Preserve log** option.
5. Log into your Sony account (the same account linked to your soundbar in the Sony BRAVIA Connect mobile app).
6. After logging in, filter the requests in the Network tab by typing `ssh` or `signin`.
7. Locate the redirect request starting with:
   ```
   ssh-app://signin?code=...
   ```
8. Copy the entire URL (or the authorization code value) and paste it into the setup dialog.
9. Click **Complete & Connect**. The application will exchange tokens, save your credentials locally to `session_keys.json`, and immediately connect to your soundbar.

> **Security Note:** `session_keys.json` contains local session tokens for your device and is stored only on your computer. It is excluded from version control via `.gitignore` and should never be shared publicly.

---

## Controls & Usage

- **Left-Click Tray Icon:** Opens the Windows 11 Fluent Quick Settings flyout panel.
- **Right-Click Tray Icon:** Opens the context menu with the following options:
  - **Start with Windows:** Toggle automatic launch on Windows logon.
  - **Always show on taskbar:** Keep the icon visible next to the clock instead of hidden in the overflow tray.
  - **Sony Account Setup:** Re-authenticate or switch Sony accounts.
  - **Exit:** Completely close the application.

---

## Configuration

An optional `config.json` file can be placed next to the executable to customize connection and discovery parameters:

```json
{
  "host": "",
  "port": 55051,
  "discovery_timeout": 6,
  "reconnect_min_seconds": 5,
  "reconnect_max_seconds": 60,
  "menu_refresh_ms": 500
}
```

- `host`: Specify a static IP address for the soundbar (leave empty for automatic mDNS discovery).
- `port`: The gRPC control port (default: `55051`).
- `discovery_timeout`: Maximum duration in seconds to scan for mDNS advertisements.
- `reconnect_min_seconds` / `reconnect_max_seconds`: Backoff window for reconnection attempts when the soundbar powers off or disconnects.

---

## Building the Executable

To package the standalone single-file Windows executable locally:

```bat
pip install pyinstaller
python build_exe.py
```

The compiled binary will be generated at `dist/BraviaTheatrePC.exe`.

---

## Acknowledgments

Special thanks to **Ryan Ludwig** ([@steamEngineer](https://github.com/steamEngineer)) for his reverse engineering work and development of the [`pybravia-connect`](https://github.com/steamEngineer/pybravia-connect) library, which made communication with Sony's modern encrypted audio protocol possible.

---

## Disclaimer

This is an independent, open-source project and is not affiliated with, sponsored by, or endorsed by Sony Corporation. Sony, BRAVIA, BRAVIA Theatre, 360 Spatial Sound Mapping, Dolby Atmos, and DTS are trademarks of their respective owners.
