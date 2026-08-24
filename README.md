# BRAVIA Theatre PC

BRAVIA Theatre PC is a native Windows tray controller and compact WPF quick-controls flyout for compatible Sony BRAVIA Theatre audio systems. It shows the current audio format in the taskbar and provides volume, mute, input, bass, rear-level, sound-field, night-mode, voice-mode, and power controls.

Built with C# and .NET 10, the application communicates directly with the device on the local network after an initial Sony account sign-in is used to obtain local-control credentials.

## Screenshots

<div align="center">
  <img src="assets/screenshots/flyout_1.png" width="31%" alt="BRAVIA Theatre PC flyout" />
  <img src="assets/screenshots/flyout_2.png" width="31%" alt="BRAVIA Theatre PC flyout with rear-level control" />
  <img src="assets/screenshots/flyout_3.png" width="31%" alt="BRAVIA Theatre PC compact flyout" />
</div>

## Features

- Live codec and channel display, with distinct badges for Dolby Atmos, Dolby Audio, Dolby TrueHD, DTS/DTS:X, IMAX Enhanced, LPCM/PCM, AAC, DSD, and Sony 360 Reality Audio.
- A compact Windows quick-controls flyout for volume, mute, input, bass, optional rear-speaker level, sound field, night mode, voice mode, and power.
- Configurable global shortcuts, registered atomically so a conflicting shortcut cannot leave a partial hotkey setup.
- Embedded Sony OAuth sign-in with PKCE and callback-state verification, a manual browser fallback, and explicit device selection when an account contains multiple compatible devices.
- Windows user-scoped credential protection using DPAPI. Sony cloud access and refresh tokens are not persisted.
- Automatic local discovery using mDNS first, followed by a bounded subnet probe that fingerprints candidates before connecting.
- Connection-scoped workers, health polling, command coalescing, stale-command rejection, and clean reconnect/teardown behavior.
- Native Win32 tray integration with Explorer restart recovery, a supported Taskbar Settings shortcut and one-time visibility guidance, multi-monitor flyout placement, and single-instance activation.
- Atomic settings storage, Windows startup integration, configurable logging, and optional static host/port override.

Default shortcuts are:

| Action | Shortcut |
| --- | --- |
| Volume up | `Ctrl + Alt + Up` |
| Volume down | `Ctrl + Alt + Down` |
| Toggle mute | `Ctrl + Shift + M` |
| Toggle sound field | `Ctrl + Alt + S` |
| Toggle voice mode | `Ctrl + Alt + V` |
| Toggle night mode | `Ctrl + Alt + N` |

All shortcuts can be changed or disabled in Settings.

## Supported devices

The protocol is intended for recent Sony systems managed by the Sony BRAVIA Connect app, including:

- BRAVIA Theatre Bar 9 (HT-A9000)
- BRAVIA Theatre Bar 8 (HT-A8000)
- BRAVIA Theatre Quad (HT-A9M2)
- Other compatible Sony home-audio systems exposing the same local control service

Compatibility can vary by model and firmware. Reports with model and firmware details are welcome, but never attach credential files or unredacted diagnostic captures.

## Install and run

### Release executable

Download the appropriate executable from [GitHub Releases](../../releases):

- `BraviaTheatrePC.exe` is self-contained and does not require a separately installed .NET runtime.
- `BraviaTheatrePC-FrameworkDependent.exe` requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

Run the executable and complete Sony sign-in when prompted.

Keep the executable at a stable path so Windows can retain your tray-icon placement choice across updates. The first launch offers to open **Taskbar settings**, where you can enable BRAVIA Theatre PC under **Other system tray icons** or drag it from the hidden-icons menu next to the clock.

### Run from source

Requirements:

- Windows 10 or Windows 11, 64-bit
- [.NET SDK 10.0.302](https://dotnet.microsoft.com/download/dotnet/10.0) or a compatible patch selected by `global.json`
- Microsoft Edge WebView2 Runtime for the embedded sign-in experience

```powershell
git clone https://github.com/pyed/bravia-theatre-pc.git
cd bravia-theatre-pc
dotnet run --project src/BraviaTheatre.UI/BraviaTheatre.UI.csproj
```

To create a self-contained single-file build:

```powershell
dotnet publish src/BraviaTheatre.UI/BraviaTheatre.UI.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

## Sony account setup and credential storage

On first launch, sign in with the Sony account linked to the device in the BRAVIA Connect app. The embedded flow normally captures the `ssh-app://signin` callback automatically. Manual mode opens the same Sony authorization page in the default browser and accepts the complete callback URL.

The application validates the OAuth state before exchanging the authorization code. It then requests device-local session credentials and stores only those local-control values in:

```text
%LOCALAPPDATA%\BraviaTheatrePC\credentials.dat
```

The file is encrypted for the current Windows user with DPAPI. If protected credentials are not present, the application starts Sony account setup and creates them after successful sign-in. Do not copy credential files, callback URLs, browser network captures, or verbose logs into issues, tests, or commits.

Use **Sony Account Setup** from the tray menu or **Re-authenticate** in Settings to select another account/device or replace local credentials. The existing engine is stopped before the replacement connection starts.

## Configuration and diagnostics

Settings are stored atomically under `%LOCALAPPDATA%\BraviaTheatrePC\settings.json`. Use the Settings window to configure:

- Windows startup
- A shortcut to Windows Taskbar settings for keeping the tray icon visible
- Optional rear-speaker controls
- Global shortcuts
- Automatic discovery or a static host/port override
- Critical, Info, or Verbose logging

Only this current settings location is read. Logs are written under `%LOCALAPPDATA%\BraviaTheatrePC` and rotate at approximately 2 MB.

## Local-network security model

Device control uses cleartext HTTP/2 gRPC on the local network (normally TCP port `55051`). Sony session credentials provide protocol authentication, but the transport is not TLS-encrypted. Run the application only on a trusted LAN, keep the soundbar and PC firmware current, and avoid exposing the control port across the internet or an untrusted network.

See [SECURITY.md](SECURITY.md) for private vulnerability reporting, credential-containment guidance, and supported versions.

## Development

Restore, verify formatting, build, and test from the repository root:

```powershell
dotnet restore BraviaTheatrePC.sln
dotnet format BraviaTheatrePC.sln --verify-no-changes --no-restore
dotnet build BraviaTheatrePC.sln -c Release --no-restore -warnaserror
dotnet test BraviaTheatrePC.sln -c Release --no-build --no-restore
```

The tests cover bounded wire decoding, schema-aware state parsing, client session sequencing, discovery parsing and cancellation, OAuth validation, credential serialization, exhaustive codec taxonomy and packaged badge resources, taskbar-settings guidance, connection teardown, reconnect isolation, stale commands, and snapshot/delta ordering.

See [CONTRIBUTING.md](CONTRIBUTING.md) before submitting changes. Test vectors must be synthetic; real session IDs, HMAC keys, OAuth tokens, device identifiers, and packet captures are prohibited.

## Project structure

```text
bravia-theatre-pc/
├── src/
│   ├── BraviaTheatre.Core/
│   │   ├── Auth/       Sony OAuth and credential models
│   │   ├── Discovery/  mDNS parsing and bounded LAN discovery
│   │   ├── Engine/     gRPC client, state engine, and command queue
│   │   ├── Models/     state and codec taxonomy
│   │   ├── Protos/     Sony control-service definitions
│   │   └── Wire/       bounded protobuf codecs and HMAC helpers
│   └── BraviaTheatre.UI/
│       ├── Models/     application settings
│       ├── Services/   credential store, tray, hotkeys, and startup
│       └── Views/      flyout, settings, device selection, and sign-in
└── tests/
    └── BraviaTheatre.Tests/
```

## Acknowledgments

Special thanks to Ryan Ludwig ([@steamEngineer](https://github.com/steamEngineer)) for the reverse-engineering work in [`pybravia-connect`](https://github.com/steamEngineer/pybravia-connect), which provided the reference foundation for this implementation.

## Disclaimer

This independent open-source project is not affiliated with, sponsored by, or endorsed by Sony Corporation. Sony, BRAVIA, BRAVIA Theatre, 360 Spatial Sound Mapping, Dolby, Dolby Atmos, Dolby Audio, DTS, and DTS:X are trademarks of their respective owners.
