using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace TaskBar2.Services;

internal sealed class TrayHookAgentService : IDisposable
{
    private static readonly TimeSpan GlobalStopPulseDuration = TimeSpan.FromMilliseconds(1500);

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
        _globalStopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, _globalStopEventName);
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

        var enableTrayIconHook = AppSettingsService.Current.EnableInvasiveTrayIconHook;
        var enableExplorerTaskbarHook =
            AppSettingsService.Current.EnableExperimentalExplorerTaskbarHook ||
            AppSettingsService.Current.EnableExperimentalExplorerTaskbarMenuProxy;

        if (enableTrayIconHook || enableExplorerTaskbarHook)
        {
            var runElevated = AppSettingsService.Current.EnableElevatedTrayIconHookAgent;
            var showAllTrayIcons = AppSettingsService.Current.ShowAllTrayIcons;
            var splitElevatedTrayAgent = enableTrayIconHook && runElevated;
            var startMainAgent = enableTrayIconHook;
            var startStandardAgent = splitElevatedTrayAgent;
            var startExplorerAgent = enableExplorerTaskbarHook;
            var mainTargetMode = splitElevatedTrayAgent ? "ElevatedOnly" : "StandardOnly";
            var mainElevated = splitElevatedTrayAgent;
            var mainEnableTray = enableTrayIconHook;
            var mainEnableExplorer = false;
            var standardEnableTray = enableTrayIconHook;
            var standardEnableExplorer = false;

            var needsRestart =
                (startMainAgent
                    ? _agent.RequiresRestart(mainTargetMode, mainElevated, showAllTrayIcons, mainEnableTray, mainEnableExplorer)
                    : _agent.IsRunning) ||
                (startStandardAgent
                    ? _standardAgent.RequiresRestart("StandardOnly", elevated: false, showAllTrayIcons, standardEnableTray, standardEnableExplorer)
                    : _standardAgent.IsRunning) ||
                (startExplorerAgent
                    ? _explorerAgent.RequiresRestart("StandardOnly", elevated: false, showAllTrayIcons: false, enableTrayIconHook: false, enableExplorerTaskbarHook: true)
                    : _explorerAgent.IsRunning);

            if (needsRestart)
            {
                Stop(resetGlobalStopEvent: true);
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
                    showAllTrayIcons,
                    mainEnableTray,
                    mainEnableExplorer);
            }

            if (startStandardAgent)
            {
                _standardAgent.Start(
                    "StandardOnly",
                    elevated: false,
                    showAllTrayIcons,
                    standardEnableTray,
                    standardEnableExplorer);
            }

            if (startExplorerAgent)
            {
                _explorerAgent.Start(
                    "StandardOnly",
                    elevated: false,
                    showAllTrayIcons: false,
                    enableTrayIconHook: false,
                    enableExplorerTaskbarHook: true);
            }
        }
        else
        {
            Stop(resetGlobalStopEvent: true);
        }
    }

    public void ForceRestart()
    {
        if (_disposed)
        {
            return;
        }

        DebugLogger.Write("Tray hook agent force restart requested.");
        Stop(resetGlobalStopEvent: true);
        ApplySettings();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop(resetGlobalStopEvent: false);
        _globalStopEvent.Dispose();
    }

    private void Stop(bool resetGlobalStopEvent)
    {
        var signaledAt = SignalGlobalStopEvent();
        _agent.Stop();
        _standardAgent.Stop();
        _explorerAgent.Stop();

        if (resetGlobalStopEvent)
        {
            WaitForGlobalStopPulse(signaledAt);
            ResetGlobalStopEvent();
        }
    }

    private void ReleaseStaleInjectedHooks()
    {
        DebugLogger.Write($"Tray hook global stop pulse starting. Event={_globalStopEventName}");
        var signaledAt = SignalGlobalStopEvent();
        WaitForGlobalStopPulse(signaledAt);
        ResetGlobalStopEvent();
        DebugLogger.Write("Tray hook global stop pulse complete.");
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

    private sealed class AgentProcess
    {
        private readonly string _pipeName;
        private readonly string _globalStopEventName;
        private Process? _process;
        private EventWaitHandle? _stopEvent;
        private string? _stopEventName;
        private string _targetMode = "StandardOnly";
        private bool _elevated;
        private bool _showAllTrayIcons;
        private bool _enableTrayIconHook;
        private bool _enableExplorerTaskbarHook;
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
            bool enableExplorerTaskbarHook) =>
            IsRunning &&
            (!string.Equals(_targetMode, targetMode, StringComparison.OrdinalIgnoreCase) ||
             _elevated != elevated ||
             _showAllTrayIcons != showAllTrayIcons ||
             _enableTrayIconHook != enableTrayIconHook ||
             _enableExplorerTaskbarHook != enableExplorerTaskbarHook);

        public void Start(
            string targetMode,
            bool elevated,
            bool showAllTrayIcons,
            bool enableTrayIconHook,
            bool enableExplorerTaskbarHook)
        {
            if (IsRunning &&
                string.Equals(_targetMode, targetMode, StringComparison.OrdinalIgnoreCase) &&
                _elevated == elevated &&
                _showAllTrayIcons == showAllTrayIcons &&
                _enableTrayIconHook == enableTrayIconHook &&
                _enableExplorerTaskbarHook == enableExplorerTaskbarHook)
            {
                return;
            }

            Stop();
            _targetMode = targetMode;
            _elevated = elevated;
            _showAllTrayIcons = showAllTrayIcons;
            _enableTrayIconHook = enableTrayIconHook;
            _enableExplorerTaskbarHook = enableExplorerTaskbarHook;

            var agentPath = ResolveAgentPath();
            if (agentPath is null)
            {
                DebugLogger.WriteIfChanged(
                    "tray-hook-agent-missing",
                    "Tray hook agent is enabled but TaskBar2.TrayHook.Agent.exe was not found.");
                return;
            }

            _stopEventName = $"Local\\TaskBar2.TrayHook.Stop.{Environment.ProcessId}.{_name}";
            _stopEvent?.Dispose();
            _stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, _stopEventName);

            var arguments = CreateArguments();

            try
            {
                if (!_elevated && IsCurrentProcessElevated() && TryStartUnelevatedViaExplorer(agentPath, arguments))
                {
                    _process = null;
                    _startedWithoutHandle = true;
                    DebugLogger.Write($"Tray hook agent started through Explorer shell. Name={_name} Path={agentPath} Elevated=False TargetMode={_targetMode} ShowAllTrayIcons={_showAllTrayIcons} EnableTrayIconHook={_enableTrayIconHook} EnableExplorerTaskbarHook={_enableExplorerTaskbarHook}");
                    return;
                }

                var startInfo = CreateStartInfo(agentPath, arguments);
                _process = Process.Start(startInfo);
                _startedWithoutHandle = false;
                DebugLogger.Write($"Tray hook agent started. Name={_name} Path={agentPath} ProcessId={_process?.Id} Elevated={_elevated} TargetMode={_targetMode} ShowAllTrayIcons={_showAllTrayIcons} EnableTrayIconHook={_enableTrayIconHook} EnableExplorerTaskbarHook={_enableExplorerTaskbarHook}");
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
            try
            {
                _stopEvent?.Set();
            }
            catch (ObjectDisposedException)
            {
            }

            if (_process is { HasExited: false })
            {
                if (!WaitForExit(_process, TimeSpan.FromSeconds(3)))
                {
                    try
                    {
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
        }

        private string[] CreateArguments()
        {
            return
            [
                "--pipe", _pipeName,
                "--agentName", _name,
                "--stopEvent", _stopEventName ?? "",
                "--globalStopEvent", _globalStopEventName,
                "--interval", "1000",
                "--targetMode", _targetMode,
                "--showAllTrayIcons", _showAllTrayIcons ? "true" : "false",
                "--enableTrayIconHook", _enableTrayIconHook ? "true" : "false",
                "--enableExplorerTaskbarHook", _enableExplorerTaskbarHook ? "true" : "false"
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
