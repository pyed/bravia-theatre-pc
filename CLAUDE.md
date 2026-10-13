# CLAUDE.md

Guidance for Claude Code and other coding agents working in this repository.

**Start every session by reading [HANDOFF.md](HANDOFF.md).** It records the current state of
work: what was diagnosed and how, what changed, what is deployed, what is unfinished, and the open
findings that have not been fixed yet.

## Keep HANDOFF.md current

Update HANDOFF.md as you progress, not only at the end of a session:

- When you diagnose something, record the evidence and the conclusion, including dead ends that
  someone else would otherwise repeat.
- When you change, build, deploy, or verify something, record what, where, and how it was checked.
- When you stop with work unfinished, record exactly where it stands and the next concrete step.
- When an item is resolved, move it to the done section with how it was verified. Do not delete it.
- Remove statements that are no longer true. A stale handoff is worse than none.

Keep personal details out of it (user names in paths, LAN addresses, device identifiers): the
repository is public. Never put credentials, tokens, session ids, or callback URLs in it.

## Project

BRAVIA Theatre PC is a Windows tray app (WPF, .NET 10) that controls Sony BRAVIA Theatre soundbars.
A Sony account sign-in (OAuth with PKCE in an embedded WebView2) mints device-local session keys;
after that the app talks to the soundbar directly over cleartext HTTP/2 gRPC on the LAN
(port 55051), signing requests with HMAC-SHA256.

```text
src/BraviaTheatre.Core/      no UI dependencies
  Auth/       SonyOAuth (cloud calls), SonyCredentialLifecycle (serialized renewal), SonyCredentials
  Discovery/  MdnsDiscovery: mDNS, then a bounded subnet probe that fingerprints the gRPC service
  Engine/     BraviaClient (gRPC + session handshake), BraviaEngine (connection loop, state, commands),
              NotifyStreamGate (notify stream across session renewals), ControlEndpoint (static host)
  Wire/       bounded protobuf codecs for Sony's hand-framed messages, HMAC signing
  Models/     SoundbarState, CodecTaxonomy, control ranges
src/BraviaTheatre.UI/        WPF app; assembly and exe name BraviaTheatrePC
  App.xaml.cs  startup, single instance, logging, unhandled-error handling, engine lifetime
  Services/    tray icon, hotkeys, DPAPI credential store, backdrop, native library pinning, ...
  Views/       FlyoutWindow, TrayMenuWindow, SettingsWindow, AuthDialog, DeviceSelectionDialog
tests/BraviaTheatre.Tests/   xUnit v3 (VSTest mode); references both projects
```

## Commands

Run from the repository root. `global.json` pins SDK 10.0.302 with `rollForward: latestPatch`,
so only a 10.0.3xx SDK satisfies it.

```powershell
dotnet restore BraviaTheatrePC.sln
dotnet format BraviaTheatrePC.sln --verify-no-changes --no-restore
dotnet build BraviaTheatrePC.sln -c Release --no-restore -warnaserror
dotnet test BraviaTheatrePC.sln -c Release --no-build --no-restore
dotnet test BraviaTheatrePC.sln -c Release --no-build --filter "FullyQualifiedName~EngineRegressionTests"
```

The release builds both of these; CI runs them as smoke tests:

```powershell
dotnet publish src/BraviaTheatre.UI/BraviaTheatre.UI.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=none -o publish/standalone
dotnet publish src/BraviaTheatre.UI/BraviaTheatre.UI.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=none -o publish/framework-dependent
```

CI (`.github/workflows/ci.yml`) also runs gitleaks and a NuGet vulnerability audit that parses
`dotnet package list --vulnerable --format json` and fails on any finding.
A `v*` tag runs `release.yml`, which derives the version from the tag. Bump `<Version>` in
`Directory.Build.props` when preparing a release.

## Conventions

- **CRLF line endings everywhere** (`.editorconfig`, `.gitattributes`). Tools that write LF files
  make `dotnet format --verify-no-changes` fail with `ENDOFLINE`; convert new files before checking.
- Warnings are errors in CI. Match the surrounding code: comments explain why, not what.
- Tests use synthetic credentials and packet fixtures only. Real keys, session ids, tokens, device
  ids, and captures must never enter the repository (see `CONTRIBUTING.md`, `SECURITY.md`).
- WPF allows one `Application` per process. UI tests that need the App resources (FlyoutWindow
  uses App-level styles) extend the existing STA test in `ReleasePolishTests` rather than creating
  another `App`.
- WPF projects drop `System.IO` from implicit usings; add it explicitly where needed.
- Do not reference `const` fields through `x:Static` in XAML: it compiles but fails at runtime in
  the single-file publish. Assign such values in code (see the `FlyoutWindow` constructor).
- Prove a regression test catches its bug: revert the fix, watch the test fail, restore it.

## Logging and diagnostics

- Daily logs: `%LOCALAPPDATA%\BraviaTheatrePC\Logs\BraviaTheatrePC-YYYY-MM-DD.log` (14 days kept).
  Settings: `%LOCALAPPDATA%\BraviaTheatrePC\settings.json`; `LogLevel` is Critical, Info, or Verbose,
  and the default is Critical.
- `App.Log(string)` chooses the level from the message text: `error`, `exception`, `fatal`,
  `failed`, or `[Credential renewal]` make it Critical; phrases such as `Starting`, `Connecting`,
  `Discovered`, or `handshake completed` make it Info; anything else is Verbose. Word new messages
  with that in mind, or call the overload that takes an explicit level.
- Unhandled exceptions are logged with their stack frames, because single-file builds lack the
  runtime's message resources (`mscorrc.dll`): runtime errors arrive with generic text such as
  "Dll was not found." that names nothing.
- Never log credentials, tokens, session ids, HMAC keys, or OAuth callback URLs. The engine logs
  failures as exception type and gRPC status only.

## Runtime facts that have caused bugs

- **Single-file native extraction.** The release exe extracts its native DLLs to
  `%TEMP%\.net\BraviaTheatrePC\<bundle id>\`. Windows temp cleanup deletes files there that are a
  week old and not in use, while the tray app keeps running for days. `BundledNativeLibraries`
  loads every such DLL at startup so it cannot be deleted. When adding a native dependency, add it
  to `BundledNativeLibraries.Libraries`; `NativeLibraryPinningTests` enforces this for WPF's own.
- **Error dialogs** go through `ErrorDialogGate`: one at a time, and the same failure at most once
  every ten minutes. A failure inside a window-message handler repeats for every message, and a
  modal dialog pumps messages, so ungated dialogs stack.
- **gRPC channels** must keep `DisposeHttpClient = true` in `BraviaClient.CreateChannelOptions`;
  a channel does not dispose a handler it was given, and every reconnect creates a new client.
- **Device protocol quirks.** The soundbar answers `InvalidArgument` both for a path it does not
  support and for a rejected session, so one rejected path means nothing while every path rejected
  means the session is gone. It ends a local session about an hour after the handshake, so the
  engine repeats the handshake on the open connection every `DeviceSessionRenewalInterval`
  (50 minutes), and once more whenever a keepalive comes back empty. `NotifyStreamGate` closes the
  notification stream for the renewal and reopens it on the new session. Any failure there falls
  back to a full reconnect. Requests carry a rolling 8-byte session random and possibly a new
  session id; `BraviaClient` adopts both.
  Two clients using the same key id at once interfere with each other, so do not run experiments
  against a soundbar while the app is connected to it.
- **Engine state.** Each connection gets a generation number. State changes and queued commands
  from an older generation are dropped, and published snapshots carry a sequence so subscribers
  never see an older state after a newer one. Keep both properties when changing the engine;
  `EngineRegressionTests` and `AuthRequiredRegressionTests` cover them.

## Verifying single-file behavior

Unit tests run framework-dependent, so they cannot show extraction or temp-cleanup effects. To test
those, publish a small harness that compiles the relevant source file with
`<Compile Include="...\File.cs" Link="File.cs" />`, set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a
scratch folder, delete the extracted files that are not in use, and exercise the code path.
Do not launch a second copy of the app for this while one is running: the single-instance check
broadcasts a show-flyout message to the running copy and exits.
