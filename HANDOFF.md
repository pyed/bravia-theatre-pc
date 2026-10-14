# HANDOFF

Living record of work on this repository. Read it first; update it as you go (see
[CLAUDE.md](CLAUDE.md#keep-handoffmd-current)). Times are the reporter's local time.

Last updated: 2026-10-14

## Status

| Item | State |
| --- | --- |
| "Dll was not found." error dialogs after a Remote Desktop session | Fixed in v2.2.8 |
| Error-dialog storm, missing stack traces in the log | Fixed in v2.2.8 |
| gRPC connection pool leaked on every reconnect | Fixed in v2.2.8 |
| Volume slider stops following the soundbar after an interrupted drag | Fixed in v2.2.8 |
| Soundbar connection drops every hour | Fixed in v2.2.8; verified on the reporter's soundbar (section 3) |
| Build on the reporter's PC | Framework-dependent build with the section 8 fix (reports 2.3.0) since 2026-10-14 16:14 |
| Framework-dependent download never starts | Fixed in v2.3.1 (section 8) |
| v2.2.8 release | Published 2026-10-13 22:56 UTC with all five assets; CI on `main` green |
| Full audit with prioritized fix proposal | Done 2026-10-14 (section 4) |
| Overnight work, 2026-10-14 | Released in v2.3.0; open checks in section 7 |
| v2.3.0 release | Published 2026-10-14 11:31 (08:31 UTC) with all five assets; Release and CI on `main` green |
| v2.3.1 release | Tagged 2026-10-14; result recorded below once the Release run finishes |

Released as **v2.3.1** on 2026-10-14: the framework-dependent download starts again (it never had
since ReadyToRun was enabled), and CI and the release workflow now launch both executables
(section 8).

Released as **v2.3.0** on 2026-10-14: one commit on `main` with batches 1 to 3, P4-4 and P4-5 from
section 4 (details in section 7), tagged `v2.3.0`; the tag push runs the Release workflow. Minor
version bump: it adds features (tray notifications, credential renewal on the open connection,
per-monitor DPI, software rendering, temp-folder cleanup), not only fixes.

Released before that as **v2.2.8**: one commit on `main`, tagged `v2.2.8`, whose push runs the
Release workflow. It sits on `432853d`, the merge of PR #12 (mDNS answer validation, static-host validation through
`ControlEndpoint`, a NuGet audit that fails on findings). That PR had been merged on GitHub but not
pulled locally, so it was integrated before committing. The one conflict, in `BraviaClient`, kept
both sides: PR #12's `ControlEndpoint.CreateAddress` for the address, and `CreateChannelOptions` for
the options.

## 1. "Dll was not found." dialogs after Remote Desktop (fixed)

### Symptom

After the reporter controlled the PC from an iPad over Remote Desktop while the monitor was off,
a stack of identical modal dialogs appeared: "An application error occurred. Please try the action
again." The daily log showed `DISPATCHER UNHANDLED EXCEPTION: DllNotFoundException: Dll was not
found.` 25 times between 19:55 and 19:57 and 9 more times at 23:22 on 2026-10-13, and on no
other day.

### Evidence

- The running copy (v2.2.7 standalone, started 2026-10-08 05:25) had its native DLLs extracted to
  `%TEMP%\.net\BraviaTheatrePC\ONm7X1fSJznm\`. Only the three DLLs it had loaded were still there
  (`wpfgfx_cor3`, `PresentationNative_cor3`, `D3DCompiler_47_cor3`). The bundle manifest, parsed
  from the exe, lists seven native files; `PenImc_cor3.dll`, `vcruntime140_cor3.dll`, and both
  copies of `WebView2Loader.dll` were gone.
- The surviving DLLs were written 2026-10-02 02:21:58, when an earlier start re-extracted them, and
  the folder was last modified 2026-10-09 03:15:19: the first maintenance window after the files
  turned seven days old. Windows temp cleanup (Storage Sense or Disk Cleanup automatic maintenance)
  deletes `%TEMP%` files older than a week that are not in use. Free disk space was 74%, so it was
  not a low-space cleanup. The extraction folders of older builds had been emptied the same way.
- The Remote Desktop event logs line up exactly. The PC resumed from sleep at 19:55:49, an RDP
  connection from the iPad was accepted at 19:55:53, and the session logon completed at 19:55:55.
  The first exception was logged at 19:55:55.331. The disconnect at 19:57:05 produced the next burst
  at 19:57:05.125. The 23:22 burst came as the reporter returned to the console.
- WPF enables its pen and touch stack the first time a HID digitizer appears in raw input
  (`WispTabletDeviceCollection.ShouldEnableTablets`), on `WM_DEVICECHANGE` with
  `DBT_DEVNODES_CHANGED`, forwarded from WPF's hidden resource-notification window to
  `WispLogic.OnDeviceChange`. That builds a `PenThreadWorker`, whose constructor calls
  `CreateResetEvent` in `PenImc_cor3.dll` on the UI thread. A Remote Desktop client with touch
  support adds exactly such a digitizer. With the DLL deleted, every later device-change broadcast
  threw again.
- The message names no DLL because single-file builds do not ship `mscorrc.dll`, the runtime's
  message resources, so CoreCLR falls back to the default text. Reproduced: a P/Invoke into a
  missing DLL on a runtime copy without `mscorrc.dll` gives exactly
  `DllNotFoundException: 'Dll was not found.' HResult=0x80131524`; with `mscorrc.dll` present it
  gives `Unable to load DLL 'PenImc_cor3.dll' or one of its dependencies`.
- The dialogs stacked because the handler showed a modal `MessageBox` for every exception, and the
  dialog's message loop delivered the next device-change message while it waited.
- Not the cause: screen resolution, and the monitor being off. Remote Desktop's touch device was
  the trigger; the deleted file was the cause.

### Latent failures with the same cause, found while testing

- Sony sign-in: `CoreWebView2Environment` needs `WebView2Loader.dll`. `AuthDialog` catches the
  failure and silently falls back to manual sign-in.
- Exit: WPF's C++/CLI text layer (`DirectWriteForwarder`) calls `vcruntime140_cor3.dll` from its
  runtime teardown, so quitting the app after the cleanup crashes with an unhandled
  `DllNotFoundException` (exit code `0xE0434352`).

### Fix

- `src/BraviaTheatre.UI/Services/BundledNativeLibraries.cs` (new): at startup, `App.OnStartup` loads
  `PenImc_cor3`, `PresentationNative_cor3`, `wpfgfx_cor3` (which brings `D3DCompiler_47_cor3`),
  `vcruntime140_cor3`, and `WebView2Loader` through the assembly that imports each, using the
  runtime's DllImport probing. A loaded DLL cannot be deleted, and the host restores missing files
  on the next start. If `PenImc_cor3.dll` still cannot be loaded, the app sets
  `Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport` before any window exists, so
  touch degrades to mouse input instead of throwing.
- Rejected alternative: setting `IncludeNativeLibrariesForSelfExtract=false` and shipping the DLLs
  beside the exe. It avoids `%TEMP%` entirely but changes both release assets (the
  framework-dependent exe would need `WebView2Loader.dll` beside it, so it would become a zip too).
  That is a release decision for the maintainer.
- Rejected alternative: disabling WPF's stylus support outright. Touch would still work as mouse
  input, but touch scrolling in the flyout's `ScrollViewer` would stop working.

### Verification

- `NativeLibraryPinningTests`: every native DLL that any WPF assembly imports and ships is pinned;
  each pin resolves through an assembly that really imports it; all pins load; failures are
  reported without stopping the rest. Removing `vcruntime140_cor3` from the list makes the coverage
  test fail.
- Single-file harness, published like the release, compiling the real `BundledNativeLibraries.cs`.
  It deletes every extracted file that is not in use, then runs WPF's pen-thread start
  (`PenThreadPool.GetPenThreadForPenContext`) and `CoreWebView2Environment.GetAvailableBrowserVersionString`.
  Without pinning, both fail with `DllNotFoundException: Dll was not found.` and the process crashes
  at exit. With pinning, both succeed and the process exits with code 0. The only deletable file
  left is the unused duplicate `runtimes\win-x64\native\WebView2Loader.dll`.
- Not verified: the full app during a real Remote Desktop touch session.

## 2. Other fixes in the same change

- **Error-dialog storm.** `ErrorDialogGate` (new) allows one error dialog at a time and shows the
  same failure (exception type plus throwing method) at most once every ten minutes. Every
  occurrence is still logged. Tests: `ErrorReportingTests`.
- **Stack traces in the log.** `App.FormatExceptionForLog` appends up to 16 frames of the innermost
  exception on the same line, and the fatal handler now logs the full exception instead of only its
  type. With this in place, the log would have pointed straight at `PenThreadWorker`.
- **gRPC connection pool leak.** `GrpcChannel` does not dispose a handler it was given unless
  `DisposeHttpClient` is true (grpc-dotnet 2.84.0 `GrpcChannel` constructor). `BraviaClient` gave it
  a `SocketsHttpHandler` with infinite idle timeout and 15 s keepalive pings, so each reconnect
  orphaned a pool. In practice the soundbar closed the old TCP connections; only one connection was
  open after 5.8 days. Fix: `BraviaClient.CreateChannelOptions` sets `DisposeHttpClient = true`.
  Test: `ClientProtocolTests.DisposingTheChannelDisposesItsConnectionPool`.
- **Volume slider.** `_isDraggingSlider` was cleared only by `PreviewMouseUp`. A drag that lost mouse
  capture (flyout hidden mid-drag, Alt+Tab) left it set, and the slider stopped following the
  soundbar. It is now also cleared on `LostMouseCapture`. Test: added to
  `ReleasePolishTests.FlyoutCompiledXamlLoadsWithReleaseControls`.

Every new test was confirmed to fail with its fix reverted. On the final v2.2.8 tree, with the
session renewal and PR #12 included: `dotnet format` clean, Release build with `-warnaserror` has no
warnings, 420 of 420 tests pass, and the NuGet audit (as the CI step parses it) finds nothing. The
Release workflow's steps also ran locally with `Version=2.2.8`: restore for `win-x64`, tests with
`-warnaserror`, and both publish variants.

## 3. Hourly disconnect (fixed, verified on the device)

### Evidence

Every day in the logs, about 60 minutes after each (re)connect:

```text
[Engine] Keepalive poll failed: InvalidOperationException
[Engine] Notify stream ended: RpcException (StatusCode=Cancelled)
[Engine] Connection worker cleanup warning: InvalidOperationException
```

For example, a connection made at 16:30:17 failed at 17:30:19, the next at 18:30:44, and so on,
24 times a day. The keepalive just before each failure, 25 s earlier, succeeded.

### Mechanism

The soundbar ends its local session about an hour after the handshake. Every `GetStatesWithAuth`
after that is rejected with a status that `BraviaClient.GetInitialStatesAsync` treats as "path
unsupported" (`InvalidArgument`, `NotFound`, `OutOfRange`). The keepalive snapshot therefore comes
back empty, `KeepAlivePollLoopAsync` throws `InvalidOperationException("Keepalive returned no
monitored state.")`, and the engine tears the connection down. The notify stream is not closed by
the device: it only ever ends with `Cancelled`, our own cancellation, right after the keepalive
failure. Each reconnect publishes the Disconnected state (the tray shows "Disconnected —
retrying..."), waits the 5 s backoff, re-runs discovery, and handshakes again. Commands sent during
the gap are dropped.

The reference implementation, steamEngineer/pybravia-connect, does not handle session expiry at
all; there is no documented session lifetime to rely on.

### Verified on the device (2026-10-14, HT-A9000)

- Connection established 00:38:09 (handshake); proactive renewal on the open connection at
  01:28:21 ("Device session renewed; handshake completed on the open connection.", then "Live notify
  stream started on the renewed session."), no errors.
- Sampled every 30 s through the window where the old code always dropped the connection (about
  01:38:09 to 01:38:34) and on to 01:43: the same TCP connection stayed established throughout, no
  keepalive failure, no reconnect, and no reactive renewal was needed. So the soundbar accepts a
  second handshake on the open connection, and the renewed session outlives the original's hour.
- Not observed: a pushed notification on the reopened stream. Nothing on the soundbar changed during
  the window, and state updates are only logged at Verbose. The stream is opened exactly as on a new
  connection, after the handshake.

### Fix

- `BraviaEngine.KeepAlivePollLoopAsync` renews the session on the open connection once
  `DeviceSessionRenewalInterval` (50 minutes) has passed since the handshake: it runs
  `InitializeSessionAsync` again (ConfirmSignin, ConfirmKeys, GetSessionRandom), the same handshake
  a new connection performs.
- Reactive fallback: if a keepalive snapshot comes back empty, it renews once and polls again.
- `NotifyStreamGate` (new, `src/BraviaTheatre.Core/Engine`) closes the notification stream before
  the handshake and holds its replacement back until the handshake finishes, so the new stream binds
  to the renewed session. `NotifyLoopAsync` reopens a stream that a renewal closed instead of ending
  the connection.
- If the renewal handshake fails, or the poll after it is still empty, the keepalive throws as
  before and the engine reconnects, so the worst case is the old behavior.
- The engine takes an optional `TimeProvider` (internal constructors) so tests control the clock.
- Logging: "Device session renewed; handshake completed on the open connection." and "Live notify
  stream started on the renewed session." are Info level; the triggers are Verbose. A failed
  renewal surfaces as the existing Critical "Keepalive poll failed" line.
- Tests: `DeviceSessionRenewalTests` (reactive, proactive, not-yet-due, failed renewal, and three
  `NotifyStreamGate` ordering and race cases); `EmptyKeepaliveSignalsConnectionFailureAndReconnects`
  now also asserts one renewal attempt before reconnecting. Disabling reactive renewal, proactive
  renewal, or the stream reopen each fails its tests. The four engine suites passed 25 times in a
  row before PR #12 was merged and 15 times after it.
- Risk that was accepted and then ruled out on the HT-A9000: had the soundbar rejected a second
  handshake on the same connection, the proactive renewal would have reconnected at 50 minutes
  instead of 60. Other models or firmware could still behave that way; a Critical "Keepalive poll
  failed" line about 50 minutes after each connect would be the sign.

## 4. Open findings and proposed fixes (full audit, 2026-10-14, on v2.2.8)

Scope: all source including PR #12, XAML, tests, a build with every .NET analyzer enabled
(`-p:AnalysisMode=All`), line coverage, dependencies, CI and release workflows, GitHub repository
settings, and assets. Effort: S is under an hour, M is about half a day, L is days. IDs are stable;
when an item is done, move it to a done section rather than renumbering.

Measurements behind the items:
- Coverage is 46% of lines overall. Core logic is well covered (engine 80%, credential lifecycle
  93%, OAuth 79%). `App.xaml.cs` is at 4% (489 lines); `AuthDialog`, `SettingsWindow`, and
  `TrayMenuWindow` are at 0%; the real `BraviaClient` is at 13%; `NotifyParser` is at 46% and
  `ProtobufWireCodec` at 53%, both parsing untrusted network input.
- The analyzer sweep gave 485 warnings. Triaged, none is a real defect in production code: every
  CA2025 and CA2000 hit is disposed after its work is awaited or cancelled. Mostly test-code noise.
- Dependencies are current apart from PR #14 and `Microsoft.NET.Test.Sdk` 18.11.0. Nothing is
  deprecated or vulnerable. Tests are hermetic: no network, no sleeps.

### P1: defects with user impact

| ID | Finding | Evidence | Proposed fix | Effort |
| --- | --- | --- | --- | --- |
| P1-1 | A static host with zero-padded octets connects to the wrong address: `192.168.1.010` becomes 192.168.1.8. | `ControlEndpoint.IsDottedQuad` accepts leading zeros; `IPAddress.Parse` reads them as octal (reproduced). | Reject octets with a leading zero, or build the address from the decimal bytes; add test cases. | S |
| P1-2 | The app is only system-DPI aware, so its windows are bitmap-scaled (blurry) on a monitor whose scale differs from the primary's, and after a Remote Desktop session changes the DPI. | `GetProcessDpiAwareness` returns 1 for the running app; the exe's manifest declares no DPI awareness. The code already expects per-monitor DPI (`GetDpiForWindow`, `WM_DPICHANGED` handling). | Add an `app.manifest` declaring `PerMonitorV2` and Windows 10/11 `supportedOS`; check flyout placement on mixed-DPI monitors and over RDP. | S-M |
| P1-3 | Sign-in can hang: a popup from Sony's login page (for example social sign-in) opens in a separate WebView2 window where the `ssh-app://` callback is not intercepted, and a WebView2 crash leaves a blank dialog. | `AuthDialog` handles only `NavigationStarting`; no `NewWindowRequested` or `ProcessFailed` handler. | Handle `NewWindowRequested` by navigating the same view; on `ProcessFailed`, switch to manual mode. | S |
| P1-4 | PR #14 bumps WebView2 1.0.4191.47 to 1.0.4258.31, a library the app pins at startup, but its green CI predates v2.2.8 and never ran the pinning tests. | PR #14 checks are from 2026-10-09. | `@dependabot rebase`, let CI run `NativeLibraryPinningTests`, then merge. | S |
| P1-5 | Renewing the 24-hour local credentials still drops the connection once a day (Disconnected state, discovery, new handshake). | `KeepAlivePollLoopAsync` returns to force a reconnect when the keys near expiry; observed 2026-10-14 00:38. | Renew the credentials while connected, then repeat the handshake on the open connection with the new keys (`BraviaClient` accepting new credentials, reusing the session-renewal path). Verify on the device. | M |

### P2: reliability and user experience

| ID | Finding | Evidence | Proposed fix | Effort |
| --- | --- | --- | --- | --- |
| P2-1 | Every deliberate reconnect logs `[Critical] Notify stream ended: RpcException (StatusCode=Cancelled)`. | gRPC reports the engine's own cancellation as `RpcException`, which the `OperationCanceledException` filter in `NotifyLoopAsync` misses. | Also accept `RpcException { StatusCode: Cancelled }` while the connection token is cancelled. | S |
| P2-2 | When sign-in or a soundbar choice is needed, nothing tells the user; only the flyout card and tooltip change, so an app running for days can stop working silently. | `SoundbarUiPresentationFactory`; no notification path. | Show a tray balloon once per transition (`NIF_INFO`; `NOTIFYICONDATA` in `NativeTrayIcon` already has the `szInfo` fields). | S-M |
| P2-3 | Credential and settings files are renamed into place without being flushed; a power cut can leave an empty `credentials.dat` and force a new sign-in. | `SonyCredentialStore.TrySave`, `AppSettings.TrySave` | Write through a `FileStream` with `Flush(flushToDisk: true)` before `File.Move`. | S |
| P2-4 | `AppSettings.StartWithWindows` is written but never read; the Run key is the real state and they drift (on the reporter's PC the file says true, the registry has no entry). Autostart enabled while running from inside the zip would register a temporary path. | `SettingsWindow.BtnSave_Click`, `AutoStartService` | Drop the field or reconcile it from the registry; warn when the exe path is under `%TEMP%`. | S |
| P2-5 | Windows 10 shows missing glyphs: icons use `Segoe Fluent Icons`, which ships only with Windows 11, though the README supports Windows 10. | `FlyoutWindow.xaml`, `SettingsWindow.xaml`, `TrayMenuWindow.xaml` | `FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets"`. | S |
| P2-6 | The error dialog says "Please try the action again" even for failures in background messages. | `App.OnStartup` | Reword, and mention the log. | S |
| P2-7 | Discovery runs on every reconnect when no static host is set. | `BraviaEngine.WorkerLoopCoreAsync` | Try the last discovered host first. | S-M |
| P2-8 | Unsupported paths are queried again on every 25 s keepalive; at Verbose each writes a log line. | `BraviaClient.GetInitialStatesAsync` | Remember unsupported paths per connection and skip them. | S |
| P2-9 | `ConfirmSignin`'s `success` field is ignored. | `BraviaClient.InitializeSessionAsync` | Treat `success == false` as a rejected handshake. | S |
| P2-10 | Dragging the rear-level slider sends one command per step, two RPCs each; volume is coalesced, rear is not. | `BraviaEngine.CommandDrainCoreAsync` | Coalesce `sound_setting.volume.rear` like `volume`. | S |
| P2-11 | Discovery accepts the first device that answers the control service; it does not check that it is the soundbar the credentials belong to. Since PR #12 the address must be local, but identity is unchecked. | `MdnsDiscovery.ParseResponse` | Compare an identity from the mDNS TXT record with the credentials' device id, if the firmware exposes one. | M |

### P3: maintainability and tests

| ID | Finding | Proposed fix | Effort |
| --- | --- | --- | --- |
| P3-1 | The orchestration code is the least tested: `App.xaml.cs` 4%, three windows 0%. WPF allows one `Application` per test run, so UI checks pile into one STA test. | Move startup, engine replacement, device recovery, and state dispatch into a WPF-free class with tests; add a shared STA fixture so UI tests can be separate. | M-L |
| P3-2 | The real `BraviaClient` (13%) is only exercised through fakes. | Test it against a fake HTTP/2 handler that speaks gRPC framing: session-random rotation, unsupported-path classification, renewal. | M |
| P3-3 | Parsers of untrusted network input are about half covered. | Seeded randomized tests that feed arbitrary bytes to `NotifyParser`, `StatesCodec`, `ProtobufWireCodec`, and `MdnsDiscovery.ParseResponse`, asserting they never throw and stay bounded. | S-M |
| P3-4 | Dead code, including a lenient OAuth code parser that skips the state check. | Remove `SonyOAuth.ParseAuthorizationCode` (used only by its tests), `StatesCodec.BuildGetStatesRequest`, `MdnsDiscovery.GetActiveIPv4Addresses`, `FlyoutWindow.ToggleFlyout`/`ShowFlyout()`/`PositionNearTray`, `FlyoutTransitionController.ForceHidden`, `NativeTrayIcon.LeftClickAction`, `SoundbarState.Input`, `AppSettings.Load()`/`Save()`. | S |
| P3-5 | `App.Log(string)` picks the level by matching words in the message, so rewording a message can silently change whether it is logged. | Pass explicit levels at call sites. | S-M |
| P3-6 | Win32 declarations are duplicated across four files and lack a DLL search-path restriction (CA5392, 27 hits). | One `NativeMethods` class using `LibraryImport`, with `DefaultDllImportSearchPaths(System32)`. | S-M |
| P3-7 | `BraviaEngine` is 1,500 lines; `ApplySnapshotCore` and `ApplyDeltaCore` each map every path to a field. | One table-driven path-to-field map used by both. | M |
| P3-8 | Small analyzer fixes worth taking. | Culture-explicit `StartsWith` in `SettingsWindow`, cached `JsonSerializerOptions`, `SHA256.HashData`. | S |

### P4: release, security, and project

| ID | Finding | Proposed fix | Effort |
| --- | --- | --- | --- |
| P4-1 | Release executables are unsigned, so SmartScreen warns about an unknown publisher. | Authenticode signing in `release.yml` (SignPath Foundation is free for open source; Azure Trusted Signing is low cost). | M |
| P4-2 | `main` has no branch protection or ruleset; Dependabot security updates are off; there is no code scanning. | Enable security updates and CodeQL for C#; optionally a ruleset requiring CI, with an owner bypass for direct commits. | S |
| P4-3 | The MIT-licensed repository ships artwork taken from Sony's app (Dolby Atmos, Dolby Audio, TrueHD, DTS-HD, DTS:X tiles; an IMAX mark) in `assets/icons/raw` and the PNGs built from it. MIT cannot license third-party artwork. | Have the licensing reviewed; replace with original badges or add a notice excluding third-party marks. (Not legal advice.) | S-M |
| P4-4 | 153 MB private bytes after days of running, against ~23 MB idle. Not a leak: WPF hardware rendering loads the NVIDIA driver (`nvgpucomp64.dll` 106 MB, `nvd3dumx.dll` 42 MB) on the first flyout open. | Try `RenderMode.SoftwareOnly`; check the acrylic backdrop visually first. | S to try |
| P4-5 | Native libraries are extracted to `%TEMP%` and leave a folder per release (ten on the reporter's PC). | Ship them beside the exe in the zip; the framework-dependent build would become a zip too. A release decision. | M |
| P4-6 | No update check and no way to remove local data. | Optional update check against GitHub releases; a "remove my data" action (credentials, logs, Run entry) and an uninstall section in the README. | M |

Looked at and found sound: bounded protobuf and mDNS parsing, OAuth with PKCE and a constant-time
state check, DPAPI credential storage with atomic replace, serialized credential renewal, theme
resources throughout the XAML (no hard-coded colors), automation names on icon buttons, hermetic
tests, CI with pinned action SHAs, gitleaks, Dependabot version updates, and a clean NuGet audit.

## 5. Working notes

- **Toolchain.** The reporter's PC had only the .NET 10.0.12 runtime. SDK 10.0.302 was installed
  system-wide with `winget install --id Microsoft.DotNet.SDK.10 --version 10.0.302 --exact`;
  winget's newest, 10.0.402, would not satisfy `global.json`.
- **The reporter's install.** The standalone exe runs from `%USERPROFILE%\Documents\bin\`. It is
  not registered in `HKCU\...\Run` (it was started from Explorer), even though `settings.json` says
  `StartWithWindows: true`; see the finding in section 4. Backups in that folder follow the pattern
  `<name>.exe.<version>.bak`.
- **Reading a bundle.** The single-file manifest can be parsed from the exe: find the 32-byte bundle
  signature in the app host, read the header offset from the 8 bytes before it, then the
  `Microsoft.NET.HostModel` manifest (v6: offset, size, compressed size, type, path per entry). The
  bundle id is the extraction folder name.
- **Where messages come from.** Scanning IL for `newobj DllNotFoundException::.ctor()` showed that no
  WPF code throws the parameterless form. `Marshal.GetExceptionForHR(0x80131524)` produces the same
  text, which led to the missing `mscorrc.dll`.

## 6. Deployments to the reporter's PC

| When | Build | Result |
| --- | --- | --- |
| 2026-10-14 00:22 | Working tree with fixes from sections 1 and 2; standalone single-file publish, version still `2.2.7+4f0fa58`; SHA-256 `c67c2f4e72fe3d828e9efe74867b87e270e4e3dad236c56d0189e766fce53f49`; bundle id `1cLnBSe7xu02` | Reconnected to the soundbar within a second; `PenImc_cor3`, `PresentationNative_cor3`, `wpfgfx_cor3`, `D3DCompiler_47_cor3`, `vcruntime140_cor3`, and `WebView2Loader` all loaded from the extraction folder; nothing logged at Critical. Replaced at 00:31. |
| 2026-10-14 00:31 | Adds the session renewal from section 3; SHA-256 `17714fe884f502e59df9677c28fe093dc4107bb3f098fae9f6db4de15fe48d9c`; bundle id `ba0CB2aUid0j` | Running. Handshake at 00:31:18; all six native DLLs pinned. At 00:38:01 the engine reconnected as designed to renew the 24-hour local credentials (they expired at 00:42); the new connection's handshake at 00:38:09 restarted the device-session clock. At 01:28:21 the proactive renewal succeeded on the open connection and the notify stream reopened, with no errors. The connection then stayed up through the old drop window and past 01:43 (section 3). Restarted at 01:43:31 only to restore the log level; it reconnected within a second. |

For the verification window `LogLevel` in the reporter's `settings.json` was set to `Info` (00:31 to
01:43). It has been restored; the file is byte-identical to the original, and the app was restarted
so the running copy logs at `Critical` again.

The deployed build was made from `4f0fa58` plus these changes, so it lacks PR #12's discovery and
static-host hardening and reports itself as 2.2.7. Replace it with the v2.2.8 release build (same
procedure) to bring the install in line with `main`.

Procedure: stop the running copy (force-stop; a normal Exit from the broken v2.2.7 would hit the
missing `vcruntime140_cor3.dll` at shutdown), keep the original as
`Documents\bin\BraviaTheatrePC.exe.2.2.7.bak` (SHA-256 `ea1272602fa4c38ce89b2e4481c71c79ff193a771babf69773fdf178deecc95e`),
copy the new exe over `BraviaTheatrePC.exe`, and start it with `explorer.exe <path>` so it runs in
the desktop session, outside the agent's process tree. Rollback: stop it, copy the `.bak` back.

## 7. Overnight work, 2026-10-14 (released in v2.3.0)

The reporter asked for batches 1 to 3 from section 4, plus P4-4 (memory) and P4-5 (packaging), to
be implemented and tested on their PC while they slept. All of it was released as v2.3.0 the next
morning, at the reporter's request; PR #14, merged during batch 1, was already on `main`.

### Batch 1 (done; 427 tests pass)

- P1-1: `ControlEndpoint` builds IPv4 addresses from the decimal octets, so `192.168.1.010` is
  192.168.1.10. Tests added; reverting the fix fails three cases.
- P1-3: `AuthDialog` hosts popups in its own window on the same WebView2 environment and intercepts
  the `ssh-app://` callback there too; `ProcessFailed` (browser or renderer exited, unresponsive)
  switches to manual mode. Not exercised against Sony's page.
- P1-4: PR #14 rebased by Dependabot; its CI ran all 420 tests (including the pinning tests) against
  WebView2 1.0.4258.31; merged as `62a1c83` and pulled.
- P2-1: the engine's own cancellation, which gRPC reports as `RpcException(Cancelled)`, no longer logs
  "Notify stream ended". Test with a fake that cancels the way Grpc.Net.Client does.
- P2-3: `DurableFile.WriteNew` flushes to disk before the rename, for credentials and settings.
- P2-4: `AppSettings.StartWithWindows` removed (old files still load); Settings refuses to register
  autostart from a temp folder (`AutoStartService.IsTemporaryLocation`, tested).
- P2-5: icon font fallback `Segoe Fluent Icons, Segoe MDL2 Assets`; all nine glyphs used were checked
  present in both fonts.
- P2-6: the error dialog no longer assumes the user just did something.
- P2-7: the engine remembers the soundbar after a successful handshake and reconnects there first,
  forgetting it if that attempt fails before a session exists. Discovery is injectable for tests
  (`DiscoveryReuseTests`; removing either the remembering or the forgetting fails a test).
- P2-8: an unsupported path is logged once per connection, not on every 25 s keepalive. It is still
  queried each time: a path rejected in standby may be supported once the soundbar is on.
- P2-9: `ConfirmSignin`'s success flag is logged (Verbose) when false, not enforced. Proto3 reads an
  absent field as false, so enforcing it could break devices that never set it. Device evidence
  pending.
- P2-10: rear-level slider commands are coalesced like volume (theory test for both; there was no
  coalescing test at all before).
- P2-11: not feasible through mDNS. The reporter's soundbar advertises only `capId`, `dest`, `fwVer`,
  `ifVer`, `imName`, `ipAddr`, `mColor`, `mNumber`, `protoVer`, `rCounter`, none of which matches the
  account's device id. The handshake (`ConfirmSignin` sends the hash of the expected device id) is
  the identity check; whether a mismatch shows in its success flag needs the P2-9 evidence.
- P3-4: removed `SonyOAuth.ParseAuthorizationCode` (and its test), `StatesCodec.BuildGetStatesRequest`,
  `MdnsDiscovery.GetActiveIPv4Addresses`, three `FlyoutWindow` entry points,
  `FlyoutTransitionController.ForceHidden`, `NativeTrayIcon.LeftClickAction`, `SoundbarState.Input`,
  `AppSettings.Load()`/`Save()`, `AutoStartService.SetAutoStart`.
- P3-8: `SHA256.HashData`, cached `JsonSerializerOptions`, ordinal key-name check in Settings.

### P4-5 packaging (done; release assets unchanged)

Decision: keep shipping one executable; do not ship the DLLs beside it. The reporter keeps loose
single-file tools in `Documentsin` with `.bak` copies for rollback. Seven files there would clutter
a shared folder, could be overwritten by another app's `wpfgfx_cor3.dll` or `WebView2Loader.dll`, and
an `.exe.bak` paired with newer DLLs is a broken rollback. Both reported problems are fixed without
changing the download:

- Deletion under the running app: fixed in v2.2.8 (every extracted library is pinned; loaded files
  cannot be deleted). The host restores missing files at the next start.
- A folder per release: new `BundleExtractionCleanup` runs at startup (background, after the
  single-instance check) and deletes other bundle folders under `%TEMP%\.net\BraviaTheatrePC`,
  `...\BraviaTheatrePC-FrameworkDependent` and the current executable's name, then the emptied
  parent. It keeps any folder in `NATIVE_DLL_SEARCH_DIRECTORIES` or holding a loaded module, and
  skips folders it cannot delete. Verified on the reporter's PC: 12 + 1 stale folders went, the
  running build's folder stayed. Tests: three cases; removing the keep check or the catch fails one.
- The publish folder now holds only `BraviaTheatrePC.exe` (`WebView2NeverCopyLoaderDllToOutputDirectory`
  drops the package's duplicate loader under `runtimes\`, `PublishReferencesDocumentationFiles` the
  XML docs; a development build still gets the loader under `runtimes\` from the SDK). CI and the
  release workflow fail if anything else appears beside the executable, and `PublishFootprintTests`
  pins `IncludeNativeLibrariesForSelfExtract=true` and keeps it off the command lines.
- Considered and rejected: `IncludeNativeLibrariesForSelfExtract=false` (measured: standalone exe plus
  six DLLs, framework-dependent exe plus `WebView2Loader.dll`); a custom extraction directory (the
  host only reads `DOTNET_BUNDLE_EXTRACT_BASE_DIR` from the environment, before managed code runs).

### P4-4 memory (done; software rendering)

`App.OnStartup` sets `RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly` before any window
exists. Measured on the reporter's PC (RTX 4080 SUPER, display off, 1280x720 fallback mode), same
code with and without the line, fresh start, then five flyout open/close cycles:

| Rendering | Private bytes idle | After 1 flyout | After 5 | Private WS after 5 |
| --- | --- | --- | --- | --- |
| Hardware | 54.8 MB | 71.3 MB | 73.0 MB | 35.5 MB |
| Software | 40.9 MB | 54.0 MB | 55.9 MB | 31.8 MB |

The long-running hardware build had reached 128.9 MB private bytes (215.7 MB working set) after
8 hours, and 153 MB after days. With the display off the hardware path never presents (WPF holds
frames while the output is occluded), so the hardware rows understate a normal day; re-measure with
the display on. WPF still loads `d3d9.dll` and the NVIDIA user-mode driver images in software mode
(it probes the adapter), but the driver's runtime allocations and `nvspcap64.dll`/`nvppex.dll` are
gone.

Appearance, checked with screenshots of the real desktop over a striped test window:
the flyout and tray menu show live acrylic (stripes blurred and dark-tinted through the panel),
rounded corners, crisp text; Settings shows Mica. A side finding: with the display off, the
hardware build showed the flyout as an empty acrylic rectangle (no content at all) because WPF
does not render while occluded; the software build draws normally. That is what a Remote Desktop
viewer would have seen with the monitor off.

Screenshot tooling for later sessions: `scratchpad` scripts posted the app's registered
`BRAVIA_THEATRE_PC_SHOW_FLYOUT` message, posted `WM_TRAYICON` (`0x0464`, lParam `WM_CONTEXTMENU`) to
the hidden `BraviaTheatre_TrayMsgWindow` for the tray menu, invoked buttons through UI Automation,
and captured with `Graphics.CopyFromScreen`.

### Batch 2 (done; needs the reporter's testing)

- P1-2: `app.manifest` declares PerMonitorV2 (with `true/PM` fallback), Windows 10/11 `supportedOS`,
  and Common-Controls v6 (themed message boxes; WPF's `MessageBox` is Win32's). The SDK default
  manifest had only `asInvoker`. The flyout and tray menu already placed themselves in physical
  pixels per monitor (`GetDpiForWindow`, `GetMonitorInfo`), and `ResolvePlacement` positions twice so
  a DPI change on the move is measured. Only one 100% monitor was available, so mixed-DPI and RDP
  placement are untested.
- P2-2: `NativeTrayIcon.ShowNotification` (`NIF_INFO`, `NIIF_USER | NIIF_LARGE_ICON |
  NIIF_RESPECT_QUIET_TIME`); Windows shows it as a toast. `TrayAttentionNotifier` turns engine states
  into one notification per transition into sign-in-needed or choose-soundbar (the engine republishes
  the same state on every retry, so a level trigger would repeat). Skipped while the sign-in or
  soundbar dialog is open. Selecting it (`NIN_BALLOONUSERCLICK`) opens the flyout, whose card has the
  action. Six tests. Not shown on the real desktop: that needs a revoked token or a reassociation.
- P1-5: the daily renewal no longer drops the connection. When the keys near expiry the keepalive
  calls `RefreshAsync`, then `IBraviaClient.ReauthenticateAsync(renewed)`: the client swaps
  credentials under its session lock, repeats ConfirmSignin/ConfirmKeys/GetSessionRandom on the open
  channel, and restores the old credentials if that fails. The notify stream is closed and reopened
  around it as in the hourly session renewal. Outcomes: sign-in or soundbar choice needed, or the
  handshake fails, so reconnect (the old path, which reports or retries); transient/unavailable
  renewal, so keep the still-valid keys and stop retrying on this connection (what the old path ended
  up doing after a reconnect). Four tests replace `KeepaliveBoundary_ReconnectsAndRenewsAuthoritativeSnapshot`
  (in place: one client, same generation, no disconnected state, stream reopened; rejection, so a
  second client with the renewed keys and stale-generation deltas ignored; sign-in needed; transient
  failure with three later polls and one refresh). Each of four mutations (always reconnect,
  transient reconnects, no stream reopen, ignore handshake failure) fails a test.
  **Device verification due at about 00:33 on 2026-10-15** (the keys expire 2026-10-14T21:38:09Z).
  At `Critical` the log shows `[Credential renewal] Trigger=proactive ... renewing on the open
  connection`, the renewal phases, then either `[Credential renewal] Handshake completed with the
  renewed credentials on the open connection.` (success) or `... failed on the open connection
  (...); reconnecting with them.` (fallback; still recovers).
- Keepalive cancellation reported as `RpcException(Cancelled)` during shutdown is no longer logged as
  a failure (the P2-1 treatment, for the other loop).
- `DescribeFailure` adds the transport cause chain by type, e.g. `RpcException (StatusCode=Unknown,
  Cause=HttpRequestException(ResponseEnded) <- ...)`, never messages (they carry addresses). Prompted
  by the log: at 02:20:20 a keepalive failed with only `StatusCode=Unknown` and the app reconnected;
  the following `Notify stream ended: RpcException (StatusCode=Cancelled)` line was the noise P2-1
  removes. Cause unknown; the next one will say more.

### Deployed 2026-10-14 10:26

Built from the working tree (batch 1, batch 2, P4-4, P4-5; reports 2.2.8). Standalone publish,
SHA-256 `D911185F9F85CC5AAAF29F8FF6A810158454CB1733E978416C20FA15FB33432E`, copied over
`Documents\bin\BraviaTheatrePC.exe` and started with `explorer.exe`. The build it replaced (00:31,
SHA-256 `17714FE8...8D9C`) is kept as `Documents\bin\BraviaTheatrePC.exe.2.2.8-pre.bak`; the original
`...2.2.7.bak` is untouched. Checked after start: running from `Documents\bin`, connected to
the soundbar on port 55051, all six native libraries loaded from the one remaining extraction folder,
40 MB private bytes. Rollback: stop it, copy either `.bak` over the exe, start it again.

The display reported 1280x720 while the monitor was off (the reporter suspected resolution changes).
The session was unlocked (console, active), so screenshots worked.

### Batch 3 (done)

- P3-1: `EngineStateRelay` (UI `Services/`) now owns the hand-off of engine states to the UI thread
  that lived in `App`: only the latest state waits, at most one dispatch is queued, a replaced
  engine's states are dropped, and a refused post does not block later states. `App` supplies the
  dispatcher. Five tests, one a four-thread stress test. Breaking the refused-post reset or the
  generation filter on publish fails a test; clearing the waiting state on a new generation is a
  second guard behind the dispatch-time check, so removing just one of those is not observable.
  `WpfApplicationFixture` (xUnit assembly fixture: one plain `Application` with
  `AppResources.xaml` on an STA thread) replaces the single STA test in `ReleasePolishTests`; its
  checks are now eight tests in `FlyoutWindowTests`. Found while building it: a pumped dispatcher
  raises Startup without `Application.Run`, so a fixture made from the real `App` ran
  `App.OnStartup` inside the test process. Here that hit the single-instance check and made the
  running app show its flyout (twice, closed again); on CI it would read settings and open sign-in.
  The App's three styles moved to `AppResources.xaml` (merged by `App.xaml`) so the fixture can load
  them without `App`; flyout and Settings checked on screen afterwards. Not extracted: engine
  replacement and device recovery. They are dialog-driven, and the fixture now makes them testable.
- P3-2: `BraviaClient` has an internal constructor taking `GrpcChannelOptions`.
  `BraviaClientProtocolTests` run the real client against a fake `HttpMessageHandler` that answers
  with gRPC framing and `grpc-status` trailers: the handshake proofs; the session random used and the
  rolling one adopted; an unsupported path skipped and reported once at Verbose; reauthentication
  switching every later call to the renewed keys; a rejected reauthentication keeping the working
  keys; an unconfirmed sign-in tolerated. Removing the restore, the rolling-random adoption, or the
  credential swap fails a test.
- P3-3: `ParserFuzzTests`: notification, delta, states, session-token, field and mDNS parsers, four
  seeds, 3,000 inputs each (random bytes, mutated valid messages, random protobuf trees). No parser
  failed. Removing the length bound in `TryDecodeField` fails 20 of the 24 cases.
- P3-5: `DiagnosticLevel` (Core) on every log call; `App.Log(string)` and its keyword classifier are
  gone. Levels were kept as the classifier chose them except where it was wrong: sign-in and
  soundbar-choice decisions and handshake rejections are Critical (were Verbose, so the default log
  never said why sign-in was asked for); cleanup, dispose and cancellation warnings are Info (were
  Critical only because a type name contains "Exception"); credential renewals and session events
  are Info. Credential-renewal diagnostics stay Critical through `App.LogCredentialDiagnostic`. A
  test pins the sign-in requirement at Critical.
- P3-6: `NativeMethods` (`LibraryImport`) holds the Win32 calls used in more than one place (cursor,
  monitor, window rectangle and position, DPI, message time, registered messages); the duplicates in
  `FlyoutWindow`, `TrayMenuWindow`, `WindowBackdropService`, `NativeTrayIcon` and `App` are gone.
  `[assembly: DefaultDllImportSearchPaths(System32)]` covers every import (CA5392). Single-use
  imports, and those with string structs such as `NOTIFYICONDATA`, stay `DllImport` beside their
  caller. Checked on the desktop: flyout and tray-menu bounds identical before and after
  (846,253-1230,660 and 898,444-1178,664), and a second launch still opens the running copy's flyout.
- P3-7: one `StateFields` table in `BraviaEngine` maps paths to fields for snapshots and
  notifications; codec and channel keep their clearing rule with shared path lists. Behavior change:
  snapshots now accept `playback_control.power`, which only notifications knew. Tests: every queried
  path is mapped, 17 values read the same through a snapshot and a notification, unusable values
  leave a field alone, and the alternative power path works in both.

### Final deployment, 2026-10-14 10:52

Everything above, built from the working tree: 508 tests pass, `dotnet format` clean, publish folder
holds only the exe. SHA-256 `16A959BF7A6E473742196173CC3FB64662E607803A0EDB567FC0192ED9F0A215` over
`Documents\bin\BraviaTheatrePC.exe` (the 10:26 build it replaced was not kept; it is reproducible).
Checked: running from `Documents\bin`, connected to the soundbar on port 55051, all six native libraries
loaded, one extraction folder in `%TEMP%\.net`, 40 MB private bytes at start. Rollback as above
(`.2.2.8-pre.bak` is the 00:31 build, `.2.2.7.bak` the original).

### Open items for the reporter

1. **Tonight about 00:33**: the first in-place credential renewal on the device (P1-5). Look for
   `[Credential renewal] Handshake completed with the renewed credentials on the open connection.`
   in the log; the fallback line also recovers, but means the device wants a new connection.
2. With the display on, compare private bytes against the hardware-rendering figures (P4-4).
3. P1-2 needs a second monitor with another scale, or a Remote Desktop session, to check placement.
4. P2-2's notification appears only when sign-in or a soundbar choice is really needed.
5. P2-9 evidence: a Verbose log over one handshake shows whether ConfirmSignin reports success.
6. Optionally replace the build in `Documents\bin` with the v2.3.1 framework-dependent release
   asset, so Settings reports 2.3.1 and the install is the attested build (same code as the
   16:14 deployment in section 8).

## 8. Framework-dependent download never started (fixed in v2.3.1)

### Symptom

The reporter downloaded v2.3.0's `BraviaTheatrePC-FrameworkDependent.exe`, renamed it to
`BraviaTheatrePC.exe` in `Documents\bin` (replacing the standalone build), and double-clicking it did
nothing. They had seen the same with earlier releases and suspected their .NET install. (The PC had
also rebooted at 11:31, and the app is not set to start with Windows, so nothing was running.)

### Evidence

- Application log: `APPCRASH` at 14:37 and 16:00, faulting module `coreclr.dll` 10.0.12, exception
  `0xc0000602` (fail-fast). WER archives hold the same crash for the framework-dependent exe on
  2026-09-21. No `.NET Runtime` event and nothing on stderr; the crash report's loaded modules stop
  at `coreclr`, `System.Private.CoreLib`, `clrjit` and `icu.dll`, so it died before `Main`.
- The download is intact (SHA-256 matches the release's `.sha256`), and a local build crashes too.
- .NET is fine: `Microsoft.WindowsDesktop.App` 10.0.10 and 10.0.12 are installed, a host trace
  (`COREHOST_TRACE`) resolves both frameworks, and the test host runs WPF on the same runtime.
- Not a version gap: SDK 10.0.302 builds against 10.0.10, the app rolls forward to 10.0.12, and
  forcing 10.0.10 (a private `DOTNET_ROOT` of junctions, confirmed by trace) crashes the same way.
- Bisect, framework-dependent: single file + ReadyToRun crashes; single file without ReadyToRun
  runs; folder + ReadyToRun runs; the shipped exe runs with `DOTNET_ReadyToRun=0`; single file +
  ReadyToRun with only the main assembly excluded (`PublishReadyToRunExclude`) runs. The
  self-contained build loads the same ReadyToRun main assembly without trouble.
  (`DOTNET_ReadyToRunExcludeList` had no effect; release runtimes seem to ignore it.)

Conclusion: a framework-dependent single-file bundle whose main assembly is ReadyToRun fails fast
inside the runtime before `Main`, on 10.0.10 and 10.0.12 alike. Not specific to the reporter's install. The
framework-dependent asset has probably never started since `PublishReadyToRun` was enabled, and CI
only checked that the files were published.

### Fix (v2.3.1)

- `PublishReadyToRun` is set only when `SelfContained` is true. The framework-dependent variant JITs
  the app's code (it starts in a fraction of a second; 21 MB private at start).
  `PublishFootprintTests` pins the condition; restoring the unconditional property fails it.
- CI and the release workflow now launch both published executables and fail if either exits within
  20 seconds. Without credentials the app opens sign-in and waits. Checked locally: both fixed
  builds pass, and the v2.3.0 framework-dependent exe fails with `0xC0000602`. Not yet run on a
  GitHub runner.
- Deployed: the fixed framework-dependent build (SHA-256 `F04E10A9...F95FF400`) over the broken
  download in `Documents\bin`, started in the reporter's Remote Desktop session at 16:14. It runs on
  the installed 10.0.12. It had not connected by 16:16 because the soundbar answered ping but refused
  TCP 55051 (its control service was not listening; it accepted connections in standby that morning).
  The app keeps retrying.

### Release

Released as v2.3.1 the same afternoon at the reporter's request, because the v2.3.0
framework-dependent asset is broken. The smoke step's first runs on GitHub are recorded in the
status table.
