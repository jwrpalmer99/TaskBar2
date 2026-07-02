# TaskBar2

TaskBar2 is a WPF prototype that creates appbar windows on secondary monitors and replicates a taskbar-like surface.

## Current behavior

- Creates a bottom appbar on each non-primary monitor.
- Shows all eligible top-level application windows as icon buttons on every secondary taskbar.
- Can be configured to show only the apps on each secondary monitor.
- Can align app buttons to the left or center of the secondary taskbar.
- Can scale the taskbar height and icon/button sizes.
- Has separate configurable intervals for taskbar polling and tray geometry refresh.
- Left-click activates, restores, or minimizes the target window.
- Right-clicking an app button opens that window's native system menu.
- Mirrors notification icons from hook-fed snapshots when available, otherwise falls back to Explorer toolbar/UI Automation probing.
- Hook-fed tray icons can receive left and right click routing through their captured `NOTIFYICONDATA` callback target.
- The invasive live tray icon hook is opt-in from Settings and runs through an EasyHook-based helper agent.
- Elevated tray apps can be captured by enabling the separate elevated hook-agent setting and approving the UAC prompt.
- Right-clicking empty taskbar space opens a TaskBar2 menu with Settings, Refresh, and Exit.
- A TaskBar2 tray icon also exposes Settings, Refresh displays, and Exit.
- Debug output is written to `%APPDATA%\TaskBar2\taskbar2.log` and can be opened from the TaskBar2 context menu.
- The app-side tray hook endpoint is written to `%APPDATA%\TaskBar2\tray-hook-endpoint.json`.

## Known limits

Windows does not provide a supported public API for third-party apps to enumerate and own Explorer's notification area icons exactly like Explorer does. The current non-invasive fallback reads classic tray toolbars where available and uses UI Automation/rendered-icon probing on Windows 11 when Explorer exposes a visible tray surface.

Truly live tray icons while Explorer never renders the primary tray require a hook/injector that captures `NOTIFYICONDATA.hIcon` from `Shell_NotifyIconW/A` calls in the source processes. TaskBar2 includes an opt-in EasyHook helper for that path. See `docs/tray-hook-architecture.md`.

Unread badges and progress overlays are also not exposed as a simple taskbar enumeration API. Matching DisplayFusion-level fidelity will require deeper shell integration, accessibility/UI Automation probing, or an Explorer-adjacent hook strategy.

## Run

```powershell
dotnet run --project .\TaskBar2\TaskBar2.csproj
```
