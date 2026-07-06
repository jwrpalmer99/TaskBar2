# TaskBar2

TaskBar2 adds a Windows taskbar to secondary monitors. It mirrors your open apps, pinned apps, tray icons, clock, hover previews, and right-click menus so extra monitors feel closer to the primary Windows taskbar. 

For OLED users worried about taskbar burn-in, you can set your Windows taskbar to just be on primary monitor and autohide, then use an always visible TaskBar2 on your other monitors.

## What It Does

- Shows a taskbar on each non-primary monitor.
- Mirrors pinned and running app buttons in the same order as the primary taskbar.
- Supports grouped app buttons, hover previews, and basic jump-list style menus.
- Mirrors tray icons, including live icon updates from supported apps.
- Routes tray icon clicks and right-click menus back to the real app.
- Supports per-monitor scale, alignment, clock visibility, notification area visibility, opacity, and auto-hide.
- Can pause most updates while a fullscreen app or game is running.
- Follows Windows light/dark mode.

## Download And Run

1. Download the latest release zip from GitHub Releases.
2. Extract it to a normal folder, for example `C:\Tools\TaskBar2`.
3. Run `TaskBar2.exe`.

To close TaskBar2, right-click the TaskBar2 tray icon or an empty area of the mirrored taskbar and choose `Exit`.

## Settings

Right-click the TaskBar2 tray icon or an empty area of the mirrored taskbar, then choose `Settings`.

Useful settings include:

- `Show only apps on this monitor`
- `Mirror primary notification area`
- `Show ALL tray icons`
- `Show clock`
- `Automatically hide the taskbar`
- `Taskbar scale`
- `Taskbar opacity`
- `Pause updates while fullscreen apps are running`

Some settings are global. Monitor-specific settings are grouped under the selected monitor in the Settings window.

## Startup

TaskBar2 can be started automatically with Windows by creating a Scheduled Task that runs `TaskBar2.exe` when you log on.

If tray icons are missing immediately after logon, exit TaskBar2 and start it again. Some tray apps register late during Windows startup, and TaskBar2 is still improving this path.

## Troubleshooting

- If a tray icon is missing, restart that app first.
- If several tray icons are missing after a TaskBar2 update, restart TaskBar2.
- If an injected tray app behaves oddly, restart that app.
- Debug logs are written to `%APPDATA%\TaskBar2`.

## Developer Notes

TaskBar2 is a .NET Windows app with a native Win32/Direct2D taskbar renderer. It uses helper hook agents for shell integration that Windows does not expose through a normal public API.

The tray icon hook captures `NOTIFYICONDATA` updates from apps that call `Shell_NotifyIconW/A`. This is needed because Windows does not provide a supported API for a third-party app to enumerate and fully own Explorer's notification area.

The Explorer taskbar hook is used to improve taskbar button order, pinned apps, icons, and related metadata. Some behavior is private, version-sensitive, and may differ from Explorer.

See [docs/tray-hook-architecture.md](docs/tray-hook-architecture.md) for more detail.

## Build

```powershell
dotnet build .\TaskBar2\TaskBar2.csproj -c Release
```

## Release Automation

GitHub Actions builds TaskBar2 on pull requests and pushes to `main`.

To create an end-user release, push a version tag:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The workflow publishes a Windows zip to the matching GitHub Release.
