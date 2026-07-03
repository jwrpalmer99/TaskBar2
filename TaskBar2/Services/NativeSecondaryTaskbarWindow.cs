using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TaskBar2.Models;
using TaskBar2.Native;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using PixelFormat = Vortice.DCommon.PixelFormat;
using Screen = System.Windows.Forms.Screen;
using WpfImageSource = System.Windows.Media.ImageSource;
using WpfPixelFormats = System.Windows.Media.PixelFormats;

namespace TaskBar2.Services;

internal sealed class NativeSecondaryTaskbarWindow : ISecondaryTaskbarHost
{
    private const string WindowClassName = "TaskBar2.NativeSecondaryTaskbarWindow";
    private static readonly TimeSpan ActiveFullscreenPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PausedFullscreenPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AutoHidePollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan AutoHideHideDelay = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan HoverPopupPollInterval = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan HoverPopupCloseDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan ClosedPreviewRefreshDelay = TimeSpan.FromMilliseconds(500);
    private const int TbpfNoProgress = 0;
    private const int TbpfIndeterminate = 1;
    private const int TbpfError = 4;
    private const int TbpfPaused = 8;
    private const int MinimumUsableExplorerButtonImageBytes = 512;
    private const int MaxJumpListMenuItemTextLength = 96;
    private const double NativeScaleBaseline = 1.5;
    private const float RenderTargetDpi = 96.0f;
    private const string TrayOverflowButtonKey = "tray-overflow";
    private const string ThemePersonalizeRegistryKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string SystemUsesLightThemeRegistryValue = "SystemUsesLightTheme";
    private const int DarkTaskbarBackgroundRed = 31;
    private const int DarkTaskbarBackgroundGreen = 31;
    private const int DarkTaskbarBackgroundBlue = 31;
    private const int LightTaskbarBackgroundRed = 243;
    private const int LightTaskbarBackgroundGreen = 243;
    private const int LightTaskbarBackgroundBlue = 243;
    private const int TopBorderHeight = 2;
    private static readonly string ExplorerButtonImageCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskBar2",
        "ExplorerButtonImageCache");
    private static readonly NativeMethods.WndProc WindowProcedure = StaticWndProc;
    private static readonly Dictionary<IntPtr, NativeSecondaryTaskbarWindow> Windows = [];
    private static bool _windowClassRegistered;

    private readonly Dispatcher _dispatcher = System.Windows.Application.Current.Dispatcher;
    private readonly Screen _screen;
    private readonly WindowTracker _windowTracker;
    private readonly System.Windows.Forms.ContextMenuStrip _contextMenu = new();
    private readonly System.Windows.Forms.ContextMenuStrip _appContextMenu = new();
    private readonly NativeTrayIconProvider _trayIconProvider = new();
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _trayRefreshTimer;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _groupFlyoutTimer;
    private readonly DispatcherTimer _hoverPopupPollTimer;
    private readonly DispatcherTimer _fullscreenPauseTimer;
    private readonly DispatcherTimer _autoHideTimer;
    private readonly List<NativeTaskbarButton> _buttons = [];
    private readonly List<NativeTrayButton> _trayButtons = [];
    private NativeTrayOverflowButton? _trayOverflowButton;
    private readonly Dictionary<string, NativeAppIcon> _iconCache = [];
    private readonly Dictionary<IntPtr, NativeOverlayIcon> _overlayCache = [];
    private readonly Dictionary<string, NativeTrayIconImage> _trayImageCache = [];
    private readonly Dictionary<string, NativeExplorerButtonImage> _explorerButtonImageCache = [];
    private readonly Dictionary<string, TrayIconItem> _trayItemsByProcessPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrayIconItem> _trayItemsByProcessName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _groupsWithExplorerButtonImage = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<TaskbarItem> _currentWindows = Array.Empty<TaskbarItem>();
    private IReadOnlyList<TaskbarItem> _visibleWindows = Array.Empty<TaskbarItem>();
    private IReadOnlyList<NativeTaskbarGroup> _visibleGroups = Array.Empty<NativeTaskbarGroup>();
    private IReadOnlyList<TrayIconItem> _trayItems = Array.Empty<TrayIconItem>();
    private IReadOnlyList<TrayIconItem> _inlineTrayItems = Array.Empty<TrayIconItem>();
    private IReadOnlyList<TrayIconItem> _overflowTrayItems = Array.Empty<TrayIconItem>();
    private string _clockText = "";
    private string _windowVisualSignature = "";
    private string _trayVisualSignature = "";
    private AppBarHost? _appBar;
    private ID2D1Factory? _factory;
    private ID2D1HwndRenderTarget? _renderTarget;
    private ID2D1SolidColorBrush? _activeBrush;
    private ID2D1SolidColorBrush? _hoverBrush;
    private ID2D1SolidColorBrush? _activeAccentBrush;
    private ID2D1SolidColorBrush? _inactiveAccentBrush;
    private ID2D1SolidColorBrush? _progressBrush;
    private ID2D1SolidColorBrush? _indeterminateProgressBrush;
    private ID2D1SolidColorBrush? _errorProgressBrush;
    private ID2D1SolidColorBrush? _pausedProgressBrush;
    private ID2D1SolidColorBrush? _clockTextBrush;
    private ID2D1SolidColorBrush? _pauseIndicatorBrush;
    private ID2D1SolidColorBrush? _topBorderBrush;
    private IDWriteFactory? _directWriteFactory;
    private IDWriteTextFormat? _clockTextFormat;
    private IntPtr _hwnd;
    private uint _shellHookMessage;
    private IntPtr _hoverHwnd;
    private string _hoverGroupKey = "";
    private string _hoverTrayKey = "";
    private NativeTaskbarButton? _pendingFlyoutButton;
    private TaskbarGroupFlyout? _groupFlyout;
    private TrayOverflowFlyout? _trayOverflowFlyout;
    private TaskbarHoverLabel? _hoverLabel;
    private int _width;
    private int _height;
    private int _clockTextFormatSize;
    private string _suppressNextTrayLeftButtonUpKey = "";
    private bool _renderQueued;
    private bool _nonClockUpdatesPaused;
    private bool _yieldingTopmostForFullscreen;
    private bool _autoHideEnabled;
    private bool _autoHideVisible = true;
    private DateTime _autoHideLastKeepVisibleUtc = DateTime.UtcNow;
    private DateTime? _hoverPopupLeaveStartedUtc;
    private bool _systemUsesLightTheme;
    private bool _disposed;

    public NativeSecondaryTaskbarWindow(Screen screen, WindowTracker windowTracker)
    {
        _screen = screen;
        _windowTracker = windowTracker;
        _currentWindows = windowTracker.CurrentWindows;
        _systemUsesLightTheme = ReadSystemUsesLightTheme();
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _trayRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(AppSettingsService.Current.TrayRefreshIntervalMs)
        };
        _trayRefreshTimer.Tick += (_, _) => RefreshTray();
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _renderTimer.Tick += (_, _) => FlushQueuedRender();
        _groupFlyoutTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(AppSettingsService.Current.TaskbarThumbnailHoverDelayMs)
        };
        _groupFlyoutTimer.Tick += (_, _) => ShowPendingGroupFlyout();
        _hoverPopupPollTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = HoverPopupPollInterval
        };
        _hoverPopupPollTimer.Tick += (_, _) => PollHoverPopupTarget();
        _fullscreenPauseTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = ActiveFullscreenPollInterval
        };
        _fullscreenPauseTimer.Tick += (_, _) => UpdateFullscreenPauseState();
        _autoHideTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = AutoHidePollInterval
        };
        _autoHideTimer.Tick += (_, _) => UpdateAutoHideState();
        BuildContextMenu();
        _appContextMenu.ShowItemToolTips = true;
        ApplyWindowList(windowTracker.CurrentWindows, force: true);
        RefreshTray(force: true);
        UpdateClock();
        _windowTracker.WindowsChanged += OnWindowsChanged;
        TaskbarStateSnapshotStore.StateChangedDetailed += OnTaskbarStateChanged;
        TrayIconSnapshotStore.SnapshotsChanged += OnTrayIconSnapshotsChanged;
        ExplorerTaskbarSnapshotStore.SnapshotChanged += OnExplorerTaskbarSnapshotChanged;
        AppSettingsService.SettingsChanged += OnSettingsChanged;
    }

    public void Show()
    {
        if (_hwnd != IntPtr.Zero || _disposed)
        {
            return;
        }

        EnsureWindowClassRegistered();

        var bounds = _screen.Bounds;
        _width = Math.Max(1, bounds.Width);
        _height = GetTaskbarHeightPixels();
        var instance = NativeMethods.GetModuleHandle(null);
        var exStyle = unchecked((int)(
            NativeMethods.WS_EX_TOOLWINDOW |
            NativeMethods.WS_EX_TOPMOST |
            NativeMethods.WS_EX_NOACTIVATE));

        _hwnd = NativeMethods.CreateWindowEx(
            exStyle,
            WindowClassName,
            "TaskBar2 Native",
            NativeMethods.WS_POPUP,
            bounds.Left,
            bounds.Bottom - _height,
            _width,
            _height,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create native taskbar window.");
        }

        _shellHookMessage = NativeMethods.RegisterWindowMessage("SHELLHOOK");
        if (!NativeMethods.RegisterShellHookWindow(_hwnd))
        {
            DebugLogger.WriteIfChanged(
                $"shell-hook-register-{_screen.DeviceName}",
                $"Shell hook registration failed for native taskbar: Hwnd=0x{_hwnd.ToInt64():X} LastError={Marshal.GetLastWin32Error()}");
        }

        Windows[_hwnd] = this;
        ApplyTaskbarBackdrop();
        var autoHideEnabled = IsAutoHideEnabled();
        _appBar = new AppBarHost(
            _hwnd,
            _screen,
            autoHideEnabled ? GetAutoHideTriggerThicknessPixels() : _height);
        _appBar.Register(positionWindow: !autoHideEnabled);
        ApplyTaskbarPlacement();
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
        NativeMethods.UpdateWindow(_hwnd);
        _clockTimer.Start();
        _trayRefreshTimer.Start();
        _fullscreenPauseTimer.Start();
        UpdateFullscreenPauseState();
        QueueRender(allowWhilePaused: true);

        DebugLogger.Write($"Native Direct2D taskbar created: Hwnd=0x{_hwnd.ToInt64():X} Screen={_screen.DeviceName} Bounds={bounds}");
    }

    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            return;
        }

        Cleanup();
    }

    public void RefreshPlacement()
    {
        _appBar?.RepositionWithoutChangingReservation(positionWindow: !IsAutoHideEnabled());
        ApplyTaskbarPlacement();
        QueueRender();
    }

    public void Dispose()
    {
        Close();
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        return Windows.TryGetValue(hwnd, out var window)
            ? window.WndProc(message, wParam, lParam)
            : NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private IntPtr WndProc(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (_shellHookMessage != 0 && message == _shellHookMessage)
        {
            OnShellHookMessage(wParam.ToInt32(), lParam);
            return IntPtr.Zero;
        }

        switch (message)
        {
            case NativeMethods.WM_ERASEBKGND:
                return new IntPtr(1);
            case NativeMethods.WM_SETTINGCHANGE:
            case NativeMethods.WM_THEMECHANGED:
            case NativeMethods.WM_DWMCOLORIZATIONCOLORCHANGED:
                OnSystemThemeChanged();
                return NativeMethods.DefWindowProc(_hwnd, message, wParam, lParam);
            case NativeMethods.WM_SIZE:
                OnSize(LoWord(lParam), HiWord(lParam));
                return IntPtr.Zero;
            case NativeMethods.WM_PAINT:
                Paint();
                return IntPtr.Zero;
            case NativeMethods.WM_MOUSEMOVE:
                OnMouseMove(LoWord(lParam), HiWord(lParam));
                return IntPtr.Zero;
            case NativeMethods.WM_LBUTTONUP:
                OnLeftButtonUp(LoWord(lParam), HiWord(lParam));
                return IntPtr.Zero;
            case NativeMethods.WM_LBUTTONDBLCLK:
                OnLeftButtonDoubleClick(LoWord(lParam), HiWord(lParam));
                return IntPtr.Zero;
            case NativeMethods.WM_RBUTTONUP:
                OnRightButtonUp(LoWord(lParam), HiWord(lParam));
                return IntPtr.Zero;
            case NativeMethods.WM_CLOSE:
                Close();
                return IntPtr.Zero;
            case NativeMethods.WM_DESTROY:
                OnDestroyed();
                return IntPtr.Zero;
            default:
                return NativeMethods.DefWindowProc(_hwnd, message, wParam, lParam);
        }
    }

    private static void EnsureWindowClassRegistered()
    {
        if (_windowClassRegistered)
        {
            return;
        }

        var instance = NativeMethods.GetModuleHandle(null);
        var wndClass = new NativeMethods.WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
            style = NativeMethods.CS_HREDRAW | NativeMethods.CS_VREDRAW | NativeMethods.CS_DBLCLKS,
            lpfnWndProc = WindowProcedure,
            hInstance = instance,
            hCursor = NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr(NativeMethods.IDC_ARROW)),
            lpszClassName = WindowClassName
        };

        if (NativeMethods.RegisterClassEx(ref wndClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to register native taskbar window class.");
        }

        _windowClassRegistered = true;
    }

    private void BuildContextMenu()
    {
        _contextMenu.Items.Add("Settings", null, (_, _) => AppCommands.ShowSettings());
        _contextMenu.Items.Add("Refresh", null, (_, _) => AppCommands.Refresh());
        if (DebugLogger.IsEnabled)
        {
            _contextMenu.Items.Add("Open log", null, (_, _) => AppCommands.OpenLog());
        }

        _contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _contextMenu.Items.Add("Exit", null, (_, _) => AppCommands.Exit());
    }

    private void OnShellHookMessage(int shellCode, IntPtr hwnd)
    {
        switch (shellCode)
        {
            case NativeMethods.HSHELL_FULLSCREENENTER:
                FullscreenApplicationDetector.NotifyShellFullscreenChanged(hwnd, entered: true);
                UpdateFullscreenPauseState();
                break;
            case NativeMethods.HSHELL_FULLSCREENEXIT:
                FullscreenApplicationDetector.NotifyShellFullscreenChanged(hwnd, entered: false);
                UpdateFullscreenPauseState();
                break;
        }
    }

    private bool UpdateFullscreenPauseState()
    {
        if (_disposed)
        {
            return false;
        }

        UpdateFullscreenZOrderState();

        var shouldPause = FullscreenApplicationDetector.IsFullscreenApplicationActive(out var fullscreenDescription);
        if (shouldPause == _nonClockUpdatesPaused)
        {
            return _nonClockUpdatesPaused;
        }

        _nonClockUpdatesPaused = shouldPause;
        _fullscreenPauseTimer.Interval = shouldPause
            ? PausedFullscreenPollInterval
            : ActiveFullscreenPollInterval;
        _windowTracker.SetNonClockUpdatesPaused(shouldPause);
        if (shouldPause)
        {
            _pendingFlyoutButton = null;
            _groupFlyoutTimer.Stop();
            _trayRefreshTimer.Stop();
            CloseGroupFlyout();
            DebugLogger.WriteIfChanged(
                $"native-fullscreen-pause-{_screen.DeviceName}",
                $"Native taskbar non-clock updates paused: Screen={_screen.DeviceName} {fullscreenDescription}");
            QueueRender(allowWhilePaused: true);
            return true;
        }

        DebugLogger.Write($"Native taskbar non-clock updates resumed: Screen={_screen.DeviceName}");
        if (!_trayRefreshTimer.IsEnabled)
        {
            _trayRefreshTimer.Start();
        }

        ApplyWindowList(_windowTracker.CurrentWindows, force: true);
        RefreshTray(force: true);
        QueueRender(allowWhilePaused: true);
        return false;
    }

    private void UpdateFullscreenZOrderState()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var shouldYieldTopmost = FullscreenApplicationDetector.ShouldTaskbarYieldToForegroundFullscreen(
            GetScreenRect(),
            out var fullscreenDescription);
        if (shouldYieldTopmost == _yieldingTopmostForFullscreen)
        {
            return;
        }

        _yieldingTopmostForFullscreen = shouldYieldTopmost;
        SetTaskbarFullscreenYield(shouldYieldTopmost);
        DebugLogger.WriteIfChanged(
            $"native-fullscreen-zorder-{_screen.DeviceName}",
            shouldYieldTopmost
                ? $"Native taskbar yielding topmost to foreground fullscreen: Screen={_screen.DeviceName} {fullscreenDescription}"
                : $"Native taskbar restored topmost after foreground fullscreen: Screen={_screen.DeviceName}");
    }

    private void SetTaskbarFullscreenYield(bool yieldToFullscreen)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!NativeMethods.SetWindowPos(
                _hwnd,
                yieldToFullscreen ? NativeMethods.HWND_BOTTOM : NativeMethods.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE |
                NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOOWNERZORDER))
        {
            DebugLogger.WriteIfChanged(
                $"native-topmost-failed-{_screen.DeviceName}",
                $"Native taskbar fullscreen yield update failed: Screen={_screen.DeviceName} Yield={yieldToFullscreen} LastError={Marshal.GetLastWin32Error()}");
        }
    }

    private NativeMethods.Rect GetScreenRect()
    {
        var bounds = _screen.Bounds;
        return new NativeMethods.Rect
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Right = bounds.Right,
            Bottom = bounds.Bottom
        };
    }

    private void UpdateAutoHideState()
    {
        if (_disposed || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var autoHideEnabled = IsAutoHideEnabled();
        if (autoHideEnabled != _autoHideEnabled)
        {
            ApplyTaskbarPlacement(forceAutoHideStateRefresh: true);
        }

        if (!_autoHideEnabled || !NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        if (_autoHideVisible)
        {
            if (ShouldKeepAutoHideVisible(cursor))
            {
                _autoHideLastKeepVisibleUtc = DateTime.UtcNow;
                return;
            }

            if (DateTime.UtcNow - _autoHideLastKeepVisibleUtc >= AutoHideHideDelay)
            {
                SetAutoHideVisible(false);
            }

            return;
        }

        if (IsCursorInAutoHideRevealZone(cursor))
        {
            SetAutoHideVisible(true);
        }
    }

    private void ApplyTaskbarPlacement(bool forceAutoHideStateRefresh = false)
    {
        if (_hwnd == IntPtr.Zero || _appBar is null)
        {
            return;
        }

        var autoHideEnabled = IsAutoHideEnabled();
        var autoHideChanged = autoHideEnabled != _autoHideEnabled;
        _autoHideEnabled = autoHideEnabled;
        if (!_autoHideEnabled)
        {
            _autoHideVisible = true;
            _autoHideTimer.Stop();
            _appBar.SetHeight(_height);
            PositionTaskbarWindow(_screen.Bounds.Bottom - _height);
            _autoHideLastKeepVisibleUtc = DateTime.UtcNow;
            return;
        }

        _appBar.SetReservedHeight(GetAutoHideTriggerThicknessPixels());
        if (autoHideChanged || forceAutoHideStateRefresh)
        {
            _autoHideVisible = IsCursorInTaskbarOrRevealZone();
            _autoHideLastKeepVisibleUtc = DateTime.UtcNow;
        }

        PositionAutoHideTaskbarWindow();
        if (!_autoHideTimer.IsEnabled)
        {
            _autoHideTimer.Start();
        }
    }

    private bool RevealAutoHiddenTaskbar()
    {
        if (!_autoHideEnabled || _autoHideVisible)
        {
            return false;
        }

        SetAutoHideVisible(true);
        return true;
    }

    private void SetAutoHideVisible(bool visible)
    {
        if (!_autoHideEnabled)
        {
            return;
        }

        if (_autoHideVisible == visible)
        {
            PositionAutoHideTaskbarWindow();
            return;
        }

        _autoHideVisible = visible;
        if (visible)
        {
            _autoHideLastKeepVisibleUtc = DateTime.UtcNow;
        }
        else
        {
            _hoverHwnd = IntPtr.Zero;
            _hoverGroupKey = "";
            _hoverTrayKey = "";
            _pendingFlyoutButton = null;
            _groupFlyoutTimer.Stop();
            CloseGroupFlyout();
        }

        PositionAutoHideTaskbarWindow();
        QueueRender(allowWhilePaused: true);
    }

    private void PositionAutoHideTaskbarWindow()
    {
        var bounds = _screen.Bounds;
        var top = _autoHideVisible
            ? bounds.Bottom - _height
            : bounds.Bottom - GetAutoHideTriggerThicknessPixels();
        PositionTaskbarWindow(top);
        DebugLogger.WriteIfChanged(
            $"native-autohide-position-{_screen.DeviceName}",
            $"Native taskbar autohide position: Screen={_screen.DeviceName} Visible={_autoHideVisible} Top={top} Height={_height}");
    }

    private void PositionTaskbarWindow(int top)
    {
        var bounds = _screen.Bounds;
        _width = Math.Max(1, bounds.Width);
        NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            bounds.Left,
            top,
            _width,
            _height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private bool IsCursorInTaskbarOrRevealZone()
    {
        return NativeMethods.GetCursorPos(out var cursor) &&
               (IsCursorInCurrentWindow(cursor) || IsCursorInAutoHideRevealZone(cursor));
    }

    private bool ShouldKeepAutoHideVisible(NativeMethods.Point cursor)
    {
        return IsCursorInCurrentWindow(cursor) ||
               _contextMenu.Visible ||
               _appContextMenu.Visible ||
               _groupFlyout is { Visible: true };
    }

    private bool IsCursorInCurrentWindow(NativeMethods.Point cursor)
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return false;
        }

        return cursor.X >= rect.Left &&
               cursor.X < rect.Right &&
               cursor.Y >= rect.Top &&
               cursor.Y < rect.Bottom;
    }

    private bool IsCursorInAutoHideRevealZone(NativeMethods.Point cursor)
    {
        var bounds = _screen.Bounds;
        var thickness = GetAutoHideTriggerThicknessPixels();
        return cursor.X >= bounds.Left &&
               cursor.X < bounds.Right &&
               cursor.Y >= bounds.Bottom - thickness &&
               cursor.Y < bounds.Bottom + thickness;
    }

    private void OnWindowsChanged(object? sender, IReadOnlyList<TaskbarItem> windows)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(new Action(() => OnWindowsChanged(sender, windows)), DispatcherPriority.Background);
            return;
        }

        if (_nonClockUpdatesPaused)
        {
            _currentWindows = windows;
            return;
        }

        ApplyWindowList(windows);
    }

    private void OnTaskbarStateChanged(object? sender, TaskbarStateChangedEventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(new Action(() => OnTaskbarStateChanged(sender, e)), DispatcherPriority.Background);
            return;
        }

        if (_nonClockUpdatesPaused)
        {
            return;
        }

        if (string.Equals(e.Operation, "SetOverlayIcon", StringComparison.OrdinalIgnoreCase))
        {
            InvalidateExplorerButtonImageForHwnd(e.Hwnd, e.Operation);
        }

        QueueRender();
    }

    private void OnTrayIconSnapshotsChanged(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(new Action(() => OnTrayIconSnapshotsChanged(sender, e)), DispatcherPriority.Background);
            return;
        }

        if (_nonClockUpdatesPaused)
        {
            return;
        }

        RefreshTray();
    }

    private void OnExplorerTaskbarSnapshotChanged(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(new Action(() => OnExplorerTaskbarSnapshotChanged(sender, e)), DispatcherPriority.Background);
            return;
        }

        if (_nonClockUpdatesPaused)
        {
            return;
        }

        ApplyWindowList(_currentWindows, force: true);
        QueueRender();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(new Action(() => OnSettingsChanged(sender, e)), DispatcherPriority.Background);
            return;
        }

        FullscreenApplicationDetector.Invalidate();
        _trayRefreshTimer.Interval = TimeSpan.FromMilliseconds(AppSettingsService.Current.TrayRefreshIntervalMs);
        _groupFlyoutTimer.Interval = TimeSpan.FromMilliseconds(AppSettingsService.Current.TaskbarThumbnailHoverDelayMs);
        if (!AppSettingsService.Current.ShowTaskbarThumbnailsOnHover)
        {
            _pendingFlyoutButton = null;
            _groupFlyoutTimer.Stop();
            CloseGroupFlyout();
        }

        var newHeight = GetTaskbarHeightPixels();
        if (newHeight != _height)
        {
            _height = newHeight;
        }

        ApplyTaskbarPlacement();

        ApplyTaskbarBackdrop();
        UpdateClock();
        if (UpdateFullscreenPauseState())
        {
            QueueRender(allowWhilePaused: true);
            return;
        }

        ApplyWindowList(_currentWindows, force: true);
        RefreshTray(force: true);
        QueueRender(allowWhilePaused: true);
    }

    private void OnSystemThemeChanged()
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(new Action(OnSystemThemeChanged), DispatcherPriority.Background);
            return;
        }

        var systemUsesLightTheme = ReadSystemUsesLightTheme();
        if (systemUsesLightTheme != _systemUsesLightTheme)
        {
            _systemUsesLightTheme = systemUsesLightTheme;
            DisposeRenderTargetResources();
        }

        ApplyTaskbarBackdrop();
        QueueRender(allowWhilePaused: true);
    }

    private void RefreshTray(bool force = false)
    {
        if (_disposed)
        {
            return;
        }

        if (!force && _nonClockUpdatesPaused)
        {
            return;
        }

        var taskbarSettings = GetMonitorTaskbarSettings();
        var latestItems = taskbarSettings.MirrorPrimaryNotificationArea
            ? TrayIconSnapshotStore.GetItems()
            : Array.Empty<TrayIconItem>();
        if (latestItems.Count == 0 &&
            taskbarSettings.MirrorPrimaryNotificationArea &&
            !AppSettingsService.Current.EnableInvasiveTrayIconHook)
        {
            latestItems = _trayIconProvider.GetIcons();
        }

        var previousSignature = _trayVisualSignature;
        var signature = BuildTrayVisualSignature(latestItems);
        _trayVisualSignature = signature;
        _trayItems = latestItems;
        PartitionTrayItems();
        RebuildTrayProcessLookups();
        RemoveUnusedTrayImages(_trayItems.Select(GetTrayKey).ToHashSet(StringComparer.Ordinal));
        UpdateTrayOverflowFlyoutItems();
        if (signature == previousSignature)
        {
            return;
        }

        QueueRender();
    }

    private void PartitionTrayItems()
    {
        if (!AppSettingsService.Current.ShowAllTrayIcons)
        {
            _inlineTrayItems = _trayItems;
            _overflowTrayItems = Array.Empty<TrayIconItem>();
            return;
        }

        _inlineTrayItems = _trayItems
            .Where(item => !item.IsOverflow)
            .ToArray();
        _overflowTrayItems = _trayItems
            .Where(item => item.IsOverflow)
            .ToArray();
    }

    private void RebuildTrayProcessLookups()
    {
        _trayItemsByProcessPath.Clear();
        _trayItemsByProcessName.Clear();
        foreach (var item in _trayItems)
        {
            if (item.Icon is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(item.SourceProcessPath))
            {
                _trayItemsByProcessPath.TryAdd(NormalizePath(item.SourceProcessPath), item);
            }

            if (!string.IsNullOrWhiteSpace(item.SourceProcessName))
            {
                _trayItemsByProcessName.TryAdd(Path.GetFileNameWithoutExtension(item.SourceProcessName), item);
            }
        }
    }

    private void UpdateClock()
    {
        if (!GetMonitorTaskbarSettings().ShowClock)
        {
            if (_clockText.Length > 0)
            {
                _clockText = "";
                QueueRender(allowWhilePaused: true);
            }

            ScheduleNextClockTick(DateTime.Now);
            return;
        }

        var now = DateTime.Now;
        var text = now.ToString("HH:mm");
        if (!string.Equals(_clockText, text, StringComparison.Ordinal))
        {
            _clockText = text;
            QueueRender(allowWhilePaused: true);
        }

        ScheduleNextClockTick(now);
    }

    private void ScheduleNextClockTick(DateTime now)
    {
        var nextMinute = new DateTime(
            now.Year,
            now.Month,
            now.Day,
            now.Hour,
            now.Minute,
            0,
            now.Kind).AddMinutes(1);
        var delay = nextMinute - now + TimeSpan.FromMilliseconds(50);
        _clockTimer.Interval = delay < TimeSpan.FromMilliseconds(250)
            ? TimeSpan.FromMilliseconds(250)
            : delay;
    }

    private void ApplyWindowList(IReadOnlyList<TaskbarItem> windows, bool force = false)
    {
        if (_disposed)
        {
            return;
        }

        _currentWindows = windows;
        if (!force && _nonClockUpdatesPaused)
        {
            return;
        }

        var taskbarSettings = GetMonitorTaskbarSettings();
        var showOnlyThisMonitor = taskbarSettings.ShowOnlyAppsOnThisMonitor;
        var visibleWindowItems = showOnlyThisMonitor
            ? windows.Where(item => item.MonitorDeviceName == _screen.DeviceName).ToArray()
            : windows.ToArray();
        _visibleWindows = ExplorerTaskbarSnapshotStore.MergePinnedItems(
            visibleWindowItems,
            includePinnedOnly: !showOnlyThisMonitor);
        _visibleGroups = BuildWindowGroups(_visibleWindows);

        var visibleHandles = _visibleWindows
            .Where(item => item.Hwnd != IntPtr.Zero)
            .Select(item => item.Hwnd)
            .ToHashSet();
        var visibleIconKeys = _visibleWindows
            .Select(GetIconCacheKey)
            .ToHashSet();
        var visibleGroupKeys = _visibleGroups.Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveUnusedIcons(visibleHandles, visibleIconKeys);
        RemoveUnusedExplorerButtonImages(visibleGroupKeys);
        var signature = BuildWindowVisualSignature(_visibleGroups);
        if (signature == _windowVisualSignature)
        {
            return;
        }

        _windowVisualSignature = signature;
        QueueRender();
    }

    private void QueueRender(bool allowWhilePaused = false)
    {
        if (_disposed || _hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(new Action(() => QueueRender(allowWhilePaused)), DispatcherPriority.Background);
            return;
        }

        if (!allowWhilePaused &&
            _nonClockUpdatesPaused &&
            AppSettingsService.Current.PauseNonClockUpdatesWhileFullscreen)
        {
            return;
        }

        if (_renderQueued)
        {
            return;
        }

        _renderQueued = true;
        if (!_renderTimer.IsEnabled)
        {
            _renderTimer.Start();
        }
    }

    private void FlushQueuedRender()
    {
        _renderTimer.Stop();
        if (_disposed || _hwnd == IntPtr.Zero)
        {
            _renderQueued = false;
            return;
        }

        _renderQueued = false;
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private void OnSize(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _renderTarget?.Resize(new SizeI(_width, _height));
        QueueRender();
    }

    private void OnMouseMove(int x, int y)
    {
        if (RevealAutoHiddenTaskbar())
        {
            return;
        }

        var hoveredTray = HitTestTrayOverflow(x, y)
            ? TrayOverflowButtonKey
            : HitTestTray(x, y)?.Key ?? "";
        var hoveredButton = string.IsNullOrEmpty(hoveredTray)
            ? HitTestTaskbarButton(x, y)
            : null;
        var hovered = hoveredButton?.Group.Representative.Hwnd ?? IntPtr.Zero;
        var hoveredGroupKey = hoveredButton?.Group.Key ?? "";
        if (hovered == _hoverHwnd && hoveredTray == _hoverTrayKey && hoveredGroupKey == _hoverGroupKey)
        {
            return;
        }

        _hoverHwnd = hovered;
        _hoverGroupKey = hoveredGroupKey;
        _hoverTrayKey = hoveredTray;
        ScheduleGroupFlyout(hoveredButton);
        QueueRender();
    }

    private void OnLeftButtonUp(int x, int y)
    {
        if (RevealAutoHiddenTaskbar())
        {
            return;
        }

        if (HitTestTrayOverflow(x, y))
        {
            ToggleTrayOverflowFlyout();
            return;
        }

        var trayButton = HitTestTray(x, y);
        if (trayButton is not null)
        {
            if (string.Equals(_suppressNextTrayLeftButtonUpKey, trayButton.Key, StringComparison.Ordinal))
            {
                _suppressNextTrayLeftButtonUpKey = "";
                return;
            }

            ForwardTrayClick(trayButton.Item, rightClick: false);
            return;
        }

        _suppressNextTrayLeftButtonUpKey = "";

        var button = HitTestTaskbarButton(x, y);
        if (button is null)
        {
            CloseTrayOverflowFlyout();
            return;
        }

        CloseTrayOverflowFlyout();
        CloseGroupFlyout();
        if (button.Group.HasMultiple)
        {
            ShowGroupFlyout(button);
            return;
        }

        if (button.Group.Representative.Hwnd != IntPtr.Zero)
        {
            WindowActions.ActivateOrMinimize(button.Group.Representative.Hwnd);
            _windowTracker.Refresh();
            return;
        }

        OpenPinnedGroup(button.Group);
    }

    private void OnRightButtonUp(int x, int y)
    {
        if (RevealAutoHiddenTaskbar())
        {
            return;
        }

        if (HitTestTrayOverflow(x, y))
        {
            return;
        }

        var trayButton = HitTestTray(x, y);
        if (trayButton is not null)
        {
            ForwardTrayClick(trayButton.Item, rightClick: true);
            return;
        }

        var button = HitTestTaskbarButton(x, y);
        if (button is not null)
        {
            CloseTrayOverflowFlyout();
            CloseGroupFlyout();
            ShowAppContextMenu(button.Group);
            return;
        }

        CloseTrayOverflowFlyout();
        CloseGroupFlyout();
        _contextMenu.Show(System.Windows.Forms.Control.MousePosition);
    }

    private void OnLeftButtonDoubleClick(int x, int y)
    {
        if (RevealAutoHiddenTaskbar())
        {
            return;
        }

        if (HitTestTrayOverflow(x, y))
        {
            return;
        }

        var trayButton = HitTestTray(x, y);
        if (trayButton is null)
        {
            return;
        }

        _suppressNextTrayLeftButtonUpKey = trayButton.Key;
        ForwardTrayDoubleClick(trayButton.Item);
    }

    private static bool TryForwardExplorerTaskbarClick(NativeTaskbarButton button, bool rightClick, int anchorX, int anchorY) =>
        ExplorerTaskbarSnapshotStore.TryForwardClick(button.Group.Items, rightClick, anchorX, anchorY);

    private void ForwardTrayClick(TrayIconItem item, bool rightClick)
    {
        if (TrayIconSnapshotStore.TryForwardClick(item, rightClick))
        {
            return;
        }

        _trayIconProvider.ForwardClick(item, rightClick);
    }

    private void ForwardTrayDoubleClick(TrayIconItem item)
    {
        if (TrayIconSnapshotStore.TryForwardClick(item, rightClick: false, doubleClick: true))
        {
            return;
        }

        _trayIconProvider.ForwardClick(item, rightClick: false, doubleClick: true);
    }

    private void ToggleTrayOverflowFlyout()
    {
        if (IsTrayOverflowFlyoutVisible())
        {
            CloseTrayOverflowFlyout();
            QueueRender();
            return;
        }

        ShowTrayOverflowFlyout();
    }

    private void ShowTrayOverflowFlyout()
    {
        var trayLayout = GetTrayLayout();
        var items = GetTrayOverflowItems(trayLayout);
        if (items.Count == 0 || _trayOverflowButton is null)
        {
            CloseTrayOverflowFlyout();
            return;
        }

        CloseGroupFlyout();
        CloseTrayOverflowFlyout();

        var flyout = new TrayOverflowFlyout(
            items,
            GetEffectiveTaskbarScale(),
            _systemUsesLightTheme,
            (item, rightClick) => ForwardTrayClick(item, rightClick),
            ForwardTrayDoubleClick);
        _trayOverflowFlyout = flyout;
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trayOverflowFlyout, flyout))
            {
                _trayOverflowFlyout = null;
                QueueRender();
            }

            flyout.Dispose();
        };
        flyout.ShowNear(GetScreenBounds(_trayOverflowButton.Bounds), _screen.WorkingArea);
        QueueRender();
    }

    private void UpdateTrayOverflowFlyoutItems()
    {
        if (!IsTrayOverflowFlyoutVisible() || _trayOverflowFlyout is null)
        {
            return;
        }

        var items = GetTrayOverflowItems(GetTrayLayout());
        if (items.Count == 0)
        {
            CloseTrayOverflowFlyout();
            return;
        }

        _trayOverflowFlyout.UpdateItems(items);
    }

    private void CloseTrayOverflowFlyout()
    {
        var flyout = _trayOverflowFlyout;
        _trayOverflowFlyout = null;
        if (flyout is not null)
        {
            flyout.Close();
        }
    }

    private bool IsTrayOverflowFlyoutVisible() =>
        _trayOverflowFlyout?.Visible == true;

    private IReadOnlyList<TrayIconItem> GetTrayOverflowItems(NativeTrayLayout trayLayout)
    {
        if (!trayLayout.HasOverflowButton)
        {
            return Array.Empty<TrayIconItem>();
        }

        var items = new List<TrayIconItem>(_overflowTrayItems.Count + Math.Max(0, _inlineTrayItems.Count - trayLayout.VisibleIconCount));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _overflowTrayItems.Concat(_inlineTrayItems.Skip(trayLayout.VisibleIconCount)))
        {
            if (seen.Add(GetTrayKey(item)))
            {
                items.Add(item);
            }
        }

        return items;
    }

    private Rectangle GetScreenBounds(Rectangle clientBounds)
    {
        if (_hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(_hwnd, out var windowRect))
        {
            return new Rectangle(
                windowRect.Left + clientBounds.Left,
                windowRect.Top + clientBounds.Top,
                clientBounds.Width,
                clientBounds.Height);
        }

        return new Rectangle(
            _screen.Bounds.Left + clientBounds.Left,
            _screen.Bounds.Bottom - _height + clientBounds.Top,
            clientBounds.Width,
            clientBounds.Height);
    }

    private void ScheduleGroupFlyout(NativeTaskbarButton? button)
    {
        if (!AppSettingsService.Current.ShowTaskbarThumbnailsOnHover)
        {
            _pendingFlyoutButton = null;
            _groupFlyoutTimer.Stop();
            CloseGroupFlyout();

            return;
        }

        if (button is null)
        {
            _pendingFlyoutButton = null;
            _groupFlyoutTimer.Stop();
            if (IsHoverPopupVisible())
            {
                EnsureHoverPopupPollRunning();
            }
            else
            {
                StopHoverPopupPollIfIdle();
            }

            return;
        }

        if (IsHoverPopupVisibleForDifferentGroup(button.Group.Key))
        {
            CloseGroupFlyout();
        }

        _pendingFlyoutButton = button;
        ResetHoverPopupCloseDelay();
        _groupFlyoutTimer.Stop();
        _groupFlyoutTimer.Start();
        EnsureHoverPopupPollRunning();
    }

    private void ShowPendingGroupFlyout()
    {
        _groupFlyoutTimer.Stop();
        if (!AppSettingsService.Current.ShowTaskbarThumbnailsOnHover ||
            _pendingFlyoutButton is not { } button)
        {
            return;
        }

        ShowHoverPopup(button);
    }

    private void ShowHoverPopup(NativeTaskbarButton button)
    {
        if (button.Group.HasRunning)
        {
            ShowGroupFlyout(button);
            return;
        }

        ShowGroupLabel(button);
    }

    private void ShowGroupLabel(NativeTaskbarButton button)
    {
        if (_disposed || button.Group.HasRunning)
        {
            return;
        }

        ResetHoverPopupCloseDelay();
        if (_hoverLabel is { Visible: true } && _hoverLabel.GroupKey == button.Group.Key)
        {
            return;
        }

        CloseGroupFlyout();
        var screenTop = _screen.Bounds.Bottom - _height;
        var screenBounds = new Rectangle(
            _screen.Bounds.Left + button.Bounds.Left,
            screenTop + button.Bounds.Top,
            button.Bounds.Width,
            button.Bounds.Height);
        var label = new TaskbarHoverLabel(
            button.Group.Key,
            GetMenuTitle(button.Group.Representative),
            _systemUsesLightTheme);
        _hoverLabel = label;
        label.Closed += (_, _) => OnHoverLabelClosed(label);
        label.ShowNear(screenBounds, _screen.WorkingArea);
        _pendingFlyoutButton = null;
        EnsureHoverPopupPollRunning();
    }

    private void ShowGroupFlyout(NativeTaskbarButton button)
    {
        if (_disposed || !button.Group.HasRunning)
        {
            return;
        }

        ResetHoverPopupCloseDelay();
        var runningItems = button.Group.Items
            .Where(item => item.Hwnd != IntPtr.Zero)
            .ToArray();
        if (runningItems.Length == 0)
        {
            return;
        }

        if (_groupFlyout is { Visible: true } && _groupFlyout.GroupKey == button.Group.Key)
        {
            return;
        }

        CloseGroupFlyout();
        var screenTop = _screen.Bounds.Bottom - _height;
        var screenBounds = new Rectangle(
            _screen.Bounds.Left + button.Bounds.Left,
            screenTop + button.Bounds.Top,
            button.Bounds.Width,
            button.Bounds.Height);
        var flyout = new TaskbarGroupFlyout(runningItems, item =>
        {
            OnHoverPreviewActivated();
            WindowActions.Activate(item.Hwnd);
            _windowTracker.Refresh();
        }, item =>
        {
            OnHoverPreviewActivated();
            WindowActions.Close(item.Hwnd);
            _windowTracker.Refresh();
            ScheduleWindowTrackerRefresh(ClosedPreviewRefreshDelay);
        });
        _groupFlyout = flyout;
        flyout.Closed += (_, _) => OnGroupFlyoutClosed(flyout);
        flyout.ShowNear(screenBounds, _screen.WorkingArea);
        _pendingFlyoutButton = null;
        EnsureHoverPopupPollRunning();
    }

    private void OnHoverPreviewActivated()
    {
        _pendingFlyoutButton = null;
        _groupFlyoutTimer.Stop();
        ResetHoverPopupCloseDelay();
        ClearHoverState();
    }

    private void ScheduleWindowTrackerRefresh(TimeSpan delay)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = delay
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_disposed)
            {
                _windowTracker.Refresh();
            }
        };
        timer.Start();
    }

    private void OnGroupFlyoutClosed(TaskbarGroupFlyout flyout)
    {
        if (ReferenceEquals(_groupFlyout, flyout))
        {
            _groupFlyout = null;
            _pendingFlyoutButton = null;
            ResetHoverPopupCloseDelay();
            ClearHoverState();
        }

        StopHoverPopupPollIfIdle();
    }

    private void OnHoverLabelClosed(TaskbarHoverLabel label)
    {
        if (ReferenceEquals(_hoverLabel, label))
        {
            _hoverLabel = null;
            _pendingFlyoutButton = null;
            ResetHoverPopupCloseDelay();
            ClearHoverState();
        }

        StopHoverPopupPollIfIdle();
    }

    private void CloseGroupFlyout()
    {
        ResetHoverPopupCloseDelay();
        _pendingFlyoutButton = null;
        _groupFlyoutTimer.Stop();

        var flyout = _groupFlyout;
        _groupFlyout = null;
        if (flyout is not null)
        {
            flyout.Close();
            flyout.Dispose();
        }

        var label = _hoverLabel;
        _hoverLabel = null;
        if (label is not null)
        {
            label.Close();
            label.Dispose();
        }

        StopHoverPopupPollIfIdle();
    }

    private void PollHoverPopupTarget()
    {
        if (_disposed || !AppSettingsService.Current.ShowTaskbarThumbnailsOnHover)
        {
            ClearHoverState();
            CloseGroupFlyout();
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            ClearHoverState();
            CloseGroupFlyout();
            return;
        }

        var hoveredButton = HitTestTaskbarButtonFromScreen(cursor, out var cursorInTaskbar);
        if (hoveredButton is null)
        {
            if (IsCursorInHoverPopup(cursor))
            {
                ResetHoverPopupCloseDelay();
                return;
            }

            if (ShouldCloseHoverPopupAfterLeave())
            {
                ClearHoverState();
                CloseGroupFlyout();
                QueueRender();
            }

            return;
        }

        var hoveredGroupKey = hoveredButton.Group.Key;
        var activeGroupKey = GetActiveHoverPopupGroupKey() ?? _pendingFlyoutButton?.Group.Key ?? "";
        if (string.Equals(activeGroupKey, hoveredGroupKey, StringComparison.Ordinal))
        {
            ResetHoverPopupCloseDelay();
            return;
        }

        ResetHoverPopupCloseDelay();
        _hoverHwnd = hoveredButton.Group.Representative.Hwnd;
        _hoverGroupKey = hoveredGroupKey;
        _hoverTrayKey = "";

        var hadVisiblePopup = IsHoverPopupVisible();
        CloseGroupFlyout();
        QueueRender();

        if (hadVisiblePopup)
        {
            ShowHoverPopup(hoveredButton);
            return;
        }

        ScheduleGroupFlyout(hoveredButton);
    }

    private NativeTaskbarButton? HitTestTaskbarButtonFromScreen(NativeMethods.Point cursor, out bool cursorInTaskbar)
    {
        cursorInTaskbar = false;
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_hwnd, out var windowRect))
        {
            return null;
        }

        if (cursor.X < windowRect.Left ||
            cursor.X >= windowRect.Right ||
            cursor.Y < windowRect.Top ||
            cursor.Y >= windowRect.Bottom)
        {
            return null;
        }

        cursorInTaskbar = true;
        var localX = cursor.X - windowRect.Left;
        var localY = cursor.Y - windowRect.Top;
        return HitTestTray(localX, localY) is null
            ? HitTestTaskbarButton(localX, localY)
            : null;
    }

    private bool IsCursorInHoverPopup(NativeMethods.Point cursor)
    {
        var point = new Point(cursor.X, cursor.Y);
        return (_groupFlyout is { Visible: true } && _groupFlyout.Bounds.Contains(point)) ||
               (_hoverLabel is { Visible: true } && _hoverLabel.Bounds.Contains(point));
    }

    private bool ShouldCloseHoverPopupAfterLeave()
    {
        var now = DateTime.UtcNow;
        if (_hoverPopupLeaveStartedUtc is null)
        {
            _hoverPopupLeaveStartedUtc = now;
            return false;
        }

        return now - _hoverPopupLeaveStartedUtc.Value >= HoverPopupCloseDelay;
    }

    private void ResetHoverPopupCloseDelay()
    {
        _hoverPopupLeaveStartedUtc = null;
    }

    private bool IsHoverPopupVisible() =>
        _groupFlyout is { Visible: true } ||
        _hoverLabel is { Visible: true };

    private bool IsHoverPopupVisibleForDifferentGroup(string groupKey)
    {
        var activeGroupKey = GetActiveHoverPopupGroupKey();
        return !string.IsNullOrEmpty(activeGroupKey) &&
               !string.Equals(activeGroupKey, groupKey, StringComparison.Ordinal);
    }

    private string? GetActiveHoverPopupGroupKey()
    {
        if (_groupFlyout is { Visible: true })
        {
            return _groupFlyout.GroupKey;
        }

        if (_hoverLabel is { Visible: true })
        {
            return _hoverLabel.GroupKey;
        }

        return null;
    }

    private void EnsureHoverPopupPollRunning()
    {
        if (!_hoverPopupPollTimer.IsEnabled)
        {
            _hoverPopupPollTimer.Start();
        }
    }

    private void StopHoverPopupPollIfIdle()
    {
        if (_pendingFlyoutButton is null &&
            _groupFlyout is not { Visible: true } &&
            _hoverLabel is not { Visible: true })
        {
            _hoverPopupPollTimer.Stop();
        }
    }

    private void ClearHoverState()
    {
        _hoverHwnd = IntPtr.Zero;
        _hoverGroupKey = "";
        _hoverTrayKey = "";
    }

    private void ShowAppContextMenu(NativeTaskbarGroup group)
    {
        if (_appContextMenu.Visible)
        {
            _appContextMenu.Close();
        }

        _appContextMenu.Items.Clear();
        var addedTaskbarItems = AddTaskbarMenuItems(group);
        if (addedTaskbarItems)
        {
            _appContextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        }

        var addedJumpListItems = AddJumpListMenuItems(group);
        if (addedJumpListItems)
        {
            _appContextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        }

        if (group.HasMultiple)
        {
            foreach (var item in group.Items)
            {
                var window = item;
                _appContextMenu.Items.Add(GetMenuTitle(window), null, (_, _) =>
                {
                    WindowActions.Activate(window.Hwnd);
                    _windowTracker.Refresh();
                });
            }

            _appContextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            _appContextMenu.Items.Add("Close all windows", null, (_, _) =>
            {
                foreach (var item in group.Items)
                {
                    WindowActions.Close(item.Hwnd);
                }
            });
        }
        else if (!group.HasRunning)
        {
            _appContextMenu.Items.Add("Open", null, (_, _) => OpenPinnedGroup(group));
        }
        else
        {
            var item = group.Representative;
            _appContextMenu.Items.Add("Open", null, (_, _) =>
            {
                WindowActions.Activate(item.Hwnd);
                _windowTracker.Refresh();
            });

            if (NativeMethods.IsIconic(item.Hwnd))
            {
                _appContextMenu.Items.Add("Restore", null, (_, _) =>
                {
                    WindowActions.Activate(item.Hwnd);
                    _windowTracker.Refresh();
                });
            }
            else
            {
                _appContextMenu.Items.Add("Minimize", null, (_, _) => WindowActions.Minimize(item.Hwnd));
            }

            _appContextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            _appContextMenu.Items.Add("Close window", null, (_, _) => WindowActions.Close(item.Hwnd));
        }

        _appContextMenu.Show(System.Windows.Forms.Control.MousePosition);
    }

    private bool AddTaskbarMenuItems(NativeTaskbarGroup group)
    {
        var added = false;
        if (group.HasRunning)
        {
            var text = group.HasMultiple ? "Move all windows to this monitor" : "Move to this monitor";
            _appContextMenu.Items.Add(text, null, (_, _) => MoveGroupToThisMonitor(group));
            added = true;
        }

        return added;
    }

    private void MoveGroupToThisMonitor(NativeTaskbarGroup group)
    {
        var targetWorkArea = GetMoveTargetWorkArea();
        var moved = 0;
        foreach (var item in group.Items.Where(item => item.Hwnd != IntPtr.Zero))
        {
            if (WindowActions.MoveToMonitor(item.Hwnd, targetWorkArea))
            {
                moved++;
            }
        }

        if (group.Representative.Hwnd != IntPtr.Zero)
        {
            WindowActions.Activate(group.Representative.Hwnd);
        }

        _windowTracker.Refresh();
        DebugLogger.WriteIfChanged(
            $"taskbar-move-group-{group.Key}",
            $"Taskbar app group moved to monitor: Group={group.Key} Screen={_screen.DeviceName} Target={targetWorkArea} Moved={moved}");
    }

    private Rectangle GetMoveTargetWorkArea()
    {
        var area = _screen.WorkingArea;
        var taskbarTop = _screen.Bounds.Bottom - _height;
        if (area.Bottom > taskbarTop)
        {
            area.Height = Math.Max(1, taskbarTop - area.Top);
        }

        return area;
    }

    private void OpenPinnedGroup(NativeTaskbarGroup group)
    {
        if (LaunchTaskbarItem(group.Representative))
        {
            _windowTracker.Refresh();
            return;
        }

        var cursor = System.Windows.Forms.Control.MousePosition;
        if (ExplorerTaskbarSnapshotStore.TryForwardClick(group.Items, rightClick: false, cursor.X, cursor.Y))
        {
            _windowTracker.Refresh();
            return;
        }

        _windowTracker.Refresh();
    }

    private static bool LaunchTaskbarItem(TaskbarItem item)
    {
        var launchPath = string.IsNullOrWhiteSpace(item.LaunchPath)
            ? item.ProcessPath
            : item.LaunchPath;
        if (string.IsNullOrWhiteSpace(launchPath) || !File.Exists(launchPath))
        {
            DebugLogger.WriteIfChanged(
                $"pinned-launch-missing-path-{item.GroupKey}",
                $"Pinned taskbar launch skipped because no launch path is known: Group={item.GroupKey} Title={item.Title} AppId={item.AppUserModelId} ProcessPath={item.ProcessPath} LaunchPath={item.LaunchPath}");
            return false;
        }

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo(launchPath)
            {
                UseShellExecute = true
            };

            if (string.IsNullOrWhiteSpace(item.LaunchPath) &&
                !string.IsNullOrWhiteSpace(item.LaunchArguments))
            {
                startInfo.Arguments = item.LaunchArguments;
            }

            var workingDirectory = string.IsNullOrWhiteSpace(item.LaunchWorkingDirectory)
                ? Path.GetDirectoryName(launchPath)
                : item.LaunchWorkingDirectory;
            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            System.Diagnostics.Process.Start(startInfo);
            return true;
        }
        catch (Exception exception)
        {
            DebugLogger.WriteIfChanged(
                $"pinned-launch-error-{item.GroupKey}",
                $"Pinned taskbar launch failed: Group={item.GroupKey} Path={launchPath} {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private bool AddJumpListMenuItems(NativeTaskbarGroup group)
    {
        var added = false;
        foreach (var section in JumpListMenuProvider.GetSections(group.Items))
        {
            if (section.Entries.Count == 0)
            {
                continue;
            }

            if (added)
            {
                _appContextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            }

            _appContextMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(section.Title)
            {
                Enabled = false
            });

            foreach (var entry in section.Entries)
            {
                var action = entry.Execute;
                _appContextMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(ClipMenuText(entry.Title), null, (_, _) => action())
                {
                    ToolTipText = entry.Title
                });
            }

            added = true;
        }

        return added;
    }

    private static string ClipMenuText(string text)
    {
        var normalized = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= MaxJumpListMenuItemTextLength)
        {
            return normalized;
        }

        return normalized[..(MaxJumpListMenuItemTextLength - 3)] + "...";
    }

    private static string GetMenuTitle(TaskbarItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            return item.Title;
        }

        return string.IsNullOrWhiteSpace(item.ProcessName)
            ? "Window"
            : item.ProcessName;
    }

    private void InvalidateExplorerButtonImageForHwnd(IntPtr hwnd, string reason)
    {
        foreach (var group in _visibleGroups.Where(group => group.Items.Any(item => item.Hwnd == hwnd)).ToArray())
        {
            ExplorerTaskbarSnapshotStore.InvalidateButtonImage(group.Items, reason);

            if (_explorerButtonImageCache.Remove(group.Key, out var cached))
            {
                cached.Dispose();
            }

            TryDeletePersistedExplorerButtonImage(GetExplorerButtonImageCacheKey(group.Key));
            DebugLogger.WriteIfChanged(
                $"explorer-button-image-render-cache-invalidated-{group.Key}",
                $"Explorer taskbar button render cache invalidated: Group={group.Key} Hwnd=0x{hwnd.ToInt64():X} Reason={reason}");
        }
    }

    private NativeTaskbarButton? HitTestTaskbarButton(int x, int y)
    {
        foreach (var button in _buttons)
        {
            if (button.Bounds.Contains(x, y))
            {
                return button;
            }
        }

        return null;
    }

    private NativeTrayButton? HitTestTray(int x, int y)
    {
        foreach (var button in _trayButtons)
        {
            if (button.Bounds.Contains(x, y))
            {
                return button;
            }
        }

        return null;
    }

    private bool HitTestTrayOverflow(int x, int y) =>
        _trayOverflowButton?.Bounds.Contains(x, y) == true;

    private void Paint()
    {
        var paint = new NativeMethods.PaintStruct
        {
            rgbReserved = new byte[32]
        };
        var hdc = NativeMethods.BeginPaint(_hwnd, out paint);
        try
        {
            RenderDirect2D();
        }
        catch (Exception exception)
        {
            DisposeRenderTargetResources();
            DebugLogger.WriteIfChanged(
                $"native-render-error-{_screen.DeviceName}",
                $"Native taskbar render failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            NativeMethods.EndPaint(_hwnd, ref paint);
        }
    }

    private void RenderDirect2D()
    {
        EnsureRenderTarget();
        var target = _renderTarget!;
        target.BeginDraw();
        target.Clear(GetTaskbarBackgroundColor());
        RenderTopBorder(target);

        _buttons.Clear();
        _trayButtons.Clear();
        _trayOverflowButton = null;
        _groupsWithExplorerButtonImage.Clear();
        var trayLayout = GetTrayLayout();
        var pauseIndicatorWidth = GetPauseIndicatorWidth();
        var layout = GetLayout(trayLayout.StartX, pauseIndicatorWidth);
        DebugLogger.WriteIfChanged(
            $"native-layout-{_screen.DeviceName}",
            "Native taskbar layout: " +
            $"Screen={_screen.DeviceName} Width={_width} Height={_height} " +
            $"Paused={_nonClockUpdatesPaused} PauseIndicator={pauseIndicatorWidth} " +
            $"Apps={_visibleWindows.Count} Groups={_visibleGroups.Count} AppStart={layout.StartX} AppButton={layout.ButtonSize} " +
            $"TrayItems={_trayItems.Count} InlineTray={_inlineTrayItems.Count} OverflowTray={GetTrayOverflowItems(trayLayout).Count} VisibleTray={trayLayout.VisibleIconCount} " +
            $"TrayStart={trayLayout.StartX} ClockLeft={trayLayout.ClockBounds.Left} ClockRight={trayLayout.ClockBounds.Right}");
        RenderPauseIndicator(target, pauseIndicatorWidth);
        var x = layout.StartX;
        foreach (var group in _visibleGroups)
        {
            var bounds = new Rectangle(x, layout.ButtonY, layout.ButtonSize, layout.ButtonSize);
            RenderButton(target, group, bounds, layout);
            _buttons.Add(new NativeTaskbarButton(group, bounds));
            x += layout.ButtonSize;
        }

        RenderTrayButtons(target, trayLayout);
        RenderIcons(target, layout, trayLayout);

        ulong tag1;
        ulong tag2;
        target.EndDraw(out tag1, out tag2);
    }

    private void RenderTopBorder(ID2D1HwndRenderTarget target)
    {
        target.FillRectangle(Rect(0, 0, _width, TopBorderHeight), _topBorderBrush!);
    }

    private void RenderPauseIndicator(ID2D1HwndRenderTarget target, int reservedWidth)
    {
        if (!_nonClockUpdatesPaused || reservedWidth <= 0)
        {
            return;
        }

        var scale = GetEffectiveTaskbarScale();
        var barWidth = Math.Max(2, (int)Math.Round(3 * scale));
        var barHeight = Math.Max(12, (int)Math.Round(15 * scale));
        var gap = Math.Max(3, (int)Math.Round(4 * scale));
        var left = Math.Max(6, (reservedWidth - ((barWidth * 2) + gap)) / 2);
        var top = Math.Max(0, (_height - barHeight) / 2);
        var cornerRadius = Math.Max(1, barWidth / 2);
        target.FillRoundedRectangle(
            Rounded(Rect(left, top, left + barWidth, top + barHeight), cornerRadius),
            _pauseIndicatorBrush!);
        target.FillRoundedRectangle(
            Rounded(Rect(left + barWidth + gap, top, left + (barWidth * 2) + gap, top + barHeight), cornerRadius),
            _pauseIndicatorBrush!);
    }

    private void RenderTrayButtons(ID2D1HwndRenderTarget target, NativeTrayLayout trayLayout)
    {
        if (trayLayout.HasOverflowButton)
        {
            var bounds = trayLayout.OverflowButtonBounds;
            if (_hoverTrayKey == TrayOverflowButtonKey || IsTrayOverflowFlyoutVisible())
            {
                target.FillRoundedRectangle(
                    Rounded(Rect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), trayLayout.CornerRadius),
                    _hoverBrush!);
            }

            DrawTrayOverflowChevron(target, bounds, IsTrayOverflowFlyoutVisible());
            _trayOverflowButton = new NativeTrayOverflowButton(bounds);
        }

        var x = trayLayout.IconStartX;
        for (var index = 0; index < trayLayout.VisibleIconCount; index++)
        {
            var item = _inlineTrayItems[index];
            var key = GetTrayKey(item);
            var bounds = new Rectangle(x, trayLayout.ButtonY, trayLayout.ButtonWidth, trayLayout.ButtonHeight);
            if (key == _hoverTrayKey)
            {
                target.FillRoundedRectangle(
                    Rounded(Rect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), trayLayout.CornerRadius),
                    _hoverBrush!);
            }

            _trayButtons.Add(new NativeTrayButton(key, item, bounds));
            x += trayLayout.ButtonWidth;
        }
    }

    private void DrawTrayOverflowChevron(ID2D1HwndRenderTarget target, Rectangle bounds, bool expanded)
    {
        var scale = GetEffectiveTaskbarScale();
        var halfWidth = Math.Max(4, (float)(4 * scale));
        var halfHeight = Math.Max(3, (float)(3 * scale));
        var centerX = bounds.Left + bounds.Width / 2f;
        var centerY = bounds.Top + bounds.Height / 2f;
        var left = new Vector2(centerX - halfWidth, expanded ? centerY - halfHeight / 2f : centerY + halfHeight / 2f);
        var middle = new Vector2(centerX, expanded ? centerY + halfHeight : centerY - halfHeight);
        var right = new Vector2(centerX + halfWidth, expanded ? centerY - halfHeight / 2f : centerY + halfHeight / 2f);
        var strokeWidth = Math.Max(1.4f, (float)(1.4 * scale));
        target.DrawLine(left, middle, _clockTextBrush!, strokeWidth);
        target.DrawLine(middle, right, _clockTextBrush!, strokeWidth);
    }

    private void RenderButton(ID2D1HwndRenderTarget target, NativeTaskbarGroup group, Rectangle bounds, NativeLayout layout)
    {
        var item = group.Representative;
        var buttonRect = Rect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        if (group.Key == _hoverGroupKey)
        {
            target.FillRoundedRectangle(Rounded(buttonRect, layout.CornerRadius), _hoverBrush!);
        }

        if (group.IsActive)
        {
            target.FillRoundedRectangle(Rounded(buttonRect, layout.CornerRadius), _activeBrush!);
        }

        var state = GetGroupState(group);
        if (state.ProgressState != TbpfNoProgress)
        {
            var progressWidth = GetProgressWidth(state, bounds.Width);
            if (progressWidth > 0)
            {
                target.FillRectangle(
                    Rect(bounds.Left, bounds.Top, bounds.Left + progressWidth, bounds.Bottom),
                    GetProgressBrush(state.ProgressState));
            }
        }

        RenderGroupUnderline(target, group, bounds, layout);
    }

    private void RenderGroupUnderline(ID2D1HwndRenderTarget target, NativeTaskbarGroup group, Rectangle bounds, NativeLayout layout)
    {
        if (!group.HasRunning)
        {
            return;
        }

        var accentBrush = group.IsActive ? _activeAccentBrush! : _inactiveAccentBrush!;
        var underlineTop = bounds.Bottom - layout.UnderlineHeight - 1;
        if (!group.HasMultiple)
        {
            var underlineLeft = bounds.Left + (bounds.Width - layout.UnderlineWidth) / 2;
            target.FillRoundedRectangle(
                Rounded(Rect(underlineLeft, underlineTop, underlineLeft + layout.UnderlineWidth, underlineTop + layout.UnderlineHeight), 1),
                accentBrush);
            return;
        }

        var segmentCount = Math.Min(3, group.Items.Count);
        var gap = Math.Max(2, layout.UnderlineHeight);
        var segmentWidth = Math.Max(5, (layout.UnderlineWidth - (segmentCount - 1) * gap) / segmentCount);
        var totalWidth = segmentCount * segmentWidth + (segmentCount - 1) * gap;
        var left = bounds.Left + (bounds.Width - totalWidth) / 2;
        for (var index = 0; index < segmentCount; index++)
        {
            var segmentLeft = left + index * (segmentWidth + gap);
            target.FillRoundedRectangle(
                Rounded(Rect(segmentLeft, underlineTop, segmentLeft + segmentWidth, underlineTop + layout.UnderlineHeight), 1),
                accentBrush);
        }
    }

    private static TaskbarButtonState GetGroupState(NativeTaskbarGroup group)
    {
        var fallback = TaskbarButtonState.Empty;
        var representativeHwnd = group.Representative.Hwnd;
        foreach (var item in group.Items)
        {
            TaskbarStateSnapshotStore.TryGetState(item.Hwnd, out var state);
            if (item.Hwnd == representativeHwnd)
            {
                fallback = state;
            }

            if (state.ProgressState != TbpfNoProgress)
            {
                return state;
            }
        }

        return fallback;
    }

    private static TaskbarItem GetOverlayRepresentative(NativeTaskbarGroup group, out TaskbarButtonState state)
    {
        foreach (var item in group.Items)
        {
            TaskbarStateSnapshotStore.TryGetState(item.Hwnd, out state);
            if (state.OverlayIcon is not null)
            {
                return item;
            }
        }

        TaskbarStateSnapshotStore.TryGetState(group.Representative.Hwnd, out state);
        return group.Representative;
    }

    private NativeTrayIconImage? GetTrayBadgeOverlay(NativeTaskbarGroup group)
    {
        foreach (var item in group.Items)
        {
            if (IsShellOwnedProcess(item))
            {
                continue;
            }

            var matchedTrayItem = FindTrayItemForTaskbarItem(item);
            if (matchedTrayItem?.Icon is null)
            {
                continue;
            }

            DebugLogger.WriteIfChanged(
                $"tray-badge-fallback-{group.Key}-{matchedTrayItem.Identity}",
                "Tray badge fallback used: " +
                $"Group={group.Key} WindowProcess={item.ProcessName} WindowPath={item.ProcessPath} " +
                $"TrayIdentity={matchedTrayItem.Identity} TrayProcess={matchedTrayItem.SourceProcessName} " +
                $"TrayPath={matchedTrayItem.SourceProcessPath} TrayTooltip={matchedTrayItem.ToolTip}");

            return GetTrayImage(matchedTrayItem);
        }

        return null;
    }

    private TrayIconItem? FindTrayItemForTaskbarItem(TaskbarItem taskbarItem)
    {
        if (!string.IsNullOrWhiteSpace(taskbarItem.ProcessPath) &&
            _trayItemsByProcessPath.TryGetValue(NormalizePath(taskbarItem.ProcessPath), out var pathMatch))
        {
            return pathMatch;
        }

        return !string.IsNullOrWhiteSpace(taskbarItem.ProcessName) &&
               _trayItemsByProcessName.TryGetValue(Path.GetFileNameWithoutExtension(taskbarItem.ProcessName), out var nameMatch)
            ? nameMatch
            : null;
    }

    private static bool IsShellOwnedProcess(TaskbarItem taskbarItem)
    {
        var processName = Path.GetFileNameWithoutExtension(taskbarItem.ProcessName);
        var fileName = string.IsNullOrWhiteSpace(taskbarItem.ProcessPath)
            ? ""
            : Path.GetFileName(taskbarItem.ProcessPath);

        return string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "explorer.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExplorerGroup(NativeTaskbarGroup group) =>
        group.Items.Any(IsExplorerItem);

    private static bool IsExplorerItem(TaskbarItem item)
    {
        var processName = Path.GetFileNameWithoutExtension(item.ProcessName);
        var processFileName = string.IsNullOrWhiteSpace(item.ProcessPath)
            ? ""
            : Path.GetFileName(item.ProcessPath);
        var iconFileName = string.IsNullOrWhiteSpace(item.IconPath)
            ? ""
            : Path.GetFileName(item.IconPath);

        return string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(processFileName, "explorer.exe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(iconFileName, "explorer.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).TrimEnd('\\');
        }
        catch
        {
            return path.Trim();
        }
    }

    private void RenderIcons(ID2D1HwndRenderTarget target, NativeLayout layout, NativeTrayLayout trayLayout)
    {
        foreach (var button in _buttons)
        {
            var explorerButtonImage = IsExplorerGroup(button.Group)
                ? null
                : GetExplorerButtonImage(button.Group);
            var bitmap = explorerButtonImage?.Bitmap;
            if (explorerButtonImage is not null)
            {
                _groupsWithExplorerButtonImage.Add(button.Group.Key);
            }
            else
            {
                bitmap = GetIcon(button.Group.Representative, layout.IconSize)?.Bitmap;
            }

            if (bitmap is null)
            {
                continue;
            }

            var iconX = button.Bounds.Left + (button.Bounds.Width - layout.IconSize) / 2;
            var iconY = button.Bounds.Top + (button.Bounds.Height - layout.IconSize) / 2;
            target.DrawBitmap(
                bitmap,
                Rect(iconX, iconY, iconX + layout.IconSize, iconY + layout.IconSize),
                1.0f,
                BitmapInterpolationMode.Linear,
                null);
        }

        RenderTrayIconsAndClock(target, trayLayout);
    }

    private void RenderOverlayIcons(ID2D1HwndRenderTarget target, NativeLayout layout)
    {
        foreach (var button in _buttons)
        {
            if (_groupsWithExplorerButtonImage.Contains(button.Group.Key))
            {
                continue;
            }

            var item = GetOverlayRepresentative(button.Group, out var state);
            var overlay = GetOverlayIcon(item.Hwnd, state);
            var bitmap = overlay?.Bitmap ?? GetTrayBadgeOverlay(button.Group)?.Bitmap;
            if (bitmap is null)
            {
                continue;
            }

            var x = button.Bounds.Right - layout.OverlaySize - layout.OverlayInset;
            var y = button.Bounds.Bottom - layout.OverlaySize - layout.OverlayInset;
            target.DrawBitmap(
                bitmap,
                Rect(x, y, x + layout.OverlaySize, y + layout.OverlaySize),
                1.0f,
                BitmapInterpolationMode.Linear,
                null);
        }
    }

    private void RenderTrayIconsAndClock(ID2D1HwndRenderTarget target, NativeTrayLayout trayLayout)
    {
        foreach (var button in _trayButtons)
        {
            var image = GetTrayImage(button.Item);
            if (image?.Bitmap is null)
            {
                continue;
            }

            var iconX = button.Bounds.Left + (button.Bounds.Width - trayLayout.IconSize) / 2;
            var iconY = button.Bounds.Top + (button.Bounds.Height - trayLayout.IconSize) / 2;
            target.DrawBitmap(
                image.Bitmap,
                Rect(iconX, iconY, iconX + trayLayout.IconSize, iconY + trayLayout.IconSize),
                1.0f,
                BitmapInterpolationMode.Linear,
                null);
        }

        if (trayLayout.ClockBounds.Width > 0)
        {
            EnsureClockTextFormat(trayLayout.ClockFontSize);
            target.DrawText(
                _clockText,
                _clockTextFormat!,
                new Vortice.Mathematics.Rect(
                    trayLayout.ClockBounds.Left,
                    trayLayout.ClockBounds.Top,
                    trayLayout.ClockBounds.Width,
                    trayLayout.ClockBounds.Height),
                _clockTextBrush!);
        }
    }

    private NativeAppIcon? GetIcon(TaskbarItem item, int desiredIconSize)
    {
        var cacheKey = GetIconCacheKey(item);
        var renderFingerprint = $"{item.IconFingerprint}:icon-size:{desiredIconSize}";
        if (_iconCache.TryGetValue(cacheKey, out var cached) &&
            string.Equals(cached.Fingerprint, renderFingerprint, StringComparison.Ordinal))
        {
            cached.EnsureBitmap(_renderTarget!);
            return cached;
        }

        if (cached is not null)
        {
            cached.Dispose();
        }

        if (ShouldPreferManagedIcon(item) &&
            TryCreateManagedIcon(item, renderFingerprint, out var managedIcon))
        {
            managedIcon.EnsureBitmap(_renderTarget!);
            _iconCache[cacheKey] = managedIcon;
            return managedIcon;
        }

        var icon = WindowIconProvider.GetIconHandleCopy(
            item.Hwnd,
            item.ProcessPath,
            item.IconPath,
            item.IconIndex,
            desiredIconSize);
        if (icon == IntPtr.Zero)
        {
            if (TryCreateManagedIcon(item, renderFingerprint, out managedIcon))
            {
                managedIcon.EnsureBitmap(_renderTarget!);
                _iconCache[cacheKey] = managedIcon;
                return managedIcon;
            }

            _iconCache.Remove(cacheKey);
            return null;
        }

        var nativeIcon = new NativeAppIcon(renderFingerprint, icon, desiredIconSize);
        nativeIcon.EnsureBitmap(_renderTarget!);
        _iconCache[cacheKey] = nativeIcon;
        return nativeIcon;
    }

    private static string GetIconCacheKey(TaskbarItem item) =>
        item.Hwnd != IntPtr.Zero
            ? $"hwnd:{item.Hwnd.ToInt64():X}"
            : $"pin:{item.GroupKey}";

    private static bool ShouldPreferManagedIcon(TaskbarItem item) =>
        !IsExplorerItem(item) &&
        item.Icon is not null &&
        (PackageAppResolver.IsPackageAppId(item.AppUserModelId) ||
         (!string.IsNullOrWhiteSpace(item.IconPath) &&
          !string.Equals(item.IconPath, item.ProcessPath, StringComparison.OrdinalIgnoreCase)));

    private static bool TryCreateManagedIcon(TaskbarItem item, string fingerprint, out NativeAppIcon icon)
    {
        icon = default!;
        if (item.Icon is null || CreateBitmapSource(item.Icon) is not { } bitmapSource)
        {
            return false;
        }

        icon = new NativeAppIcon(fingerprint, bitmapSource);
        return true;
    }

    private NativeExplorerButtonImage? GetExplorerButtonImage(NativeTaskbarGroup group)
    {
        if (!AppSettingsService.Current.EnableExperimentalExplorerTaskbarHook)
        {
            return null;
        }

        if (!AppSettingsService.Current.EnableExperimentalExplorerTaskbarButtonImageCapture)
        {
            return null;
        }

        if (!ExplorerTaskbarSnapshotStore.TryGetButtonImage(group.Items, out var image))
        {
            if (_explorerButtonImageCache.TryGetValue(group.Key, out var stale))
            {
                stale.EnsureBitmap(_renderTarget!);
                DebugLogger.WriteIfChanged(
                    $"explorer-button-image-retained-{group.Key}",
                    $"Explorer taskbar button image retained after snapshot miss: Group={group.Key} Button={stale.ButtonName} Fingerprint={ShortFingerprint(stale.Fingerprint)}");
                return stale;
            }

            return TryLoadPersistedExplorerButtonImage(group.Key);
        }

        if (!IsUsableExplorerButtonImage(image.PngBytes))
        {
            DebugLogger.WriteIfChanged(
                $"explorer-button-image-rejected-{group.Key}",
                $"Explorer taskbar button image rejected before render: Group={group.Key} Button={image.ButtonName} Bytes={image.PngBytes.Length} Fingerprint={ShortFingerprint(image.Fingerprint)}");
            if (_explorerButtonImageCache.TryGetValue(group.Key, out var stale))
            {
                stale.EnsureBitmap(_renderTarget!);
                return stale;
            }

            return TryLoadPersistedExplorerButtonImage(group.Key);
        }

        if (_explorerButtonImageCache.TryGetValue(group.Key, out var cached) &&
            string.Equals(cached.Fingerprint, image.Fingerprint, StringComparison.Ordinal))
        {
            cached.EnsureBitmap(_renderTarget!);
            return cached;
        }

        if (cached is not null)
        {
            cached.Dispose();
        }

        try
        {
            using var stream = new MemoryStream(image.PngBytes);
            using var sourceImage = Image.FromStream(stream);
            var explorerButtonImage = new NativeExplorerButtonImage(
                image.Fingerprint,
                image.ButtonName,
                CreateSquareBitmap(sourceImage));
            explorerButtonImage.EnsureBitmap(_renderTarget!);
            _explorerButtonImageCache[group.Key] = explorerButtonImage;
            PersistExplorerButtonImage(group.Key, image);

            DebugLogger.WriteIfChanged(
                $"explorer-button-image-{group.Key}",
                $"Explorer taskbar button image used: Group={group.Key} Button={image.ButtonName} Bytes={image.PngBytes.Length} Fingerprint={ShortFingerprint(image.Fingerprint)}");

            return explorerButtonImage;
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException or IOException)
        {
            DebugLogger.WriteIfChanged(
                $"explorer-button-image-error-{group.Key}",
                $"Explorer taskbar button image decode failed: Group={group.Key} {exception.GetType().Name}: {exception.Message}");
            _explorerButtonImageCache.Remove(group.Key);
            return null;
        }
    }

    private NativeExplorerButtonImage? TryLoadPersistedExplorerButtonImage(string groupKey)
    {
        try
        {
            var cacheKey = GetExplorerButtonImageCacheKey(groupKey);
            var pngPath = Path.Combine(ExplorerButtonImageCacheDirectory, cacheKey + ".png");
            if (!File.Exists(pngPath))
            {
                return null;
            }

            var pngBytes = File.ReadAllBytes(pngPath);
            if (!IsUsableExplorerButtonImage(pngBytes))
            {
                TryDeletePersistedExplorerButtonImage(cacheKey);
                DebugLogger.WriteIfChanged(
                    $"explorer-button-image-cache-rejected-{groupKey}",
                    $"Explorer taskbar button image disk cache rejected: Group={groupKey} Bytes={pngBytes.Length}");
                return null;
            }

            var metadataPath = Path.Combine(ExplorerButtonImageCacheDirectory, cacheKey + ".txt");
            var metadata = File.Exists(metadataPath)
                ? File.ReadAllLines(metadataPath)
                : Array.Empty<string>();
            var fingerprint = metadata.Length > 0 && !string.IsNullOrWhiteSpace(metadata[0])
                ? metadata[0]
                : File.GetLastWriteTimeUtc(pngPath).Ticks.ToString();
            var buttonName = metadata.Length > 1 ? metadata[1] : "";

            using var stream = new MemoryStream(pngBytes);
            using var sourceImage = Image.FromStream(stream);
            var explorerButtonImage = new NativeExplorerButtonImage(
                fingerprint,
                buttonName,
                CreateSquareBitmap(sourceImage));
            explorerButtonImage.EnsureBitmap(_renderTarget!);
            _explorerButtonImageCache[groupKey] = explorerButtonImage;

            DebugLogger.WriteIfChanged(
                $"explorer-button-image-cache-{groupKey}",
                $"Explorer taskbar button image loaded from disk cache: Group={groupKey} Button={buttonName} Fingerprint={ShortFingerprint(fingerprint)}");

            return explorerButtonImage;
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException or IOException or UnauthorizedAccessException)
        {
            DebugLogger.WriteIfChanged(
                $"explorer-button-image-cache-error-{groupKey}",
                $"Explorer taskbar button image disk cache load failed: Group={groupKey} {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    private static void PersistExplorerButtonImage(string groupKey, ExplorerTaskbarButtonImage image)
    {
        if (!IsUsableExplorerButtonImage(image.PngBytes))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(ExplorerButtonImageCacheDirectory);
            var cacheKey = GetExplorerButtonImageCacheKey(groupKey);
            File.WriteAllBytes(Path.Combine(ExplorerButtonImageCacheDirectory, cacheKey + ".png"), image.PngBytes);
            File.WriteAllLines(
                Path.Combine(ExplorerButtonImageCacheDirectory, cacheKey + ".txt"),
                [image.Fingerprint, image.ButtonName]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DebugLogger.WriteIfChanged(
                $"explorer-button-image-cache-save-error-{groupKey}",
                $"Explorer taskbar button image disk cache save failed: Group={groupKey} {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static bool IsUsableExplorerButtonImage(byte[] pngBytes) =>
        pngBytes.Length >= MinimumUsableExplorerButtonImageBytes;

    private static void TryDeletePersistedExplorerButtonImage(string cacheKey)
    {
        try
        {
            File.Delete(Path.Combine(ExplorerButtonImageCacheDirectory, cacheKey + ".png"));
            File.Delete(Path.Combine(ExplorerButtonImageCacheDirectory, cacheKey + ".txt"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DebugLogger.WriteIfChanged(
                $"explorer-button-image-cache-delete-error-{cacheKey}",
                $"Explorer taskbar button image disk cache delete failed: CacheKey={cacheKey} {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string GetExplorerButtonImageCacheKey(string groupKey)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var character in "transparent-v2:")
        {
            hash ^= character;
            hash *= prime;
        }

        foreach (var character in groupKey)
        {
            hash ^= character;
            hash *= prime;
        }

        return $"v2-{groupKey.Length:X}-{hash:X16}";
    }

    private static Bitmap CreateSquareBitmap(Image image)
    {
        if (image.Width == image.Height)
        {
            return new Bitmap(image);
        }

        var size = Math.Max(image.Width, image.Height);
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        ConfigureHighQualityGraphics(graphics);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.Clear(System.Drawing.Color.Transparent);
        graphics.DrawImage(
            image,
            (size - image.Width) / 2,
            (size - image.Height) / 2,
            image.Width,
            image.Height);
        return bitmap;
    }

    private static Bitmap CreateIconBitmap(IntPtr iconHandle, int width, int height)
    {
        var bitmap = new Bitmap(
            Math.Max(1, width),
            Math.Max(1, height),
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.Transparent);

        var hdc = graphics.GetHdc();
        try
        {
            NativeMethods.DrawIconEx(
                hdc,
                0,
                0,
                iconHandle,
                bitmap.Width,
                bitmap.Height,
                0,
                IntPtr.Zero,
                NativeMethods.DI_NORMAL);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        return bitmap;
    }

    private static BitmapSource? CreateIconBitmapSource(IntPtr iconHandle, int size)
    {
        if (iconHandle == IntPtr.Zero || size <= 0)
        {
            return null;
        }

        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                iconHandle,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(size, size));
            return CreateBitmapSource(source);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ExternalException or NotSupportedException)
        {
            return null;
        }
    }

    private NativeOverlayIcon? GetOverlayIcon(IntPtr hwnd, TaskbarButtonState state)
    {
        if (state.OverlayIcon is null)
        {
            if (_overlayCache.Remove(hwnd, out var removed))
            {
                removed.Dispose();
            }

            return null;
        }

        if (_overlayCache.TryGetValue(hwnd, out var cached) &&
            string.Equals(cached.Fingerprint, state.OverlayFingerprint, StringComparison.Ordinal))
        {
            cached.EnsureBitmap(_renderTarget!);
            return cached;
        }

        if (cached is not null)
        {
            cached.Dispose();
        }

        if (CreateBitmapSource(state.OverlayIcon) is not { } bitmapSource)
        {
            _overlayCache.Remove(hwnd);
            return null;
        }

        var overlayIcon = new NativeOverlayIcon(state.OverlayFingerprint, bitmapSource);
        overlayIcon.EnsureBitmap(_renderTarget!);
        _overlayCache[hwnd] = overlayIcon;
        return overlayIcon;
    }

    private NativeTrayIconImage? GetTrayImage(TrayIconItem item)
    {
        if (item.Icon is null)
        {
            return null;
        }

        var key = GetTrayKey(item);
        if (_trayImageCache.TryGetValue(key, out var cached) &&
            ReferenceEquals(cached.Source, item.Icon))
        {
            cached.EnsureBitmap(_renderTarget!);
            return cached;
        }

        if (cached is not null)
        {
            cached.Dispose();
        }

        if (CreateBitmapSource(item.Icon) is not { } bitmapSource)
        {
            _trayImageCache.Remove(key);
            return null;
        }

        var trayIcon = new NativeTrayIconImage(item.Icon, bitmapSource);
        trayIcon.EnsureBitmap(_renderTarget!);
        _trayImageCache[key] = trayIcon;
        return trayIcon;
    }

    private static BitmapSource? CreateBitmapSource(WpfImageSource source)
    {
        if (source is not BitmapSource bitmapSource)
        {
            return null;
        }

        try
        {
            BitmapSource converted = bitmapSource;
            if (converted.Format != WpfPixelFormats.Pbgra32)
            {
                converted = new FormatConvertedBitmap(bitmapSource, WpfPixelFormats.Pbgra32, null, 0);
            }

            if (converted.CanFreeze && !converted.IsFrozen)
            {
                converted.Freeze();
            }

            return converted;
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or ExternalException)
        {
            DebugLogger.WriteIfChanged(
                "native-tray-image-error",
                $"Native tray icon image conversion failed: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    private static ID2D1Bitmap? CreateDirect2DBitmap(ID2D1HwndRenderTarget target, BitmapSource source)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        source.CopyPixels(pixels, stride, 0);
        var pixelsHandle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return target.CreateBitmap(
                new SizeI(width, height),
                pixelsHandle.AddrOfPinnedObject(),
                (uint)stride,
                new BitmapProperties(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
        }
        finally
        {
            pixelsHandle.Free();
        }
    }

    private static ID2D1Bitmap? CreateDirect2DBitmap(ID2D1HwndRenderTarget target, Image image)
    {
        using var bitmap = new Bitmap(
            image.Width,
            image.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            ConfigureHighQualityGraphics(graphics);
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.DrawImage(image, 0, 0, image.Width, image.Height);
        }

        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(
            bounds,
            ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

        try
        {
            return target.CreateBitmap(
                new SizeI(bitmap.Width, bitmap.Height),
                data.Scan0,
                (uint)data.Stride,
                new BitmapProperties(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void ConfigureHighQualityGraphics(Graphics graphics)
    {
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    }

    private static string GetTrayKey(TrayIconItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Identity))
        {
            return item.Identity;
        }

        return $"{item.ToolbarHwnd.ToInt64():X}:{item.ScreenRect.Left}:{item.ScreenRect.Top}:{item.ToolTip}";
    }

    private static string BuildTrayVisualSignature(IReadOnlyList<TrayIconItem> items)
    {
        return string.Join("|", items.Select(item =>
            $"{GetTrayKey(item)}:{RuntimeHelpers.GetHashCode(item.Icon)}"));
    }

    private static IReadOnlyList<NativeTaskbarGroup> BuildWindowGroups(IReadOnlyList<TaskbarItem> items)
    {
        return items
            .GroupBy(item => item.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new NativeTaskbarGroup(group.Key, group.ToArray()))
            .ToArray();
    }

    private static string BuildWindowVisualSignature(IReadOnlyList<NativeTaskbarGroup> groups)
    {
        return string.Join("|", groups.Select(group =>
            $"{group.Key}:{string.Join(",", group.Items.Select(item => $"{item.Hwnd.ToInt64():X}:{item.IsActive}:{item.IsMinimized}:{item.MonitorDeviceName}:{item.IconFingerprint}:{item.IconIndex}"))}"));
    }

    private int GetPauseIndicatorWidth()
    {
        if (!_nonClockUpdatesPaused)
        {
            return 0;
        }

        return Math.Max(18, (int)Math.Round(24 * GetEffectiveTaskbarScale()));
    }

    private NativeLayout GetLayout(int availableWidth, int leftReservedWidth)
    {
        var taskbarSettings = GetMonitorTaskbarSettings();
        var scale = GetEffectiveTaskbarScale();
        var buttonSize = Math.Min(_height, Math.Max(28, (int)Math.Round(36 * scale)));
        var iconSize = Math.Max(16, (int)Math.Round(20 * scale));
        var overlaySize = Math.Max(9, (int)Math.Round(11 * scale));
        var underlineWidth = Math.Max(10, (int)Math.Round(16 * scale));
        var totalWidth = buttonSize * _visibleGroups.Count;
        var appAreaWidth = Math.Max(0, availableWidth - leftReservedWidth);
        var startX = string.Equals(taskbarSettings.TaskbarButtonAlignment, "Center", StringComparison.OrdinalIgnoreCase)
            ? leftReservedWidth + Math.Max(0, (appAreaWidth - totalWidth) / 2)
            : leftReservedWidth;

        return new NativeLayout(
            startX,
            Math.Max(0, (_height - buttonSize) / 2),
            buttonSize,
            iconSize,
            overlaySize,
            Math.Max(2, (int)Math.Round(5 * scale)),
            underlineWidth,
            Math.Max(2, (int)Math.Round(2 * scale)),
            Math.Max(2, (int)Math.Round(3 * scale)));
    }

    private NativeTrayLayout GetTrayLayout()
    {
        var taskbarSettings = GetMonitorTaskbarSettings();
        var scale = GetEffectiveTaskbarScale();
        var buttonWidth = Math.Max(22, (int)Math.Round(28 * scale));
        var buttonHeight = Math.Min(_height, Math.Max(28, (int)Math.Round(36 * scale)));
        var iconSize = Math.Max(12, (int)Math.Round(16 * scale));
        var clockWidth = taskbarSettings.ShowClock
            ? Math.Max(52, (int)Math.Round(62 * scale))
            : 0;
        var rightPadding = Math.Max(6, (int)Math.Round(8 * scale));
        var canShowTray = taskbarSettings.MirrorPrimaryNotificationArea;
        var canShowOverflow = canShowTray && AppSettingsService.Current.ShowAllTrayIcons;
        var inlineTrayItemCount = canShowTray ? _inlineTrayItems.Count : 0;
        var explicitOverflowCount = canShowOverflow ? _overflowTrayItems.Count : 0;
        var reservedRightPadding = taskbarSettings.ShowClock || inlineTrayItemCount > 0 || explicitOverflowCount > 0
            ? rightPadding
            : 0;
        var maxTrayIconAreaWidth = Math.Max(0, _width / 2 - clockWidth - reservedRightPadding);
        var hasOverflowButton = canShowOverflow &&
                                (explicitOverflowCount > 0 ||
                                 inlineTrayItemCount * buttonWidth > maxTrayIconAreaWidth);
        var overflowButtonWidth = hasOverflowButton ? buttonWidth : 0;
        var visibleTrayIconCount = Math.Min(
            inlineTrayItemCount,
            buttonWidth == 0 ? 0 : Math.Max(0, maxTrayIconAreaWidth - overflowButtonWidth) / buttonWidth);
        hasOverflowButton = hasOverflowButton &&
                            (explicitOverflowCount > 0 ||
                             inlineTrayItemCount > visibleTrayIconCount);
        overflowButtonWidth = hasOverflowButton ? buttonWidth : 0;
        var trayWidth = overflowButtonWidth + (visibleTrayIconCount * buttonWidth) + clockWidth + reservedRightPadding;
        var startX = Math.Max(0, _width - trayWidth);
        var buttonY = Math.Max(0, (_height - buttonHeight) / 2);
        var overflowButtonBounds = hasOverflowButton
            ? new Rectangle(startX, buttonY, buttonWidth, buttonHeight)
            : Rectangle.Empty;
        var clockBounds = new RectangleF(
            taskbarSettings.ShowClock ? _width - rightPadding - clockWidth : _width,
            0,
            clockWidth,
            _height);

        return new NativeTrayLayout(
            startX,
            startX + overflowButtonWidth,
            buttonY,
            buttonWidth,
            buttonHeight,
            iconSize,
            visibleTrayIconCount,
            hasOverflowButton,
            overflowButtonBounds,
            clockBounds,
            Math.Max(9, (int)Math.Round(12 * scale)),
            Math.Max(2, (int)Math.Round(3 * scale)));
    }

    private int GetTaskbarHeightPixels()
    {
        return Math.Max(32, (int)Math.Round(40 * GetEffectiveTaskbarScale()));
    }

    private int GetAutoHideTriggerThicknessPixels()
    {
        return Math.Max(2, (int)Math.Round(2 * GetEffectiveTaskbarScale()));
    }

    private MonitorTaskbarSettings GetMonitorTaskbarSettings() =>
        AppSettingsService.GetMonitorTaskbarSettings(_screen.DeviceName);

    private bool IsAutoHideEnabled() =>
        GetMonitorTaskbarSettings().AutomaticallyHideTaskbar;

    private double GetEffectiveTaskbarScale()
    {
        return GetMonitorTaskbarSettings().TaskbarScale * NativeScaleBaseline;
    }

    private double GetTaskbarOpacity()
    {
        return GetMonitorTaskbarSettings().TaskbarOpacity;
    }

    private static bool ReadSystemUsesLightTheme()
    {
        try
        {
            return Registry.GetValue(
                ThemePersonalizeRegistryKey,
                SystemUsesLightThemeRegistryValue,
                0) switch
            {
                int value => value != 0,
                long value => value != 0,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private (int Red, int Green, int Blue) GetTaskbarBackgroundRgb()
    {
        return _systemUsesLightTheme
            ? (LightTaskbarBackgroundRed, LightTaskbarBackgroundGreen, LightTaskbarBackgroundBlue)
            : (DarkTaskbarBackgroundRed, DarkTaskbarBackgroundGreen, DarkTaskbarBackgroundBlue);
    }

    private Color4 GetTaskbarBackgroundColor()
    {
        var opacity = (float)GetTaskbarOpacity();
        var background = GetTaskbarBackgroundRgb();
        return Color(background.Red, background.Green, background.Blue, opacity);
    }

    private void ApplyTaskbarBackdrop()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var opacity = GetTaskbarOpacity();
        ApplyWindowOpacity(opacity);
        var useAcrylic = opacity < 0.995;
        var accentPolicy = new NativeMethods.AccentPolicy
        {
            AccentState = useAcrylic
                ? NativeMethods.ACCENT_ENABLE_ACRYLICBLURBEHIND
                : NativeMethods.ACCENT_DISABLED,
            AccentFlags = useAcrylic ? 2 : 0,
            GradientColor = useAcrylic ? CreateAcrylicGradientColor(opacity) : 0,
            AnimationId = 0
        };

        try
        {
            NativeMethods.SetAccentPolicy(_hwnd, accentPolicy);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            DebugLogger.WriteIfChanged(
                $"native-backdrop-unsupported-{_screen.DeviceName}",
                $"Native taskbar acrylic backdrop unsupported: Screen={_screen.DeviceName} Error={exception.GetType().Name}: {exception.Message}");
        }
    }

    private void ApplyWindowOpacity(double opacity)
    {
        var useLayeredOpacity = opacity < 0.995;
        var currentStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var desiredStyle = useLayeredOpacity
            ? currentStyle | NativeMethods.WS_EX_LAYERED
            : currentStyle & ~NativeMethods.WS_EX_LAYERED;

        if (desiredStyle != currentStyle)
        {
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(desiredStyle));
            NativeMethods.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE |
                NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_FRAMECHANGED);
        }

        if (!useLayeredOpacity)
        {
            return;
        }

        var alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 1, 255);
        if (!NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, alpha, NativeMethods.LWA_ALPHA))
        {
            DebugLogger.WriteIfChanged(
                $"native-opacity-failed-{_screen.DeviceName}",
                $"Native taskbar opacity failed: Screen={_screen.DeviceName} LastError={Marshal.GetLastWin32Error()} Alpha={alpha}");
        }
    }

    private int CreateAcrylicGradientColor(double opacity)
    {
        var alpha = (uint)Math.Clamp((int)Math.Round(opacity * 255), 0, 255);
        var background = GetTaskbarBackgroundRgb();
        var value =
            (alpha << 24) |
            ((uint)background.Blue << 16) |
            ((uint)background.Green << 8) |
            (uint)background.Red;
        return unchecked((int)value);
    }

    private void EnsureRenderTarget()
    {
        _factory ??= D2D1.D2D1CreateFactory<ID2D1Factory>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        if (_renderTarget is not null)
        {
            return;
        }

        var renderTargetProperties = new RenderTargetProperties(
            RenderTargetType.Default,
            new PixelFormat(Format.Unknown, Vortice.DCommon.AlphaMode.Unknown),
            RenderTargetDpi,
            RenderTargetDpi,
            RenderTargetUsage.None,
            FeatureLevel.Default);
        var hwndProperties = new HwndRenderTargetProperties
        {
            Hwnd = _hwnd,
            PixelSize = new SizeI(_width, _height),
            PresentOptions = PresentOptions.None
        };

        _renderTarget = _factory.CreateHwndRenderTarget(renderTargetProperties, hwndProperties);
        if (_systemUsesLightTheme)
        {
            _activeBrush = _renderTarget.CreateSolidColorBrush(Color(224, 235, 246, 0.96f), null);
            _hoverBrush = _renderTarget.CreateSolidColorBrush(Color(232, 232, 232, 0.88f), null);
            _activeAccentBrush = _renderTarget.CreateSolidColorBrush(Color(0, 95, 184), null);
            _inactiveAccentBrush = _renderTarget.CreateSolidColorBrush(Color(96, 96, 96, 0.86f), null);
            _progressBrush = _renderTarget.CreateSolidColorBrush(Color(53, 132, 67, 0.30f), null);
            _indeterminateProgressBrush = _renderTarget.CreateSolidColorBrush(Color(0, 95, 184, 0.30f), null);
            _errorProgressBrush = _renderTarget.CreateSolidColorBrush(Color(183, 35, 35, 0.30f), null);
            _pausedProgressBrush = _renderTarget.CreateSolidColorBrush(Color(176, 120, 26, 0.30f), null);
            _clockTextBrush = _renderTarget.CreateSolidColorBrush(Color(32, 32, 32), null);
            _pauseIndicatorBrush = _renderTarget.CreateSolidColorBrush(Color(176, 120, 26), null);
            _topBorderBrush = _renderTarget.CreateSolidColorBrush(Color(0, 0, 0, 0.24f), null);
        }
        else
        {
            _activeBrush = _renderTarget.CreateSolidColorBrush(Color(48, 57, 67), null);
            _hoverBrush = _renderTarget.CreateSolidColorBrush(Color(44, 44, 44), null);
            _activeAccentBrush = _renderTarget.CreateSolidColorBrush(Color(118, 185, 237), null);
            _inactiveAccentBrush = _renderTarget.CreateSolidColorBrush(Color(120, 120, 120), null);
            _progressBrush = _renderTarget.CreateSolidColorBrush(Color(77, 166, 89, 0.36f), null);
            _indeterminateProgressBrush = _renderTarget.CreateSolidColorBrush(Color(82, 155, 214, 0.36f), null);
            _errorProgressBrush = _renderTarget.CreateSolidColorBrush(Color(199, 61, 58, 0.36f), null);
            _pausedProgressBrush = _renderTarget.CreateSolidColorBrush(Color(211, 158, 52, 0.36f), null);
            _clockTextBrush = _renderTarget.CreateSolidColorBrush(Color(220, 220, 220), null);
            _pauseIndicatorBrush = _renderTarget.CreateSolidColorBrush(Color(232, 177, 58), null);
            _topBorderBrush = _renderTarget.CreateSolidColorBrush(Color(255, 255, 255, 0.18f), null);
        }
    }

    private void EnsureClockTextFormat(int fontSize)
    {
        if (_clockTextFormat is not null && _clockTextFormatSize == fontSize)
        {
            return;
        }

        _clockTextFormat?.Dispose();
        _directWriteFactory ??= DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        _clockTextFormat = _directWriteFactory.CreateTextFormat(
            "Segoe UI",
            null,
            Vortice.DirectWrite.FontWeight.Normal,
            Vortice.DirectWrite.FontStyle.Normal,
            Vortice.DirectWrite.FontStretch.Normal,
            fontSize,
            "");
        _clockTextFormat.TextAlignment = Vortice.DirectWrite.TextAlignment.Center;
        _clockTextFormat.ParagraphAlignment = ParagraphAlignment.Center;
        _clockTextFormatSize = fontSize;
    }

    private ID2D1SolidColorBrush GetProgressBrush(int progressState)
    {
        return progressState switch
        {
            TbpfError => _errorProgressBrush!,
            TbpfPaused => _pausedProgressBrush!,
            TbpfIndeterminate => _indeterminateProgressBrush!,
            _ => _progressBrush!
        };
    }

    private static int GetProgressWidth(TaskbarButtonState state, int buttonWidth)
    {
        if (state.ProgressState == TbpfIndeterminate || state.ProgressTotal == 0)
        {
            return buttonWidth;
        }

        var fraction = Math.Clamp((double)state.ProgressCompleted / state.ProgressTotal, 0, 1);
        return Math.Max(2, (int)Math.Round(buttonWidth * fraction));
    }

    private void OnDestroyed()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DeregisterShellHookWindow(_hwnd);
            Windows.Remove(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        Cleanup();
    }

    private void Cleanup()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _clockTimer.Stop();
        _trayRefreshTimer.Stop();
        _renderTimer.Stop();
        _groupFlyoutTimer.Stop();
        _hoverPopupPollTimer.Stop();
        _fullscreenPauseTimer.Stop();
        _autoHideTimer.Stop();
        CloseTrayOverflowFlyout();
        CloseGroupFlyout();
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DeregisterShellHookWindow(_hwnd);
        }

        _windowTracker.WindowsChanged -= OnWindowsChanged;
        TaskbarStateSnapshotStore.StateChangedDetailed -= OnTaskbarStateChanged;
        TrayIconSnapshotStore.SnapshotsChanged -= OnTrayIconSnapshotsChanged;
        ExplorerTaskbarSnapshotStore.SnapshotChanged -= OnExplorerTaskbarSnapshotChanged;
        AppSettingsService.SettingsChanged -= OnSettingsChanged;
        _appBar?.Dispose();
        _appContextMenu.Dispose();
        _contextMenu.Dispose();
        DisposeRenderTargetResources();
        _factory?.Dispose();
        _factory = null;
        _clockTextFormat?.Dispose();
        _clockTextFormat = null;
        _directWriteFactory?.Dispose();
        _directWriteFactory = null;
        DestroyCachedIcons();
    }

    private void DisposeRenderTargetResources()
    {
        _activeBrush?.Dispose();
        _hoverBrush?.Dispose();
        _activeAccentBrush?.Dispose();
        _inactiveAccentBrush?.Dispose();
        _progressBrush?.Dispose();
        _indeterminateProgressBrush?.Dispose();
        _errorProgressBrush?.Dispose();
        _pausedProgressBrush?.Dispose();
        _clockTextBrush?.Dispose();
        _pauseIndicatorBrush?.Dispose();
        _topBorderBrush?.Dispose();
        DisposeCachedDirect2DBitmaps();
        _renderTarget?.Dispose();
        _activeBrush = null;
        _hoverBrush = null;
        _activeAccentBrush = null;
        _inactiveAccentBrush = null;
        _progressBrush = null;
        _indeterminateProgressBrush = null;
        _errorProgressBrush = null;
        _pausedProgressBrush = null;
        _clockTextBrush = null;
        _pauseIndicatorBrush = null;
        _topBorderBrush = null;
        _renderTarget = null;
    }

    private void RemoveUnusedIcons(HashSet<IntPtr> visibleHandles, HashSet<string> visibleIconKeys)
    {
        foreach (var key in _iconCache.Keys.Where(key => !visibleIconKeys.Contains(key)).ToArray())
        {
            _iconCache[key].Dispose();
            _iconCache.Remove(key);
        }

        foreach (var hwnd in _overlayCache.Keys.Where(hwnd => !visibleHandles.Contains(hwnd)).ToArray())
        {
            _overlayCache[hwnd].Dispose();
            _overlayCache.Remove(hwnd);
        }
    }

    private void RemoveUnusedTrayImages(HashSet<string> visibleKeys)
    {
        foreach (var key in _trayImageCache.Keys.Where(key => !visibleKeys.Contains(key)).ToArray())
        {
            _trayImageCache[key].Dispose();
            _trayImageCache.Remove(key);
        }
    }

    private void RemoveUnusedExplorerButtonImages(HashSet<string> visibleGroupKeys)
    {
        foreach (var key in _explorerButtonImageCache.Keys.Where(key => !visibleGroupKeys.Contains(key)).ToArray())
        {
            _explorerButtonImageCache[key].Dispose();
            _explorerButtonImageCache.Remove(key);
        }
    }

    private void DestroyCachedIcons()
    {
        foreach (var icon in _iconCache.Values)
        {
            icon.Dispose();
        }

        _iconCache.Clear();

        foreach (var overlay in _overlayCache.Values)
        {
            overlay.Dispose();
        }

        _overlayCache.Clear();

        foreach (var trayIcon in _trayImageCache.Values)
        {
            trayIcon.Dispose();
        }

        _trayImageCache.Clear();

        foreach (var explorerButtonImage in _explorerButtonImageCache.Values)
        {
            explorerButtonImage.Dispose();
        }

        _explorerButtonImageCache.Clear();
    }

    private void DisposeCachedDirect2DBitmaps()
    {
        foreach (var icon in _iconCache.Values)
        {
            icon.DisposeBitmap();
        }

        foreach (var overlay in _overlayCache.Values)
        {
            overlay.DisposeBitmap();
        }

        foreach (var trayIcon in _trayImageCache.Values)
        {
            trayIcon.DisposeBitmap();
        }

        foreach (var explorerButtonImage in _explorerButtonImageCache.Values)
        {
            explorerButtonImage.DisposeBitmap();
        }
    }

    private static string ShortFingerprint(string fingerprint) =>
        fingerprint.Length <= 12 ? fingerprint : fingerprint[..12];

    private static RawRectF Rect(int left, int top, int right, int bottom)
    {
        return new RectangleF(left, top, right - left, bottom - top);
    }

    private static RoundedRectangle Rounded(RawRectF rect, int radius)
    {
        return new RoundedRectangle
        {
            Rect = rect,
            RadiusX = radius,
            RadiusY = radius
        };
    }

    private static Color4 Color(int red, int green, int blue, float alpha = 1.0f)
    {
        return new Color4(red / 255.0f, green / 255.0f, blue / 255.0f, alpha);
    }

    private static int LoWord(IntPtr value)
    {
        return unchecked((short)((long)value & 0xFFFF));
    }

    private static int HiWord(IntPtr value)
    {
        return unchecked((short)(((long)value >> 16) & 0xFFFF));
    }

    private sealed record NativeTaskbarButton(NativeTaskbarGroup Group, Rectangle Bounds);

    private sealed class NativeTaskbarGroup(string key, IReadOnlyList<TaskbarItem> items)
    {
        public string Key { get; } = key;

        public IReadOnlyList<TaskbarItem> Items { get; } = items;

        public bool HasMultiple { get; } = items.Count > 1;

        public bool HasRunning { get; } = items.Any(item => item.Hwnd != IntPtr.Zero);

        public bool IsActive { get; } = items.Any(item => item.Hwnd != IntPtr.Zero && item.IsActive);

        public TaskbarItem Representative { get; } =
            items.FirstOrDefault(item => item.Hwnd != IntPtr.Zero && item.IsActive) ??
            items.FirstOrDefault(item => item.Hwnd != IntPtr.Zero) ??
            items.FirstOrDefault() ??
            throw new InvalidOperationException("Taskbar group must contain at least one item.");
    }

    private sealed record NativeTrayButton(string Key, TrayIconItem Item, Rectangle Bounds);

    private sealed record NativeTrayOverflowButton(Rectangle Bounds);

    private sealed record NativeLayout(
        int StartX,
        int ButtonY,
        int ButtonSize,
        int IconSize,
        int OverlaySize,
        int OverlayInset,
        int UnderlineWidth,
        int UnderlineHeight,
        int CornerRadius);

    private sealed record NativeTrayLayout(
        int StartX,
        int IconStartX,
        int ButtonY,
        int ButtonWidth,
        int ButtonHeight,
        int IconSize,
        int VisibleIconCount,
        bool HasOverflowButton,
        Rectangle OverflowButtonBounds,
        RectangleF ClockBounds,
        int ClockFontSize,
        int CornerRadius);

    private sealed class NativeAppIcon : IDisposable
    {
        private IntPtr _handle;
        private readonly int _bitmapSize;
        private readonly BitmapSource? _sourceBitmap;

        public NativeAppIcon(string fingerprint, IntPtr handle, int bitmapSize)
        {
            Fingerprint = fingerprint;
            _handle = handle;
            _bitmapSize = bitmapSize;
        }

        public NativeAppIcon(string fingerprint, BitmapSource sourceBitmap)
        {
            Fingerprint = fingerprint;
            _sourceBitmap = sourceBitmap;
        }

        public string Fingerprint { get; }

        public ID2D1Bitmap? Bitmap { get; private set; }

        public void EnsureBitmap(ID2D1HwndRenderTarget target)
        {
            if (Bitmap is not null)
            {
                return;
            }

            if (_sourceBitmap is not null)
            {
                Bitmap = CreateDirect2DBitmap(target, _sourceBitmap);
                return;
            }

            if (_handle == IntPtr.Zero)
            {
                return;
            }

            var bitmapSize = _bitmapSize;
            if (bitmapSize <= 0)
            {
                using var icon = Icon.FromHandle(_handle);
                bitmapSize = Math.Max(icon.Width, icon.Height);
            }

            if (CreateIconBitmapSource(_handle, bitmapSize) is { } iconBitmapSource)
            {
                Bitmap = CreateDirect2DBitmap(target, iconBitmapSource);
                return;
            }

            using var image = CreateIconBitmap(_handle, bitmapSize, bitmapSize);
            Bitmap = CreateDirect2DBitmap(target, image);
        }

        public void DisposeBitmap()
        {
            Bitmap?.Dispose();
            Bitmap = null;
        }

        public void Dispose()
        {
            DisposeBitmap();
            if (_handle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    private sealed class NativeOverlayIcon(string fingerprint, BitmapSource sourceBitmap) : IDisposable
    {
        public string Fingerprint { get; } = fingerprint;

        public BitmapSource SourceBitmap { get; } = sourceBitmap;

        public ID2D1Bitmap? Bitmap { get; private set; }

        public void EnsureBitmap(ID2D1HwndRenderTarget target)
        {
            Bitmap ??= CreateDirect2DBitmap(target, SourceBitmap);
        }

        public void DisposeBitmap()
        {
            Bitmap?.Dispose();
            Bitmap = null;
        }

        public void Dispose()
        {
            DisposeBitmap();
        }
    }

    private sealed class NativeTrayIconImage(WpfImageSource source, BitmapSource sourceBitmap) : IDisposable
    {
        public WpfImageSource Source { get; } = source;

        public BitmapSource SourceBitmap { get; } = sourceBitmap;

        public ID2D1Bitmap? Bitmap { get; private set; }

        public void EnsureBitmap(ID2D1HwndRenderTarget target)
        {
            Bitmap ??= CreateDirect2DBitmap(target, SourceBitmap);
        }

        public void DisposeBitmap()
        {
            Bitmap?.Dispose();
            Bitmap = null;
        }

        public void Dispose()
        {
            DisposeBitmap();
        }
    }

    private sealed class NativeExplorerButtonImage(string fingerprint, string buttonName, Image image) : IDisposable
    {
        public string Fingerprint { get; } = fingerprint;

        public string ButtonName { get; } = buttonName;

        public Image Image { get; } = image;

        public ID2D1Bitmap? Bitmap { get; private set; }

        public void EnsureBitmap(ID2D1HwndRenderTarget target)
        {
            Bitmap ??= CreateDirect2DBitmap(target, Image);
        }

        public void DisposeBitmap()
        {
            Bitmap?.Dispose();
            Bitmap = null;
        }

        public void Dispose()
        {
            DisposeBitmap();
            Image.Dispose();
        }
    }
}
