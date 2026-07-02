using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TaskBar2.Native;

namespace TaskBar2.Services;

internal static class FullscreenApplicationDetector
{
    private const int BoundsTolerance = 3;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMilliseconds(350);
    private static readonly object Sync = new();
    private static readonly int CurrentProcessId = Environment.ProcessId;
    private static DateTimeOffset _cachedAt;
    private static bool _cachedActive;
    private static string _cachedDescription = "";
    private static IntPtr _shellFullscreenWindow;

    public static bool IsFullscreenApplicationActive(out string description)
    {
        if (!AppSettingsService.Current.PauseNonClockUpdatesWhileFullscreen)
        {
            description = "";
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        lock (Sync)
        {
            if (now - _cachedAt < CacheDuration)
            {
                description = _cachedDescription;
                return _cachedActive;
            }
        }

        var active = Detect(out var detectedDescription);
        lock (Sync)
        {
            _cachedAt = now;
            _cachedActive = active;
            _cachedDescription = detectedDescription;
            description = _cachedDescription;
            return _cachedActive;
        }
    }

    public static void Invalidate()
    {
        lock (Sync)
        {
            _cachedAt = default;
        }
    }

    public static void NotifyShellFullscreenChanged(IntPtr hwnd, bool entered)
    {
        lock (Sync)
        {
            if (entered)
            {
                _shellFullscreenWindow = hwnd;
            }
            else if (_shellFullscreenWindow == hwnd || hwnd == IntPtr.Zero)
            {
                _shellFullscreenWindow = IntPtr.Zero;
            }

            _cachedAt = default;
        }

        DebugLogger.WriteIfChanged(
            "fullscreen-shell-state",
            $"Shell fullscreen state changed: Active={entered} Hwnd=0x{hwnd.ToInt64():X}");
    }

    private static bool Detect(out string description)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var shellFullscreenWindow = GetShellFullscreenWindow();
        if (shellFullscreenWindow != IntPtr.Zero &&
            IsFullscreenCandidate(shellFullscreenWindow, foreground, allowShellSignal: true, out description))
        {
            return true;
        }

        if (shellFullscreenWindow != IntPtr.Zero)
        {
            ClearShellFullscreenWindow(shellFullscreenWindow);
        }

        var foundDescription = "";
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (IsFullscreenCandidate(hwnd, foreground, allowShellSignal: false, out foundDescription))
            {
                return false;
            }

            return true;
        }, IntPtr.Zero);

        description = foundDescription;
        return foundDescription.Length > 0;
    }

    private static IntPtr GetShellFullscreenWindow()
    {
        lock (Sync)
        {
            return _shellFullscreenWindow;
        }
    }

    private static void ClearShellFullscreenWindow(IntPtr expected)
    {
        lock (Sync)
        {
            if (_shellFullscreenWindow == expected)
            {
                _shellFullscreenWindow = IntPtr.Zero;
            }
        }
    }

    private static bool IsFullscreenCandidate(
        IntPtr hwnd,
        IntPtr foreground,
        bool allowShellSignal,
        out string description)
    {
        description = "";
        if (hwnd == IntPtr.Zero ||
            !NativeMethods.IsWindowVisible(hwnd) ||
            NativeMethods.IsIconic(hwnd))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == CurrentProcessId || processId == 0)
        {
            return false;
        }

        var className = GetClassName(hwnd);
        if (IsIgnoredWindowClass(className) || IsCloaked(hwnd))
        {
            return false;
        }

        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((style & NativeMethods.WS_CHILD) == NativeMethods.WS_CHILD ||
            (exStyle & NativeMethods.WS_EX_TOOLWINDOW) == NativeMethods.WS_EX_TOOLWINDOW)
        {
            return false;
        }

        var hasAppWindowStyle = (exStyle & NativeMethods.WS_EX_APPWINDOW) == NativeMethods.WS_EX_APPWINDOW;
        var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
        if (owner != IntPtr.Zero && !hasAppWindowStyle)
        {
            return false;
        }

        if (!TryGetCoveredMonitor(hwnd, out var windowRect, out var monitorRect))
        {
            return false;
        }

        if (!allowShellSignal && !LooksLikeFullscreenMode(style))
        {
            return false;
        }

        var title = GetWindowTitle(hwnd);
        var processName = GetProcessName(processId);
        description =
            $"Hwnd=0x{hwnd.ToInt64():X} Process={processName} Title=\"{title}\" Class={className} " +
            $"Foreground={hwnd == foreground} Rect={FormatRect(windowRect)} Monitor={FormatRect(monitorRect)} " +
            $"ShellSignal={allowShellSignal}";
        return true;
    }

    private static bool LooksLikeFullscreenMode(long style)
    {
        var hasCaption = (style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION;
        var hasThickFrame = (style & NativeMethods.WS_THICKFRAME) == NativeMethods.WS_THICKFRAME;
        var hasBorder = (style & (NativeMethods.WS_BORDER | NativeMethods.WS_DLGFRAME)) != 0;
        var isPopup = (style & unchecked((long)0x80000000)) != 0;

        return (!hasCaption && !hasThickFrame && !hasBorder) ||
               (isPopup && !hasCaption && !hasThickFrame);
    }

    private static bool TryGetCoveredMonitor(
        IntPtr hwnd,
        out NativeMethods.Rect windowRect,
        out NativeMethods.Rect monitorRect)
    {
        windowRect = default;
        monitorRect = default;
        if (!NativeMethods.GetWindowRect(hwnd, out windowRect))
        {
            return false;
        }

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new NativeMethods.MonitorInfoEx
        {
            cbSize = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
            szDevice = ""
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        monitorRect = monitorInfo.rcMonitor;
        return windowRect.Left <= monitorRect.Left + BoundsTolerance &&
               windowRect.Top <= monitorRect.Top + BoundsTolerance &&
               windowRect.Right >= monitorRect.Right - BoundsTolerance &&
               windowRect.Bottom >= monitorRect.Bottom - BoundsTolerance;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_CLOAKED,
                out var cloaked,
                Marshal.SizeOf<int>()) == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsIgnoredWindowClass(string className) =>
        className is
            "Shell_TrayWnd" or
            "Shell_SecondaryTrayWnd" or
            "Progman" or
            "WorkerW" or
            "TaskBar2.NativeSecondaryTaskbarWindow";

    private static string GetClassName(IntPtr hwnd)
    {
        var className = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        return className.ToString();
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return "";
        }

        var title = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hwnd, title, title.Capacity);
        return title.ToString();
    }

    private static string GetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(unchecked((int)processId));
            return process.ProcessName;
        }
        catch
        {
            return processId.ToString();
        }
    }

    private static string FormatRect(NativeMethods.Rect rect) =>
        $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}";
}
