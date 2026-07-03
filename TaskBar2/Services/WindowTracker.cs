using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using TaskBar2.Models;
using TaskBar2.Native;

namespace TaskBar2.Services;

public sealed class WindowTracker : IDisposable
{
    private static readonly TimeSpan IconFingerprintRefreshInterval = TimeSpan.FromSeconds(10);
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly NativeMethods.WinEventDelegate _winEventDelegate;
    private readonly List<IntPtr> _hooks = [];
    private readonly Dictionary<IntPtr, WindowCacheEntry> _windowCache = [];
    private readonly Dictionary<uint, ProcessCacheEntry> _processCache = [];
    private readonly int _currentProcessId = Environment.ProcessId;
    private static readonly Guid IidPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly NativeMethods.PropertyKey AppUserModelIdKey = new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        PropertyId = 5
    };
    private WindowSnapshotKey[] _lastSnapshot = [];
    private bool _refreshQueued;
    private bool _fullscreenPauseActive;
    private bool _externallyPaused;

    public WindowTracker(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _winEventDelegate = OnWinEvent;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(AppSettingsService.Current.TaskbarPollingIntervalMs)
        };
        _refreshTimer.Tick += (_, _) => Refresh();
        AppSettingsService.SettingsChanged += OnSettingsChanged;
    }

    public event EventHandler<IReadOnlyList<TaskbarItem>>? WindowsChanged;

    public ReadOnlyCollection<TaskbarItem> CurrentWindows { get; private set; } = new(Array.Empty<TaskbarItem>());

    public bool IsPaused => _fullscreenPauseActive || _externallyPaused;

    public void Start()
    {
        Hook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND);
        Hook(NativeMethods.EVENT_OBJECT_CREATE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE);
        Hook(NativeMethods.EVENT_OBJECT_NAMECHANGE, NativeMethods.EVENT_OBJECT_NAMECHANGE);
        _refreshTimer.Start();
        Refresh();
    }

    public void Refresh()
    {
        _refreshQueued = false;
        if (_externallyPaused)
        {
            return;
        }

        if (FullscreenApplicationDetector.IsFullscreenApplicationActive(out var fullscreenDescription))
        {
            if (!_fullscreenPauseActive)
            {
                _fullscreenPauseActive = true;
                DebugLogger.WriteIfChanged(
                    "window-tracker-fullscreen-pause",
                    $"Window tracker refresh paused during fullscreen app: {fullscreenDescription}");
            }

            return;
        }

        if (_fullscreenPauseActive)
        {
            _fullscreenPauseActive = false;
            DebugLogger.Write("Window tracker refresh resumed after fullscreen app.");
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var windows = new List<TaskbarItem>();
        var includedHandles = new HashSet<IntPtr>();
        var includedProcessIds = new HashSet<uint>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (TryCreateTaskbarItem(hwnd, foreground, out var item))
            {
                includedHandles.Add(hwnd);
                includedProcessIds.Add(item.ProcessId);
                windows.Add(item);
            }

            return true;
        }, IntPtr.Zero);

        PruneWindowCache(includedHandles);
        PruneProcessCache(includedProcessIds);
        var orderedWindows = windows.ToArray();
        var snapshot = BuildSnapshot(orderedWindows);
        if (snapshot.SequenceEqual(_lastSnapshot))
        {
            return;
        }

        _lastSnapshot = snapshot;
        CurrentWindows = new ReadOnlyCollection<TaskbarItem>(orderedWindows);
        WindowsChanged?.Invoke(this, CurrentWindows);
    }

    public void SetNonClockUpdatesPaused(bool paused)
    {
        if (_externallyPaused == paused)
        {
            return;
        }

        _externallyPaused = paused;
        if (paused)
        {
            _refreshQueued = false;
            _refreshTimer.Stop();
            return;
        }

        _fullscreenPauseActive = false;
        _refreshTimer.Start();
        QueueRefresh();
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        AppSettingsService.SettingsChanged -= OnSettingsChanged;
        foreach (var hook in _hooks)
        {
            NativeMethods.UnhookWinEvent(hook);
        }
        _hooks.Clear();
        _windowCache.Clear();
        _processCache.Clear();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(AppSettingsService.Current.TaskbarPollingIntervalMs);
        FullscreenApplicationDetector.Invalidate();
        if (!AppSettingsService.Current.PauseNonClockUpdatesWhileFullscreen)
        {
            SetNonClockUpdatesPaused(paused: false);
        }

        QueueRefresh();
    }

    private void Hook(uint minEvent, uint maxEvent)
    {
        var hook = NativeMethods.SetWinEventHook(
            minEvent,
            maxEvent,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        if (hook != IntPtr.Zero)
        {
            _hooks.Add(hook);
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (eventType != NativeMethods.EVENT_SYSTEM_FOREGROUND &&
            (idObject != NativeMethods.OBJID_WINDOW || idChild != NativeMethods.CHILDID_SELF))
        {
            return;
        }

        if (_externallyPaused || _fullscreenPauseActive)
        {
            return;
        }

        InvalidateCachedMetadata(eventType, hwnd);
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_externallyPaused || _fullscreenPauseActive || _refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        _dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
    }

    private bool TryCreateTaskbarItem(IntPtr hwnd, IntPtr foreground, out TaskbarItem item)
    {
        item = default!;

        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == _currentProcessId)
        {
            return false;
        }

        var cache = GetCacheEntry(hwnd);
        if (IsCloaked(hwnd) || IsShellWindow(hwnd, cache))
        {
            return false;
        }

        var title = GetWindowTitle(hwnd, cache);
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var hasAppWindowStyle = (exStyle & NativeMethods.WS_EX_APPWINDOW) == NativeMethods.WS_EX_APPWINDOW;
        var isToolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) == NativeMethods.WS_EX_TOOLWINDOW;
        var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);

        if (isToolWindow || (owner != IntPtr.Zero && !hasAppWindowStyle))
        {
            return false;
        }

        var processIdentity = GetProcessIdentity(processId);
        var appUserModelId = GetAppUserModelId(hwnd, cache);
        var packageInfo = PackageAppResolver.Resolve(appUserModelId);
        var iconFingerprint = GetIconFingerprint(hwnd, cache, processIdentity, packageInfo);
        item = new TaskbarItem(
            hwnd,
            title,
            GetWindowIcon(hwnd, cache, iconFingerprint, processIdentity, packageInfo),
            iconFingerprint,
            hwnd == foreground,
            NativeMethods.IsIconic(hwnd),
            GetMonitorDeviceName(hwnd, cache),
            processId,
            processIdentity.Name,
            processIdentity.Path,
            appUserModelId,
            GetGroupKey(hwnd, cache, processIdentity),
            packageInfo?.IconPath ?? processIdentity.Path);
        return true;
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

    private static bool IsShellWindow(IntPtr hwnd, WindowCacheEntry cache)
    {
        if (cache.ShellClassLoaded)
        {
            return cache.IsShellWindow;
        }

        var className = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);

        cache.IsShellWindow = className.ToString() is
            "Shell_TrayWnd" or
            "Shell_SecondaryTrayWnd" or
            "Progman" or
            "WorkerW";
        cache.ShellClassLoaded = true;
        return cache.IsShellWindow;
    }

    private static string GetWindowTitle(IntPtr hwnd, WindowCacheEntry cache)
    {
        if (cache.TitleLoaded)
        {
            return cache.Title;
        }

        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length == 0)
        {
            cache.Title = "";
            cache.TitleLoaded = true;
            return "";
        }

        var title = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hwnd, title, title.Capacity);
        cache.Title = title.ToString();
        cache.TitleLoaded = true;
        return cache.Title;
    }

    private string GetIconFingerprint(
        IntPtr hwnd,
        WindowCacheEntry cache,
        ProcessCacheEntry processIdentity,
        PackageAppInfo? packageInfo)
    {
        var now = DateTimeOffset.UtcNow;
        if (cache.IconFingerprintLoaded && now < cache.NextIconFingerprintRefresh)
        {
            return cache.IconFingerprint;
        }

        var packageIconFingerprint = PackageAppResolver.GetFileFingerprint(packageInfo?.IconPath);
        cache.IconFingerprint = !string.IsNullOrWhiteSpace(packageIconFingerprint)
            ? $"package:{packageIconFingerprint}"
            : WindowIconProvider.GetIconFingerprint(hwnd, processIdentity.Path);
        cache.IconFingerprintLoaded = true;
        cache.NextIconFingerprintRefresh = now + IconFingerprintRefreshInterval;
        return cache.IconFingerprint;
    }

    private ImageSource? GetWindowIcon(
        IntPtr hwnd,
        WindowCacheEntry cache,
        string iconFingerprint,
        ProcessCacheEntry processIdentity,
        PackageAppInfo? packageInfo)
    {
        if (cache.IconLoaded && string.Equals(cache.IconImageFingerprint, iconFingerprint, StringComparison.Ordinal))
        {
            return cache.Icon;
        }

        var packageIcon = PackageAppResolver.CreateImageSource(packageInfo?.IconPath);
        if (packageIcon is not null)
        {
            cache.Icon = packageIcon;
            cache.IconLoaded = true;
            cache.IconImageFingerprint = iconFingerprint;
            return cache.Icon;
        }

        return cache.Icon;
    }

    private static string GetMonitorDeviceName(IntPtr hwnd, WindowCacheEntry cache)
    {
        if (cache.MonitorDeviceNameLoaded)
        {
            return cache.MonitorDeviceName;
        }

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            cache.MonitorDeviceName = "";
            cache.MonitorDeviceNameLoaded = true;
            return "";
        }

        var info = new NativeMethods.MonitorInfoEx
        {
            cbSize = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
            szDevice = ""
        };

        cache.MonitorDeviceName = NativeMethods.GetMonitorInfo(monitor, ref info)
            ? info.szDevice
            : "";
        cache.MonitorDeviceNameLoaded = true;
        return cache.MonitorDeviceName;
    }

    private WindowCacheEntry GetCacheEntry(IntPtr hwnd)
    {
        if (_windowCache.TryGetValue(hwnd, out var cached))
        {
            return cached;
        }

        cached = new WindowCacheEntry();
        _windowCache[hwnd] = cached;
        return cached;
    }

    private ProcessCacheEntry GetProcessIdentity(uint processId)
    {
        if (_processCache.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        var path = GetProcessPath(processId);
        var name = string.IsNullOrWhiteSpace(path)
            ? ""
            : Path.GetFileNameWithoutExtension(path);

        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                using var process = Process.GetProcessById(unchecked((int)processId));
                name = process.ProcessName;
            }
            catch
            {
            }
        }

        var groupKey = !string.IsNullOrWhiteSpace(path)
            ? "path:" + path.ToUpperInvariant()
            : "process:" + (string.IsNullOrWhiteSpace(name) ? processId.ToString() : name.ToUpperInvariant());
        cached = new ProcessCacheEntry(name, path, groupKey);
        _processCache[processId] = cached;
        return cached;
    }

    private static string GetProcessPath(uint processId)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            processId);
        if (handle == IntPtr.Zero)
        {
            return "";
        }

        try
        {
            var builder = new StringBuilder(1024);
            var length = builder.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, builder, ref length)
                ? builder.ToString()
                : "";
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static string GetAppUserModelId(IntPtr hwnd, WindowCacheEntry cache)
    {
        if (cache.AppUserModelIdLoaded)
        {
            return cache.AppUserModelId;
        }

        NativeMethods.IPropertyStore? propertyStore = null;
        var iid = IidPropertyStore;
        var key = AppUserModelIdKey;
        try
        {
            if (NativeMethods.SHGetPropertyStoreForWindow(hwnd, ref iid, out propertyStore) == 0 &&
                propertyStore.GetValue(ref key, out var value) == 0)
            {
                try
                {
                    cache.AppUserModelId = value.GetString() ?? "";
                }
                finally
                {
                    NativeMethods.PropVariantClear(ref value);
                }
            }
        }
        catch
        {
            cache.AppUserModelId = "";
        }
        finally
        {
            if (propertyStore is not null)
            {
                Marshal.ReleaseComObject(propertyStore);
            }
        }

        cache.AppUserModelIdLoaded = true;
        return cache.AppUserModelId;
    }

    private static string GetGroupKey(IntPtr hwnd, WindowCacheEntry cache, ProcessCacheEntry processIdentity)
    {
        var appUserModelId = GetAppUserModelId(hwnd, cache);
        return !string.IsNullOrWhiteSpace(appUserModelId)
            ? "appid:" + appUserModelId.ToUpperInvariant()
            : processIdentity.GroupKey;
    }

    private void InvalidateCachedMetadata(uint eventType, IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !_windowCache.TryGetValue(hwnd, out var cached))
        {
            return;
        }

        switch (eventType)
        {
            case NativeMethods.EVENT_OBJECT_CREATE:
                _windowCache.Remove(hwnd);
                break;
            case NativeMethods.EVENT_OBJECT_NAMECHANGE:
                cached.TitleLoaded = false;
                break;
            case NativeMethods.EVENT_OBJECT_LOCATIONCHANGE:
                cached.MonitorDeviceNameLoaded = false;
                break;
        }
    }

    private void PruneWindowCache(HashSet<IntPtr> visibleHandles)
    {
        foreach (var hwnd in _windowCache.Keys.Where(hwnd => !visibleHandles.Contains(hwnd)).ToArray())
        {
            _windowCache.Remove(hwnd);
        }
    }

    private void PruneProcessCache(HashSet<uint> visibleProcessIds)
    {
        foreach (var processId in _processCache.Keys.Where(processId => !visibleProcessIds.Contains(processId)).ToArray())
        {
            _processCache.Remove(processId);
        }
    }

    private static WindowSnapshotKey[] BuildSnapshot(IReadOnlyList<TaskbarItem> windows)
    {
        var snapshot = new WindowSnapshotKey[windows.Count];
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            snapshot[index] = new WindowSnapshotKey(
                window.Hwnd,
                window.Title,
                window.IsActive,
                window.IsMinimized,
                window.MonitorDeviceName,
                window.IconFingerprint,
                window.GroupKey);
        }

        return snapshot;
    }

    private sealed class WindowCacheEntry
    {
        public string Title { get; set; } = "";

        public bool TitleLoaded { get; set; }

        public string MonitorDeviceName { get; set; } = "";

        public bool MonitorDeviceNameLoaded { get; set; }

        public bool IsShellWindow { get; set; }

        public bool ShellClassLoaded { get; set; }

        public string AppUserModelId { get; set; } = "";

        public bool AppUserModelIdLoaded { get; set; }

        public ImageSource? Icon { get; set; }

        public bool IconLoaded { get; set; }

        public string IconFingerprint { get; set; } = "";

        public bool IconFingerprintLoaded { get; set; }

        public DateTimeOffset NextIconFingerprintRefresh { get; set; }

        public string IconImageFingerprint { get; set; } = "";
    }

    private sealed record ProcessCacheEntry(string Name, string Path, string GroupKey);

    private readonly record struct WindowSnapshotKey(
        IntPtr Hwnd,
        string Title,
        bool IsActive,
        bool IsMinimized,
        string MonitorDeviceName,
        string IconFingerprint,
        string GroupKey);
}
