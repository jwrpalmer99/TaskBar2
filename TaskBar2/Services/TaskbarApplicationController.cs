using Microsoft.Win32;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace TaskBar2.Services;

internal sealed class TaskbarApplicationController : IDisposable
{
    private readonly WindowTracker _windowTracker;
    private readonly List<ISecondaryTaskbarHost> _taskbars = [];
    private readonly AppTrayService _trayService;
    private readonly TrayIconHookServer _trayIconHookServer = new();
    private readonly TrayHookAgentService _trayHookAgentService;
    private SettingsWindow? _settingsWindow;
    private int _hookRestartQueued;

    public TaskbarApplicationController()
    {
        _windowTracker = new WindowTracker(System.Windows.Application.Current.Dispatcher);
        _trayHookAgentService = new TrayHookAgentService(_trayIconHookServer.PipeName);
        _trayService = new AppTrayService(
            () => AppCommands.Refresh(),
            () => System.Windows.Application.Current.Shutdown());
    }

    public void Start()
    {
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        AppCommands.ShowSettingsRequested += OnShowSettingsRequested;
        AppCommands.RefreshRequested += OnRefreshRequested;
        AppCommands.OpenLogRequested += OnOpenLogRequested;
        AppCommands.RestartHooksRequested += OnRestartHooksRequested;
        AppCommands.ExitRequested += OnExitRequested;
        AppSettingsService.SettingsChanged += OnSettingsChanged;
        DebugLogger.Write($"TaskBar2 starting. ProcessId={Environment.ProcessId}");
        _trayIconHookServer.Start();
        _trayHookAgentService.ApplySettings();
        TrayIconSnapshotStore.ReapplyVisibilityFilter();
        _windowTracker.Start();
        RebuildTaskbars();
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        AppCommands.ShowSettingsRequested -= OnShowSettingsRequested;
        AppCommands.RefreshRequested -= OnRefreshRequested;
        AppCommands.OpenLogRequested -= OnOpenLogRequested;
        AppCommands.RestartHooksRequested -= OnRestartHooksRequested;
        AppCommands.ExitRequested -= OnExitRequested;
        AppSettingsService.SettingsChanged -= OnSettingsChanged;
        DebugLogger.Write("TaskBar2 stopping.");
        foreach (var taskbar in _taskbars.ToArray())
        {
            taskbar.Close();
        }
        _taskbars.Clear();
        _trayHookAgentService.Dispose();
        _trayIconHookServer.Dispose();
        _trayService.Dispose();
        _windowTracker.Dispose();
    }

    private void OnShowSettingsRequested(object? sender, EventArgs e)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnRefreshRequested(object? sender, EventArgs e)
    {
        RefreshTaskbars();
        _windowTracker.Refresh();
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Shutdown();
    }

    private void OnOpenLogRequested(object? sender, EventArgs e)
    {
        DebugLogger.OpenLog();
    }

    private void OnRestartHooksRequested(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _hookRestartQueued, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                _trayHookAgentService.ForceRestart();
            }
            finally
            {
                Interlocked.Exchange(ref _hookRestartQueued, 0);
            }
        });
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _trayHookAgentService.ApplySettings();
        TrayIconSnapshotStore.ReapplyVisibilityFilter();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        RebuildTaskbars();
        _windowTracker.Refresh();
    }

    private void RebuildTaskbars()
    {
        foreach (var taskbar in _taskbars.ToArray())
        {
            taskbar.Close();
        }
        _taskbars.Clear();

        foreach (var screen in Screen.AllScreens.Where(screen => !screen.Primary))
        {
            DebugLogger.WriteIfChanged($"screen-{screen.DeviceName}", $"Secondary screen active: {screen.DeviceName} Bounds={screen.Bounds}");
            ISecondaryTaskbarHost taskbar = new NativeSecondaryTaskbarWindow(screen, _windowTracker);
            _taskbars.Add(taskbar);
            taskbar.Show();
        }
    }

    private void RefreshTaskbars()
    {
        foreach (var taskbar in _taskbars)
        {
            taskbar.RefreshPlacement();
        }

        DebugLogger.Write("Taskbars refreshed without rebuilding appbar registrations.");
    }
}
