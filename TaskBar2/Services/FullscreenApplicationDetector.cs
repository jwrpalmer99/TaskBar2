using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TaskBar2.Native;

namespace TaskBar2.Services;

internal static class FullscreenApplicationDetector
{
    private const int BoundsTolerance = 3;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan ProcessNameCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly object Sync = new();
    private static readonly int CurrentProcessId = Environment.ProcessId;
    private static readonly Dictionary<uint, ProcessNameCacheEntry> ProcessNameCache = [];
    private static DateTimeOffset _cachedAt;
    private static DetectionState _cachedState;
    private static IntPtr _shellFullscreenWindow;

    public static bool IsFullscreenApplicationActive(out string description)
    {
        if (!AppSettingsService.Current.PauseNonClockUpdatesWhileFullscreen)
        {
            description = "";
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var state = GetState(now);
        description = state.PauseDescription;
        return state.PauseActive;
    }

    public static bool ShouldTaskbarYieldToForegroundFullscreen(NativeMethods.Rect monitorRect, out string description)
    {
        var state = GetState(DateTimeOffset.UtcNow);
        if (state.ForegroundCoversMonitor &&
            IsSameRect(state.ForegroundMonitorRect, monitorRect))
        {
            description = state.ForegroundDescription;
            return true;
        }

        description = "";
        return false;
    }

    public static TaskbarFullscreenState GetTaskbarState(NativeMethods.Rect monitorRect)
    {
        var state = GetState(DateTimeOffset.UtcNow);
        var shouldPause = AppSettingsService.Current.PauseNonClockUpdatesWhileFullscreen && state.PauseActive;
        var shouldYieldTopmost = state.ForegroundCoversMonitor &&
                                 IsSameRect(state.ForegroundMonitorRect, monitorRect);
        return new TaskbarFullscreenState(
            shouldPause,
            shouldPause ? state.PauseDescription : "",
            shouldYieldTopmost,
            shouldYieldTopmost ? state.ForegroundDescription : "");
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

    private static DetectionState GetState(DateTimeOffset now)
    {
        lock (Sync)
        {
            if (now - _cachedAt < CacheDuration)
            {
                return _cachedState;
            }
        }

        var state = Detect();
        lock (Sync)
        {
            _cachedAt = now;
            _cachedState = state;
            return _cachedState;
        }
    }

    private static DetectionState Detect()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var state = new DetectionState
        {
            PauseDescription = "",
            ForegroundDescription = ""
        };

        var shellFullscreenWindow = GetShellFullscreenWindow();
        if (shellFullscreenWindow != IntPtr.Zero &&
            TryGetFullscreenWindowInfo(shellFullscreenWindow, foreground, allowShellSignal: true, out var shellInfo))
        {
            state.ForegroundCoversMonitor = true;
            state.ForegroundMonitorRect = shellInfo.MonitorRect;
            state.ForegroundDescription = BuildDescription(shellInfo);
            if (IsPauseFullscreenCandidate(shellInfo))
            {
                state.PauseActive = true;
                state.PauseDescription = state.ForegroundDescription;
                return state;
            }
        }

        if (shellFullscreenWindow != IntPtr.Zero)
        {
            ClearShellFullscreenWindow(shellFullscreenWindow);
        }

        if (foreground != IntPtr.Zero &&
            TryGetFullscreenWindowInfo(foreground, foreground, allowShellSignal: false, out var foregroundInfo))
        {
            state.ForegroundCoversMonitor = true;
            state.ForegroundMonitorRect = foregroundInfo.MonitorRect;
            state.ForegroundDescription = BuildDescription(foregroundInfo);
            if (IsPauseFullscreenCandidate(foregroundInfo))
            {
                state.PauseActive = true;
                state.PauseDescription = state.ForegroundDescription;
                return state;
            }
        }

        return state;
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

    private static bool TryGetFullscreenWindowInfo(
        IntPtr hwnd,
        IntPtr foreground,
        bool allowShellSignal,
        out FullscreenWindowInfo info)
    {
        info = default;
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

        var title = GetWindowTitle(hwnd);
        var processName = GetProcessName(processId);
        info = new FullscreenWindowInfo
        {
            Hwnd = hwnd,
            ProcessId = processId,
            ProcessName = processName,
            Title = title,
            ClassName = className,
            Style = style,
            ExStyle = exStyle,
            WindowRect = windowRect,
            MonitorRect = monitorRect,
            IsZoomed = NativeMethods.IsZoomed(hwnd),
            IsForeground = hwnd == foreground,
            ShellSignal = allowShellSignal
        };
        return true;
    }

    private static bool IsPauseFullscreenCandidate(FullscreenWindowInfo info) =>
        !IsKnownNonGameFullscreenProcess(info.ProcessName) &&
        (info.ShellSignal ||
         LooksLikeFullscreenMode(info.Style));

    private static bool LooksLikeFullscreenMode(long style)
    {
        var hasCaption = (style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION;
        var hasThickFrame = (style & NativeMethods.WS_THICKFRAME) == NativeMethods.WS_THICKFRAME;
        var hasBorder = (style & (NativeMethods.WS_BORDER | NativeMethods.WS_DLGFRAME)) != 0;
        var isVisible = (style & NativeMethods.WS_VISIBLE) == NativeMethods.WS_VISIBLE;

        return isVisible && !hasCaption && !hasThickFrame && !hasBorder;
    }

    private static bool IsKnownNonGameFullscreenProcess(string processName)
    {
        return processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("opera", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("vivaldi", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("discord", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("ms-teams", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("teams", StringComparison.OrdinalIgnoreCase);
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
        var now = DateTimeOffset.UtcNow;
        lock (Sync)
        {
            if (ProcessNameCache.TryGetValue(processId, out var cached) &&
                now - cached.CachedAt < ProcessNameCacheDuration)
            {
                return cached.Name;
            }
        }

        var processName = QueryProcessName(processId);
        lock (Sync)
        {
            ProcessNameCache[processId] = new ProcessNameCacheEntry(processName, now);

            if (ProcessNameCache.Count > 32)
            {
                foreach (var staleProcessId in ProcessNameCache
                             .Where(entry => now - entry.Value.CachedAt >= ProcessNameCacheDuration)
                             .Select(entry => entry.Key)
                             .ToArray())
                {
                    ProcessNameCache.Remove(staleProcessId);
                }
            }
        }

        return processName;
    }

    private static string QueryProcessName(uint processId)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            processId);
        if (handle == IntPtr.Zero)
        {
            return processId.ToString();
        }

        try
        {
            var builder = new StringBuilder(1024);
            var length = builder.Capacity;
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, builder, ref length) || length <= 0)
            {
                return processId.ToString();
            }

            var fileName = Path.GetFileNameWithoutExtension(builder.ToString(0, length));
            return string.IsNullOrWhiteSpace(fileName)
                ? processId.ToString()
                : fileName;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static bool IsSameRect(NativeMethods.Rect left, NativeMethods.Rect right) =>
        Math.Abs(left.Left - right.Left) <= BoundsTolerance &&
        Math.Abs(left.Top - right.Top) <= BoundsTolerance &&
        Math.Abs(left.Right - right.Right) <= BoundsTolerance &&
        Math.Abs(left.Bottom - right.Bottom) <= BoundsTolerance;

    private static string BuildDescription(FullscreenWindowInfo info) =>
        $"Hwnd=0x{info.Hwnd.ToInt64():X} Process={info.ProcessName} Title=\"{info.Title}\" Class={info.ClassName} " +
        $"Foreground={info.IsForeground} Rect={FormatRect(info.WindowRect)} Monitor={FormatRect(info.MonitorRect)} " +
        $"Style=0x{info.Style:X} ExStyle=0x{info.ExStyle:X} ShellSignal={info.ShellSignal}";

    private static string FormatRect(NativeMethods.Rect rect) =>
        $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}";

    private struct DetectionState
    {
        public bool PauseActive;
        public string PauseDescription;
        public bool ForegroundCoversMonitor;
        public NativeMethods.Rect ForegroundMonitorRect;
        public string ForegroundDescription;
    }

    public readonly record struct TaskbarFullscreenState(
        bool ShouldPauseNonClockUpdates,
        string PauseDescription,
        bool ShouldYieldTopmost,
        string YieldDescription);

    private readonly record struct ProcessNameCacheEntry(string Name, DateTimeOffset CachedAt);

    private struct FullscreenWindowInfo
    {
        public IntPtr Hwnd;
        public uint ProcessId;
        public string ProcessName;
        public string Title;
        public string ClassName;
        public long Style;
        public long ExStyle;
        public NativeMethods.Rect WindowRect;
        public NativeMethods.Rect MonitorRect;
        public bool IsZoomed;
        public bool IsForeground;
        public bool ShellSignal;
    }
}
