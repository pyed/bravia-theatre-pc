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
| Fixed build on the reporter's PC | Running since 2026-10-14 00:31; predates the PR #12 merge (section 6) |

Released as **v2.2.8**: one commit on `main`, tagged `v2.2.8`, whose push runs the Release workflow.
It sits on `432853d`, the merge of PR #12 (mDNS answer validation, static-host validation through
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

## 4. Open findings (not fixed)

| Priority | Finding | Evidence | Suggested fix |
| --- | --- | --- | --- |
| Medium | Memory: 153 MB private bytes after 5.8 days, against the ~23 MB idle figure in the csproj comment. | Not a leak. WPF hardware rendering loaded the NVIDIA user-mode driver on the first flyout open: `nvgpucomp64.dll` (106 MB image) and `nvd3dumx.dll` (42 MB), plus driver heaps. | `RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly` would avoid it, but the DWM acrylic backdrop relies on WPF alpha composition. Check visually before adopting. |
| Low | Windows 10: icons use the `Segoe Fluent Icons` font, which only ships with Windows 11, yet the README lists Windows 10 as supported. | `FlyoutWindow.xaml`, `SettingsWindow.xaml` | `FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets"` (the glyphs used exist in both). |
| Low | Credential and settings files are written and then renamed without flushing to disk. A power cut can leave an empty `credentials.dat`, forcing a new Sony sign-in. | `SonyCredentialStore.TrySave`, `AppSettings.TrySave` | Write through a `FileStream` and call `Flush(flushToDisk: true)` before `File.Move`. |
| Low | Discovery connects to the first device that answers Sony's control service; it does not check that it is the soundbar the credentials belong to. | `MdnsDiscovery.ParseResponse` takes the first instance. Since PR #12 the answer must point at a local-scope address on the receiving subnet, but the fingerprint still proves only that the service exists, not the device identity. | Compare an identity from the mDNS TXT record with the credentials' device id, if the firmware exposes one. |
| Low | Discovery runs again on every reconnect when no static host is set, including the hourly ones. | `BraviaEngine.WorkerLoopCoreAsync` | Try the last discovered host first. |
| Low | `ConfirmSignin`'s `success` field is ignored. | `BraviaClient.InitializeSessionAsync` | Treat `success == false` as a rejected handshake. |
| Low | Dragging the rear-level slider sends one command per step, two RPCs each; volume commands are coalesced, rear ones are not. | `BraviaEngine.CommandDrainCoreAsync` | Coalesce `sound_setting.volume.rear` like `volume`. |
| Low | The error dialog says "Please try the action again" even when the failure came from a background message, not an action. | `App.OnStartup` | Reword. |
| Low | Every deliberate reconnect (for example the daily renewal of the 24-hour local credentials) logs `[Critical] Notify stream ended: RpcException (StatusCode=Cancelled)`. The cancellation is the engine's own, but gRPC reports it as `RpcException`, which the `OperationCanceledException` filter misses. | `BraviaEngine.NotifyLoopAsync`; seen at 00:38:01 on 2026-10-14 | Also accept `RpcException { StatusCode: Cancelled }` while the connection token is cancelled. |
| Low | Renewing the 24-hour local credentials still drops the connection once a day (Disconnected state, discovery, new handshake). | `KeepAlivePollLoopAsync` returns to force a reconnect when the keys near expiry | Renew the credentials, then hand the open connection a new client, or re-handshake with the new keys. Needs `BraviaClient` to accept new credentials. |
| Low | `AppSettings.StartWithWindows` is written by the Settings window but never read; the registry Run key is the real state, and the two drift. On the reporter's PC the file says `true` and there is no Run entry. | `SettingsWindow.BtnSave_Click`, `AutoStartService` | Drop the field, or reconcile it from `AutoStartService.IsAutoStartEnabled()` when loading. |
| Info | Every release build leaves its own extraction folder under `%TEMP%\.net\BraviaTheatrePC`; ten were present, mostly emptied by cleanup. | | Cosmetic. |

Looked at and found sound: bounded protobuf and mDNS parsing, OAuth with PKCE and a constant-time
state check, DPAPI credential storage with atomic replace, serialized credential renewal, CI with
pinned action SHAs, gitleaks, Dependabot, and a clean NuGet audit.

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
