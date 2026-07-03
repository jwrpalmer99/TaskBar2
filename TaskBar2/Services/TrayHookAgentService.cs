using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace TaskBar2.Services;

internal sealed class TrayHookAgentService : IDisposable
{
    private static readonly TimeSpan GlobalStopPulseDuration = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan AgentStopTimeout = TimeSpan.FromSeconds(10);

    private readonly string _pipeName;
    private readonly string _globalStopEventName;
    private readonly EventWaitHandle _globalStopEvent;
    private readonly AgentProcess _agent;
    private readonly AgentProcess _standardAgent;
    private readonly AgentProcess _explorerAgent;
    private bool _disposed;

    public TrayHookAgentService(string pipeName)
    {
        _pipeName = pipeName;
        _globalStopEventName = $"Local\\TaskBar2.TrayHook.StopAll.{Process.GetCurrentProcess().SessionId}";
        _globalStopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, _globalStopEventName, out var createdNew);
        if (!createdNew)
        {
            ResetStaleInjectedHookStopSignal();
        }
        StopOwnedAgentProcesses();
        StopOwnedEasyHookServices();

        _agent = new AgentProcess(_pipeName, _globalStopEventName, "agent");
        _standardAgent = new AgentProcess(_pipeName, _globalStopEventName, "standard");
        _explorerAgent = new AgentProcess(_pipeName, _globalStopEventName, "explorer");
    }

    public void ApplySettings()
    {
        if (_disposed)
        {
            return;
        }

        var enableTrayIconHook =
            ShouldMirrorNotificationAreaOnAnySecondaryMonitor() &&
            AppSettingsService.Current.EnableInvasiveTrayIconHook;
        var enableExplorerTaskbarHook = AppSettingsService.Current.EnableExperimentalExplorerTaskbarHook;
        var enableExplorerTaskbarButtonImageCapture =
            AppSettingsService.Current.EnableExperimentalExplorerTaskbarButtonImageCapture;

        if (enableTrayIconHook || enableExplorerTaskbarHook)
        {
            var runElevated = AppSettingsService.Current.EnableElevatedTrayIconHookAgent;
            var showAllTrayIcons = AppSettingsService.Current.ShowAllTrayIcons;
            var splitElevatedTrayAgent = enableTrayIconHook && runElevated;
            var startMainAgent = enableTrayIconHook || (!splitElevatedTrayAgent && enableExplorerTaskbarHook);
            var startStandardAgent = splitElevatedTrayAgent;
            var startExplorerAgent = false;
            var mainTargetMode = splitElevatedTrayAgent ? "ElevatedOnly" : "StandardOnly";
            var mainElevated = splitElevatedTrayAgent;
            var mainEnableTray = enableTrayIconHook;
            var mainEnableExplorer = enableExplorerTaskbarHook && !mainElevated;
            var mainShowAllTrayIcons = mainEnableTray && showAllTrayIcons;
            var standardEnableTray = enableTrayIconHook;
            var standardEnableExplorer = enableExplorerTaskbarHook;
            var standardShowAllTrayIcons = standardEnableTray && showAllTrayIcons;

            var needsRestart =
                (startMainAgent
                    ? _agent.RequiresRestart(
                        mainTargetMode,
                        mainElevated,
                        mainShowAllTrayIcons,
                        mainEnableTray,
                        mainEnableExplorer,
                        mainEnableExplorer && enableExplorerTaskbarButtonImageCapture)
                    : _agent.IsRunning) ||
                (startStandardAgent
                    ? _standardAgent.RequiresRestart(
                        "StandardOnly",
                        elevated: false,
                        standardShowAllTrayIcons,
                        standardEnableTray,
                        standardEnableExplorer,
                        standardEnableExplorer && enableExplorerTaskbarButtonImageCapture)
                    : _standardAgent.IsRunning) ||
                (startExplorerAgent
                    ? _explorerAgent.RequiresRestart("StandardOnly", elevated: false, showAllTrayIcons: false, enableTrayIconHook: false, enableExplorerTaskbarHook: true, enableExplorerTaskbarButtonImageCapture)
                    : _explorerAgent.IsRunning);

            if (needsRestart)
            {
                Stop(signalInjectedHooks: false);
            }
            else
            {
                ResetGlobalStopEvent();
            }

            if (startMainAgent)
            {
                _agent.Start(
                    mainTargetMode,
                    mainElevated,
                    mainShowAllTrayIcons,
                    mainEnableTray,
                    mainEnableExplorer,
                    mainEnableExplorer && enableExplorerTaskbarButtonImageCapture);
            }

            if (startStandardAgent)
            {
                _standardAgent.Start(
                    "StandardOnly",
                    elevated: false,
                    standardShowAllTrayIcons,
                    standardEnableTray,
                    standardEnableExplorer,
                    standardEnableExplorer && enableExplorerTaskbarButtonImageCapture);
            }

            if (startExplorerAgent)
            {
                _explorerAgent.Start(
                    "StandardOnly",
                    elevated: false,
                    showAllTrayIcons: false,
                    enableTrayIconHook: false,
                    enableExplorerTaskbarHook: true,
                    enableExplorerTaskbarButtonImageCapture);
            }
        }
        else
        {
            Stop(signalInjectedHooks: false);
        }
    }

    public void ForceRestart()
    {
        if (_disposed)
        {
            return;
        }

        DebugLogger.Write("Tray hook agent force restart requested.");
        Stop(signalInjectedHooks: false);
        ApplySettings();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop(signalInjectedHooks: false);
        _globalStopEvent.Dispose();
    }

    private void Stop(bool signalInjectedHooks)
    {
        var signaledAt = signalInjectedHooks
            ? SignalGlobalStopEvent()
            : DateTimeOffset.UtcNow;
        _agent.SignalStop();
        _standardAgent.SignalStop();
        _explorerAgent.SignalStop();
        if (signalInjectedHooks)
        {
            WaitForGlobalStopPulse(signaledAt);
        }

        _agent.Stop();
        _standardAgent.Stop();
        _explorerAgent.Stop();
        StopOwnedAgentProcesses();
        StopOwnedEasyHookServices();

        if (signalInjectedHooks)
        {
            ResetGlobalStopEvent();
        }
    }

    private void ResetStaleInjectedHookStopSignal()
    {
        DebugLogger.Write($"Tray hook global stop event already exists; resetting without unloading resident hooks. Event={_globalStopEventName}");
        ResetGlobalStopEvent();
    }

    private DateTimeOffset SignalGlobalStopEvent()
    {
        try
        {
            _globalStopEvent.Set();
        }
        catch (ObjectDisposedException)
        {
        }

        return DateTimeOffset.UtcNow;
    }

    private void ResetGlobalStopEvent()
    {
        try
        {
            _globalStopEvent.Reset();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void WaitForGlobalStopPulse(DateTimeOffset signaledAt)
    {
        var elapsed = DateTimeOffset.UtcNow - signaledAt;
        var remaining = GlobalStopPulseDuration - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            Thread.Sleep(remaining);
        }
    }

    private static void StopOwnedEasyHookServices()
    {
        var agentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TrayHookAgent"));
        StopOwnedProcessesByName("EasyHook32Svc", agentRoot, "stale EasyHook service helper");
        StopOwnedProcessesByName("EasyHook64Svc", agentRoot, "stale EasyHook service helper");
    }

    private static void StopOwnedAgentProcesses()
    {
        var agentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TrayHookAgent"));
        StopOwnedProcessesByName("TaskBar2.TrayHook.Agent", agentRoot, "stale tray hook agent");
    }

    private static void StopOwnedProcessesByName(string processName, string agentRoot, string description)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                string? processPath = null;
                try
                {
                    processPath = process.MainModule?.FileName;
                }
                catch (Exception exception)
                {
                    DebugLogger.WriteIfChanged(
                        $"owned-process-path-{process.Id}",
                        $"Could not inspect {processName} path. ProcessId={process.Id} Error={exception.GetType().Name}: {exception.Message}");
                }

                if (string.IsNullOrWhiteSpace(processPath) || !IsPathUnder(processPath, agentRoot))
                {
                    continue;
                }

                try
                {
                    DebugLogger.Write($"Stopping {description}. ProcessId={process.Id} Path={processPath}");
                    process.Kill();
                    process.WaitForExit((int)TimeSpan.FromSeconds(3).TotalMilliseconds);
                }
                catch (Exception exception)
                {
                    DebugLogger.WriteIfChanged(
                        $"owned-process-stop-{process.Id}",
                        $"Could not stop {description}. ProcessId={process.Id} Path={processPath} Error={exception.GetType().Name}: {exception.Message}");
                }
            }
        }
    }

    private static bool IsPathUnder(string path, string root)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldMirrorNotificationAreaOnAnySecondaryMonitor()
    {
        var secondaryMonitorDeviceNames = System.Windows.Forms.Screen.AllScreens
            .Where(screen => !screen.Primary)
            .Select(screen => screen.DeviceName);
        return AppSettingsService.ShouldMirrorNotificationAreaOnAnyMonitor(secondaryMonitorDeviceNames);
    }

    private sealed class AgentProcess
    {
        private readonly string _pipeName;
        private readonly string _globalStopEventName;
        private Process? _process;
        private EventWaitHandle? _stopEvent;
        private EventWaitHandle? _injecteeStopEvent;
        private string? _stopEventName;
        private string? _injecteeStopEventName;
        private string _targetMode = "StandardOnly";
        private bool _elevated;
        private bool _showAllTrayIcons;
        private bool _enableTrayIconHook;
        private bool _enableExplorerTaskbarHook;
        private bool _enableExplorerTaskbarButtonImageCapture;
        private bool _startedWithoutHandle;
        private readonly string _name;

        public AgentProcess(string pipeName, string globalStopEventName, string name)
        {
            _pipeName = pipeName;
            _globalStopEventName = globalStopEventName;
            _name = name;
        }

        public bool IsRunning => _process is { HasExited: false } || _startedWithoutHandle;

        public bool RequiresRestart(
            string targetMode,
            bool elevated,
            bool showAllTrayIcons,
            bool enableTrayIconHook,
            bool enableExplorerTaskbarHook,
            bool enableExplorerTaskbarButtonImageCapture = false) =>
            IsRunning &&
            (!string.Equals(_targetMode, targetMode, StringComparison.OrdinalIgnoreCase) ||
             _elevated != elevated ||
             _showAllTrayIcons != showAllTrayIcons ||
             _enableTrayIconHook != enableTrayIconHook ||
             _enableExplorerTaskbarHook != enableExplorerTaskbarHook ||
             _enableExplorerTaskbarButtonImageCapture != enableExplorerTaskbarButtonImageCapture);

        public void Start(
            string targetMode,
            bool elevated,
            bool showAllTrayIcons,
            bool enableTrayIconHook,
            bool enableExplorerTaskbarHook,
            bool enableExplorerTaskbarButtonImageCapture = false)
        {
            if (IsRunning &&
                string.Equals(_targetMode, targetMode, StringComparison.OrdinalIgnoreCase) &&
                _elevated == elevated &&
                _showAllTrayIcons == showAllTrayIcons &&
                _enableTrayIconHook == enableTrayIconHook &&
                _enableExplorerTaskbarHook == enableExplorerTaskbarHook &&
                _enableExplorerTaskbarButtonImageCapture == enableExplorerTaskbarButtonImageCapture)
            {
                return;
            }

            Stop();
            _targetMode = targetMode;
            _elevated = elevated;
            _showAllTrayIcons = showAllTrayIcons;
            _enableTrayIconHook = enableTrayIconHook;
            _enableExplorerTaskbarHook = enableExplorerTaskbarHook;
            _enableExplorerTaskbarButtonImageCapture = enableExplorerTaskbarButtonImageCapture;

            var agentPath = ResolveAgentPath();
            if (agentPath is null)
            {
                DebugLogger.WriteIfChanged(
                    "tray-hook-agent-missing",
                    "Tray hook agent is enabled but TaskBar2.TrayHook.Agent.exe was not found.");
                return;
            }

            _stopEventName = $"Local\\TaskBar2.TrayHook.Stop.{Environment.ProcessId}.{_name}";
            _injecteeStopEventName = $"Local\\TaskBar2.TrayHook.InjecteeStop.{Environment.ProcessId}.{_name}";
            _stopEvent?.Dispose();
            _injecteeStopEvent?.Dispose();
            _stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, _stopEventName);
            _injecteeStopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, _injecteeStopEventName);

            var arguments = CreateArguments();

            try
            {
                if (!_elevated && IsCurrentProcessElevated() && TryStartUnelevatedViaExplorer(agentPath, arguments))
                {
                    _process = null;
                    _startedWithoutHandle = true;
                    DebugLogger.Write($"Tray hook agent started through Explorer shell. Name={_name} Path={agentPath} Elevated=False TargetMode={_targetMode} ShowAllTrayIcons={_showAllTrayIcons} EnableTrayIconHook={_enableTrayIconHook} EnableExplorerTaskbarHook={_enableExplorerTaskbarHook} EnableExplorerTaskbarButtonImageCapture={_enableExplorerTaskbarButtonImageCapture}");
                    return;
                }

                var startInfo = CreateStartInfo(agentPath, arguments);
                _process = Process.Start(startInfo);
                _startedWithoutHandle = false;
                DebugLogger.Write($"Tray hook agent started. Name={_name} Path={agentPath} ProcessId={_process?.Id} Elevated={_elevated} TargetMode={_targetMode} ShowAllTrayIcons={_showAllTrayIcons} EnableTrayIconHook={_enableTrayIconHook} EnableExplorerTaskbarHook={_enableExplorerTaskbarHook} EnableExplorerTaskbarButtonImageCapture={_enableExplorerTaskbarButtonImageCapture}");
            }
            catch (Exception exception)
            {
                DebugLogger.WriteIfChanged(
                    "tray-hook-agent-start-error",
                    $"Tray hook agent failed to start: {exception.GetType().Name}: {exception.Message}");
                _process = null;
            }
        }

        public void Stop()
        {
            SignalStop();

            if (_process is { HasExited: false })
            {
                if (!WaitForExit(_process, AgentStopTimeout))
                {
                    try
                    {
                        DebugLogger.Write($"Tray hook agent did not stop within {AgentStopTimeout.TotalSeconds:0.#}s; killing. Name={_name} ProcessId={_process.Id}");
                        _process.Kill();
                    }
                    catch (Exception exception)
                    {
                        DebugLogger.WriteIfChanged(
                            "tray-hook-agent-kill-error",
                            $"Tray hook agent kill failed: {exception.GetType().Name}: {exception.Message}");
                    }
                }
            }

            _process?.Dispose();
            _process = null;
            _startedWithoutHandle = false;
            _stopEvent?.Dispose();
            _stopEvent = null;
            _injecteeStopEvent?.Dispose();
            _injecteeStopEvent = null;
        }

        public void SignalStop()
        {
            try
            {
                _stopEvent?.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private string[] CreateArguments()
        {
            return
            [
                "--pipe", _pipeName,
                "--agentName", _name,
                "--stopEvent", _stopEventName ?? "",
                "--injecteeStopEvent", _injecteeStopEventName ?? "",
                "--globalStopEvent", _globalStopEventName,
                "--interval", "1000",
                "--targetMode", _targetMode,
                "--showAllTrayIcons", _showAllTrayIcons ? "true" : "false",
                "--enableTrayIconHook", _enableTrayIconHook ? "true" : "false",
                "--enableExplorerTaskbarHook", _enableExplorerTaskbarHook ? "true" : "false",
                "--enableExplorerTaskbarButtonImageCapture", _enableExplorerTaskbarButtonImageCapture ? "true" : "false"
            ];
        }

        private ProcessStartInfo CreateStartInfo(string agentPath, string[] arguments)
        {
            if (_elevated)
            {
                return new ProcessStartInfo(agentPath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(agentPath) ?? AppContext.BaseDirectory
                };
            }

            var startInfo = new ProcessStartInfo(agentPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(agentPath) ?? AppContext.BaseDirectory
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return startInfo;
        }

        private static bool TryStartUnelevatedViaExplorer(string agentPath, string[] arguments)
        {
            object? shell = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is null)
                {
                    return false;
                }

                shell = Activator.CreateInstance(shellType);
                if (shell is null)
                {
                    return false;
                }

                shellType.InvokeMember(
                    "ShellExecute",
                    BindingFlags.InvokeMethod,
                    binder: null,
                    target: shell,
                    args:
                    [
                        agentPath,
                        string.Join(" ", arguments.Select(QuoteArgument)),
                        Path.GetDirectoryName(agentPath) ?? AppContext.BaseDirectory,
                        "open",
                        0
                    ]);
                return true;
            }
            catch (Exception exception)
            {
                DebugLogger.WriteIfChanged(
                    "tray-hook-agent-unelevated-start-error",
                    $"Tray hook agent unelevated Explorer-shell start failed: {exception.GetType().Name}: {exception.Message}");
                return false;
            }
            finally
            {
                if (shell is not null && Marshal.IsComObject(shell))
                {
                    try
                    {
                        Marshal.FinalReleaseComObject(shell);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool IsCurrentProcessElevated()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static bool WaitForExit(Process process, TimeSpan timeout)
        {
            try
            {
                return process.WaitForExit((int)timeout.TotalMilliseconds);
            }
            catch
            {
                return true;
            }
        }
    }

    private static string? ResolveAgentPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var agentRoot = Path.Combine(baseDirectory, "TrayHookAgent");
        var versionedCandidates = Directory.Exists(agentRoot)
            ? Directory.EnumerateDirectories(agentRoot)
                .Select(directory => new
                {
                    Path = Path.Combine(directory, "TaskBar2.TrayHook.Agent.exe"),
                    LastWriteTimeUtc = Directory.GetLastWriteTimeUtc(directory)
                })
                .Where(candidate => File.Exists(candidate.Path))
                .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
                .Select(candidate => candidate.Path)
                .ToArray()
            : [];
        var fixedCandidates = new[]
        {
            Path.Combine(baseDirectory, "TrayHookAgent", "Current", "TaskBar2.TrayHook.Agent.exe"),
            Path.Combine(baseDirectory, "TrayHookAgent", "TaskBar2.TrayHook.Agent.exe"),
            Path.Combine(baseDirectory, "TaskBar2.TrayHook.Agent.exe"),
            Path.GetFullPath(Path.Combine(
                baseDirectory,
                "..",
                "..",
                "..",
                "..",
                "TrayHook",
                "TaskBar2.TrayHook.Agent",
                "bin",
                "Debug",
                "net48",
                "TaskBar2.TrayHook.Agent.exe"))
        };

        return versionedCandidates
            .Concat(fixedCandidates)
            .Where(File.Exists)
            .FirstOrDefault();
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        return argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? "\"" + argument.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : argument;
    }
}
