# Tray hook architecture

TaskBar2 has an opt-in invasive tray-icon path:

- `TrayIconHookServer` listens on a current-user named pipe and publishes its endpoint at `%APPDATA%\TaskBar2\tray-hook-endpoint.json`.
- `TrayIconSnapshotStore` accepts add, modify, set-version, hidden, and delete events.
- `TrayMirrorControl` prefers hook-fed snapshots over the old Explorer/UIA fallback.
- Hook-fed tray buttons can post the captured `uCallbackMessage` back to the original `NOTIFYICONDATA.hWnd`.
- `TaskBar2.TrayHook.Agent` scans the current interactive session and injects the hook assembly into candidate user processes.
- `TaskBar2.TrayHook.Injectee` uses EasyHook to intercept `shell32!Shell_NotifyIconW` and `shell32!Shell_NotifyIconA` inside each target process.

The path is disabled by default. Enable it in TaskBar2 Settings with `Enable invasive live tray icon hook`.

Elevated tray owners are not reachable from the standard helper. Enable `Run tray hook agent elevated` to start a second helper with `runas`; Windows will show a UAC prompt, and that elevated helper targets only elevated tray-owner processes.

## Hook responsibilities

1. Inject the EasyHook-managed hook assembly into candidate user processes in the current session.
2. Intercept `Shell_NotifyIconW/A(dwMessage, pnid)` and call the original function.
3. Copy the notification identity fields from `NOTIFYICONDATA`: `hWnd`, `uID`, `guidItem` when `NIF_GUID` is used, `uCallbackMessage`, and `uVersion` from `NIM_SETVERSION`.
4. When `hIcon` is present, duplicate or render it inside the source process and encode the icon image as PNG bytes.
5. Send one newline-delimited JSON message per notification update to the named pipe endpoint.
6. Never inject into system services, elevated processes from a non-elevated TaskBar2 instance, protected processes, or processes in another user session.

The current agent enforces the session boundary, filters candidates through Windows' notification icon settings registry, and skips known critical process names. The standard helper targets standard-integrity processes only; the optional elevated helper targets elevated processes only. Failed injection attempts are backed off for 30 seconds and logged to `%APPDATA%\TaskBar2\tray-hook-agent.log`.

## Pipe message

The managed endpoint expects UTF-8, newline-delimited JSON:

```json
{
  "protocolVersion": 1,
  "operation": "Modify",
  "shellMessage": 1,
  "ownerHwnd": 123456,
  "iconId": 7,
  "guid": "00000000-0000-0000-0000-000000000000",
  "callbackMessage": 32769,
  "notificationVersion": 4,
  "toolTip": "Example",
  "iconPngBase64": "iVBORw0KGgo...",
  "hidden": false
}
```

`shellMessage` follows the shell constants: `NIM_ADD = 0`, `NIM_MODIFY = 1`, `NIM_DELETE = 2`, and `NIM_SETVERSION = 4`.

For `NIM_DELETE`, `operation: "Delete"`, or `hidden: true`, TaskBar2 removes the snapshot.

For updates that do not include `iconPngBase64`, TaskBar2 preserves the last icon image for that identity and updates metadata only.

## Click routing

Classic/UIA fallback icons still route clicks by moving the cursor to Explorer's tray rectangle.

Hook-fed icons route clicks without Explorer rendering:

- Legacy notification versions post `uCallbackMessage` with `wParam = uID` and `lParam = WM_LBUTTONDOWN/UP` or `WM_RBUTTONDOWN/UP`.
- Version 4 notification icons post `uCallbackMessage` with low word `lParam` set to the mouse/context event, high word `lParam` set to the 16-bit icon id, and `wParam` set to the current cursor coordinate pair.

This matches the documented callback shape used by `NOTIFYICONDATA.uCallbackMessage`.

## Build output

`TaskBar2.csproj` builds the helper projects and copies the agent payload to:

`TaskBar2\bin\<Configuration>\net8.0-windows\TrayHookAgent`

The copied payload includes EasyHook's x86/x64 loader binaries, `TaskBar2.TrayHook.Agent.exe`, and `TaskBar2.TrayHook.Injectee.dll`.

## Limits

This captures notification icons from processes that call `Shell_NotifyIcon` after the hook is active. It cannot reconstruct an icon that was added before the target process was injected unless that process later sends `NIM_MODIFY`, `NIM_ADD`, or `NIM_SETVERSION`.

Windows system indicators that are implemented directly by the Windows 11 XAML tray rather than by a third-party `Shell_NotifyIcon` caller may still need separate Explorer/UIA handling.
