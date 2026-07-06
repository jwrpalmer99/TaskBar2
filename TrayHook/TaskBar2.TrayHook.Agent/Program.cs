using EasyHook;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TaskBar2.TrayHook.Injectee;

namespace TaskBar2.TrayHook.Agent;

internal static class Program
{
    public static int Main(string[] args)
    {
        var options = AgentOptions.Parse(args);
        if (options is null)
        {
            AgentLog.Write("Invalid arguments. Required: --pipe <name> --stopEvent <name> --globalStopEvent <name>");
            return 2;
        }

        var injecteePath = options.InjecteePath ?? typeof(TrayIconHookEntryPoint).Assembly.Location;
        if (!File.Exists(injecteePath))
        {
            AgentLog.Write($"Injectee assembly not found: {injecteePath}");
            return 3;
        }

        try
        {
            using var stopEvent = EventWaitHandle.OpenExisting(options.StopEventName);
            using var globalStopEvent = EventWaitHandle.OpenExisting(options.GlobalStopEventName);
            using var pauseEvent = OpenOptionalEvent(options.PauseEventName);
            var shadowInjecteePath = HookPayloadShadowCopy.Prepare(injecteePath);
            var agent = new HookAgent(
                options.AgentName,
                options.PipeName,
                options.StopEventName,
                options.InjecteeStopEventName,
                options.GlobalStopEventName,
                options.PauseEventName,
                shadowInjecteePath,
                options.IntervalMs,
                options.TargetMode,
                options.ShowAllTrayIcons,
                options.EnableTrayIconHook,
                options.EnableExplorerTaskbarHook,
                options.EnableExplorerTaskbarButtonImageCapture);
            agent.Run(stopEvent, globalStopEvent, pauseEvent);
            return 0;
        }
        catch (Exception exception)
        {
            AgentLog.Write($"Fatal agent error: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static EventWaitHandle? OpenOptionalEvent(string eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return null;
        }

        try
        {
            return EventWaitHandle.OpenExisting(eventName);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            AgentLog.Write($"Optional pause event not found. Event={eventName}");
            return null;
        }
    }
}

internal static class HookPayloadShadowCopy
{
    public static string Prepare(string injecteePath)
    {
        var sourceDirectory = Path.GetDirectoryName(injecteePath) ?? AppDomain.CurrentDomain.BaseDirectory;
        var shadowDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskBar2",
            "TrayHookShadow",
            $"{Process.GetCurrentProcess().Id}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");

        Directory.CreateDirectory(shadowDirectory);
        foreach (var sourceFile in Directory.GetFiles(sourceDirectory))
        {
            var fileName = Path.GetFileName(sourceFile);
            File.Copy(sourceFile, Path.Combine(shadowDirectory, fileName), overwrite: true);
        }

        CleanupOldShadowCopies(Path.GetDirectoryName(shadowDirectory));
        var shadowInjecteePath = Path.Combine(shadowDirectory, Path.GetFileName(injecteePath));
        AgentLog.Write($"Tray hook payload shadow-copied to {shadowDirectory}");
        return shadowInjecteePath;
    }

    private static void CleanupOldShadowCopies(string? rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(rootDirectory))
        {
            try
            {
                var info = new DirectoryInfo(directory);
                if (DateTime.UtcNow - info.CreationTimeUtc > TimeSpan.FromDays(2))
                {
                    info.Delete(recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}

internal sealed class HookAgent
{
    private const int StartupFastScanCount = 5;
    private const int StartupTransientRetryMs = 2000;
    private const int TrayIdleScanIntervalMs = 5000;
    private const int ExplorerIdleScanIntervalMs = 15000;
    private static readonly TimeSpan StartupTransientFailureWindow = TimeSpan.FromMinutes(1);

    private static readonly HashSet<string> ExcludedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle",
        "Registry",
        "System",
        "smss",
        "csrss",
        "wininit",
        "services",
        "lsass",
        "fontdrvhost",
        "TaskBar2",
        "TaskBar2.TrayHook.Agent",
        "TaskBar2.TrayHook.Injectee"
    };

    private readonly string _pipeName;
    private readonly string _agentName;
    private readonly string _stopEventName;
    private readonly string _injecteeStopEventName;
    private readonly string _globalStopEventName;
    private readonly string _pauseEventName;
    private readonly string _injecteePath;
    private readonly int _activeScanIntervalMs;
    private readonly int _idleScanIntervalMs;
    private readonly TargetMode _targetMode;
    private readonly TrayIconTargetCatalog _targetCatalog;
    private readonly int _currentProcessId = Process.GetCurrentProcess().Id;
    private readonly int _sessionId = Process.GetCurrentProcess().SessionId;
    private readonly HashSet<int> _injectedProcessIds = [];
    private readonly Dictionary<int, DateTimeOffset> _failedUntil = [];
    private readonly Dictionary<int, int> _failureCounts = [];
    private readonly Dictionary<int, CachedProcessState> _processStateById = [];
    private readonly bool _enableTrayIconHook;
    private readonly bool _enableExplorerTaskbarHook;
    private readonly bool _enableExplorerTaskbarButtonImageCapture;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastTaskbarCreatedBroadcast = DateTimeOffset.MinValue;
    private bool _residentInjecteeSeenInScan;
    private int _startupScanBroadcastsRemaining = 3;
    private int _startupFastScansRemaining = StartupFastScanCount;

    public HookAgent(
        string agentName,
        string pipeName,
        string stopEventName,
        string injecteeStopEventName,
        string globalStopEventName,
        string pauseEventName,
        string injecteePath,
        int intervalMs,
        TargetMode targetMode,
        bool showAllTrayIcons,
        bool enableTrayIconHook,
        bool enableExplorerTaskbarHook,
        bool enableExplorerTaskbarButtonImageCapture)
    {
        _agentName = string.IsNullOrWhiteSpace(agentName) ? "agent" : agentName;
        _pipeName = pipeName;
        _stopEventName = stopEventName;
        _injecteeStopEventName = string.IsNullOrWhiteSpace(injecteeStopEventName)
            ? stopEventName
            : injecteeStopEventName;
        _globalStopEventName = globalStopEventName;
        _pauseEventName = pauseEventName;
        _targetMode = targetMode;
        _targetCatalog = new TrayIconTargetCatalog(showAllTrayIcons);
        _enableTrayIconHook = enableTrayIconHook;
        _enableExplorerTaskbarHook = enableExplorerTaskbarHook;
        _enableExplorerTaskbarButtonImageCapture = enableExplorerTaskbarButtonImageCapture;
        _injecteePath = injecteePath;
        _activeScanIntervalMs = Math.Max(1000, intervalMs);
        _idleScanIntervalMs = Math.Max(
            _activeScanIntervalMs,
            _enableTrayIconHook ? TrayIdleScanIntervalMs : ExplorerIdleScanIntervalMs);
    }

    public void Run(WaitHandle stopEvent, WaitHandle globalStopEvent, WaitHandle? pauseEvent)
    {
        AgentLog.Write($"Tray hook agent started. Agent={_agentName} ProcessId={_currentProcessId} Session={_sessionId} Pipe={_pipeName} PauseEvent={_pauseEventName} Injectee={DescribeInjectee()} TargetMode={_targetMode} ShowAllTrayIcons={_targetCatalog.ShowAllTrayIcons} EnableTrayIconHook={_enableTrayIconHook} EnableExplorerTaskbarHook={_enableExplorerTaskbarHook} EnableExplorerTaskbarButtonImageCapture={_enableExplorerTaskbarButtonImageCapture} ActiveScanInterval={_activeScanIntervalMs} IdleScanInterval={_idleScanIntervalMs}");
        var stopHandles = new[] { stopEvent, globalStopEvent };
        while (WaitHandle.WaitAny(stopHandles, 0) == WaitHandle.WaitTimeout)
        {
            if (pauseEvent is not null && pauseEvent.WaitOne(0))
            {
                WaitWhilePaused(stopHandles, pauseEvent);
                continue;
            }

            var scanResult = ScanOnce();
            WaitHandle.WaitAny(stopHandles, GetNextScanDelay(scanResult));
        }

        AgentLog.Write($"Tray hook agent stopping. Agent={_agentName}");
    }

    private void WaitWhilePaused(WaitHandle[] stopHandles, WaitHandle pauseEvent)
    {
        AgentLog.Write($"Tray hook agent scan loop suspended. Agent={_agentName}");
        while (WaitHandle.WaitAny(stopHandles, 500) == WaitHandle.WaitTimeout)
        {
            if (!pauseEvent.WaitOne(0))
            {
                AgentLog.Write($"Tray hook agent scan loop resumed. Agent={_agentName}");
                return;
            }
        }
    }

    private ScanResult ScanOnce()
    {
        if (_enableTrayIconHook)
        {
            _targetCatalog.RefreshIfNeeded();
        }

        var now = DateTimeOffset.UtcNow;
        var injectedCount = 0;
        _residentInjecteeSeenInScan = false;
        var processNameFilter = BuildProcessNameFilter();
        var processes = ProcessSnapshotProvider.GetProcesses(processNameFilter);
        PruneProcessState(processes);
        var candidates = processes
            .Where(process => IsCandidate(process, now))
            .ToArray();

        foreach (var process in SelectCandidateProcesses(candidates))
        {
            if (TryInject(process))
            {
                injectedCount++;
            }
        }

        var startupBroadcast = _startupScanBroadcastsRemaining > 0;
        if (_startupScanBroadcastsRemaining > 0)
        {
            _startupScanBroadcastsRemaining--;
        }

        if (injectedCount > 0 || _residentInjecteeSeenInScan || startupBroadcast)
        {
            BroadcastTaskbarCreated(injectedCount, force: startupBroadcast);
        }

        var startupFastScan = _startupFastScansRemaining > 0;
        if (_startupFastScansRemaining > 0)
        {
            _startupFastScansRemaining--;
        }

        return new ScanResult(candidates.Length, injectedCount, startupBroadcast || startupFastScan);
    }

    private int GetNextScanDelay(ScanResult scanResult)
    {
        if (scanResult.IsStartupScan ||
            scanResult.CandidateCount > 0 ||
            scanResult.InjectedCount > 0)
        {
            return _activeScanIntervalMs;
        }

        return _idleScanIntervalMs;
    }

    private HashSet<string>? BuildProcessNameFilter()
    {
        HashSet<string>? names = null;
        if (_enableExplorerTaskbarHook)
        {
            names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "explorer" };
        }

        if (_enableTrayIconHook && !_targetCatalog.IsEmpty)
        {
            names ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var processName in _targetCatalog.GetProcessNamesSnapshot())
            {
                names.Add(processName);
            }
        }

        return names is { Count: > 0 } ? names : null;
    }

    private static IEnumerable<ProcessSnapshot> SelectCandidateProcesses(IReadOnlyList<ProcessSnapshot> candidates)
    {
        var duplicateProcessIds = new HashSet<int>(
            candidates
                .GroupBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Skip(1).Any())
                .SelectMany(group => group.Select(process => process.Id)));
        var mainWindows = ProcessWindowFinder.FindMainWindows(duplicateProcessIds);

        foreach (var group in candidates.GroupBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            var grouped = group.ToArray();
            if (grouped.Length == 1)
            {
                yield return grouped[0];
                continue;
            }

            var windowProcesses = grouped
                .Where(process => mainWindows.ContainsKey(process.Id))
                .ToArray();

            foreach (var process in windowProcesses.Length > 0 ? windowProcesses : grouped)
            {
                yield return process;
            }
        }
    }

    private bool IsCandidate(ProcessSnapshot process, DateTimeOffset now)
    {
        try
        {
            if (process.Id == _currentProcessId || process.Id <= 4)
            {
                return false;
            }

            var processName = process.ProcessName;
            var isExplorer = IsExplorer(processName);
            if (isExplorer && !_enableExplorerTaskbarHook)
            {
                return false;
            }

            if (ExcludedProcessNames.Contains(processName))
            {
                return false;
            }

            if (!isExplorer && !_enableTrayIconHook)
            {
                return false;
            }

            if (!isExplorer && !_targetCatalog.IsEmpty && !_targetCatalog.Matches(processName))
            {
                return false;
            }

            if (process.SessionId != _sessionId)
            {
                return false;
            }

            if (_injectedProcessIds.Contains(process.Id))
            {
                return false;
            }

            if (!MatchesTargetMode(process))
            {
                return false;
            }

            if (HasResidentInjectee(process))
            {
                _injectedProcessIds.Add(process.Id);
                _residentInjecteeSeenInScan = true;
                AgentLog.Write($"Skipped resident injected process. Agent={_agentName} {DescribeProcess(process)}");
                return false;
            }

            if (_failedUntil.TryGetValue(process.Id, out var retryAt) && retryAt > now)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool MatchesTargetMode(ProcessSnapshot process)
    {
        if (_targetMode == TargetMode.All)
        {
            return true;
        }

        if (!TryGetCachedElevation(process, out var isElevated))
        {
            return false;
        }

        return _targetMode == TargetMode.ElevatedOnly
            ? isElevated
            : !isElevated;
    }

    private bool TryInject(ProcessSnapshot process)
    {
        try
        {
            AgentLog.Write($"Injection attempt. Agent={_agentName} {DescribeProcess(process)} Injectee={DescribeInjectee()} TargetMode={_targetMode} EnableTrayIconHook={_enableTrayIconHook} EnableExplorerTaskbarHook={_enableExplorerTaskbarHook} EnableExplorerTaskbarButtonImageCapture={_enableExplorerTaskbarButtonImageCapture}");
            RemoteHooking.Inject(
                process.Id,
                InjectionOptions.Default,
                _injecteePath,
                _injecteePath,
                _pipeName,
                _injecteeStopEventName,
                _globalStopEventName,
                _pauseEventName,
                _targetCatalog.ShowAllTrayIcons,
                _enableExplorerTaskbarButtonImageCapture);

            _injectedProcessIds.Add(process.Id);
            _failedUntil.Remove(process.Id);
            _failureCounts.Remove(process.Id);
            AgentLog.Write($"Injected process. Agent={_agentName} {DescribeProcess(process)}");
            return true;
        }
        catch (Exception exception)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _startedAt < StartupTransientFailureWindow)
            {
                _failedUntil[process.Id] = now.AddMilliseconds(StartupTransientRetryMs);
                AgentLog.Write($"Skipped process. Agent={_agentName} {DescribeProcess(process)} RetryIn={StartupTransientRetryMs / 1000:0}s StartupTransient=True HResult=0x{exception.HResult:X8}: {exception.GetType().Name}: {exception.Message}");
                return false;
            }

            var failureCount = _failureCounts.TryGetValue(process.Id, out var existingFailureCount)
                ? existingFailureCount + 1
                : 1;
            _failureCounts[process.Id] = failureCount;
            var retryDelay = GetFailureRetryDelay(failureCount);
            _failedUntil[process.Id] = DateTimeOffset.UtcNow.Add(retryDelay);
            AgentLog.Write($"Skipped process. Agent={_agentName} {DescribeProcess(process)} RetryIn={retryDelay.TotalSeconds:0}s FailureCount={failureCount} HResult=0x{exception.HResult:X8}: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private bool HasResidentInjectee(ProcessSnapshot process)
    {
        try
        {
            using var aliveEvent = EventWaitHandle.OpenExisting(GetInjecteeAliveEventName(process.SessionId, process.Id));
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string GetInjecteeAliveEventName(int sessionId, int processId) =>
        $"Local\\TaskBar2.TrayHook.InjecteeAlive.{sessionId}.{processId}";

    private void PruneProcessState(IReadOnlyList<ProcessSnapshot> processes)
    {
        var activeProcessIds = new HashSet<int>(processes.Select(process => process.Id));
        foreach (var processId in _processStateById.Keys.Where(processId => !activeProcessIds.Contains(processId)).ToArray())
        {
            _processStateById.Remove(processId);
        }

        _injectedProcessIds.RemoveWhere(processId => !activeProcessIds.Contains(processId));
        foreach (var processId in _failedUntil.Keys.Where(processId => !activeProcessIds.Contains(processId)).ToArray())
        {
            _failedUntil.Remove(processId);
            _failureCounts.Remove(processId);
        }
    }

    private bool TryGetCachedElevation(ProcessSnapshot process, out bool isElevated)
    {
        isElevated = false;
        if (!_processStateById.TryGetValue(process.Id, out var state) ||
            !state.Matches(process))
        {
            state = new CachedProcessState(process.ProcessName, process.SessionId);
            _processStateById[process.Id] = state;
        }

        if (state.IsElevated.HasValue)
        {
            isElevated = state.IsElevated.Value;
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        if (state.ElevationRetryAt > now)
        {
            return false;
        }

        if (!ProcessElevation.TryIsElevated(process.Id, out isElevated))
        {
            state.ElevationRetryAt = now.AddSeconds(30);
            return false;
        }

        state.IsElevated = isElevated;
        return true;
    }

    private static TimeSpan GetFailureRetryDelay(int failureCount)
    {
        var seconds = failureCount switch
        {
            <= 1 => 10,
            2 => 30,
            3 => 60,
            4 => 120,
            _ => 300
        };
        return TimeSpan.FromSeconds(seconds);
    }

    private string DescribeInjectee()
    {
        try
        {
            var info = new FileInfo(_injecteePath);
            var version = FileVersionInfo.GetVersionInfo(_injecteePath);
            return $"{_injecteePath} Exists={info.Exists} Bytes={(info.Exists ? info.Length : 0)} Version={version.FileVersion ?? "unknown"}";
        }
        catch (Exception exception)
        {
            return $"{_injecteePath} DescribeError={exception.GetType().Name}:{exception.Message}";
        }
    }

    private string DescribeProcess(ProcessSnapshot process)
    {
        var elevated = TryGetCachedElevation(process, out var isElevated)
            ? isElevated ? "true" : "false"
            : "unknown";
        return
            $"Pid={process.Id} Name={process.ProcessName} Session={process.SessionId} Elevated={elevated} Arch={ProcessDiagnostics.GetArchitecture(process.Id)} MainWindow=0x{ProcessWindowFinder.FindMainWindow(process.Id).ToInt64():X} Path={ProcessDiagnostics.GetImagePath(process.Id)}";
    }

    private sealed class CachedProcessState
    {
        public CachedProcessState(string processName, int sessionId)
        {
            ProcessName = processName;
            SessionId = sessionId;
        }

        public string ProcessName { get; }

        public int SessionId { get; }

        public bool? IsElevated { get; set; }

        public DateTimeOffset ElevationRetryAt { get; set; }

        public bool Matches(ProcessSnapshot process) =>
            SessionId == process.SessionId &&
            string.Equals(ProcessName, process.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private readonly struct ScanResult
    {
        public ScanResult(int candidateCount, int injectedCount, bool isStartupScan)
        {
            CandidateCount = candidateCount;
            InjectedCount = injectedCount;
            IsStartupScan = isStartupScan;
        }

        public int CandidateCount { get; }

        public int InjectedCount { get; }

        public bool IsStartupScan { get; }
    }

    private void BroadcastTaskbarCreated(int injectedCount, bool force)
    {
        if (!force && DateTimeOffset.UtcNow - _lastTaskbarCreatedBroadcast < TimeSpan.FromSeconds(5))
        {
            return;
        }

        var message = RegisterWindowMessage("TaskbarCreated");
        if (message == 0)
        {
            AgentLog.Write("TaskbarCreated broadcast skipped: RegisterWindowMessage failed.");
            return;
        }

        _lastTaskbarCreatedBroadcast = DateTimeOffset.UtcNow;
        SendNotifyMessage(new IntPtr(0xFFFF), message, UIntPtr.Zero, IntPtr.Zero);
        AgentLog.Write($"Broadcast TaskbarCreated after tray hook scan. Injected={injectedCount} Force={force}.");
    }

    private static bool IsExplorer(string processName) =>
        string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNotifyMessage(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);
}

internal enum TargetMode
{
    All,
    StandardOnly,
    ElevatedOnly
}

internal readonly struct ProcessSnapshot
{
    public ProcessSnapshot(int id, string processName, int sessionId)
    {
        Id = id;
        ProcessName = processName;
        SessionId = sessionId;
    }

    public int Id { get; }

    public string ProcessName { get; }

    public int SessionId { get; }
}

internal static class ProcessSnapshotProvider
{
    private const uint Th32csSnapProcess = 0x00000002;
    private const int MaxPath = 260;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static IReadOnlyList<ProcessSnapshot> GetProcesses(ICollection<string>? processNameFilter = null)
    {
        return TryGetProcessesFromWts(processNameFilter, out var processes)
            ? processes
            : GetProcessesFromToolhelp(processNameFilter);
    }

    private static bool TryGetProcessesFromWts(
        ICollection<string>? processNameFilter,
        out IReadOnlyList<ProcessSnapshot> processes)
    {
        processes = Array.Empty<ProcessSnapshot>();
        if (!WTSEnumerateProcessesW(IntPtr.Zero, 0, 1, out var processInfoBuffer, out var count) ||
            processInfoBuffer == IntPtr.Zero ||
            count <= 0)
        {
            return false;
        }

        try
        {
            var result = new List<ProcessSnapshot>(Math.Min(count, 256));
            var entrySize = Marshal.SizeOf<WtsProcessInfo>();
            for (var index = 0; index < count; index++)
            {
                var entryAddress = IntPtr.Add(processInfoBuffer, index * entrySize);
                var entry = Marshal.PtrToStructure<WtsProcessInfo>(entryAddress);
                var processId = unchecked((int)entry.ProcessId);
                if (processId <= 0)
                {
                    continue;
                }

                var processName = Path.GetFileNameWithoutExtension(entry.ProcessName ?? "");
                if (string.IsNullOrWhiteSpace(processName))
                {
                    continue;
                }

                if (processNameFilter is { Count: > 0 } &&
                    !processNameFilter.Contains(processName))
                {
                    continue;
                }

                result.Add(new ProcessSnapshot(processId, processName, unchecked((int)entry.SessionId)));
            }

            processes = result;
            return true;
        }
        finally
        {
            WTSFreeMemory(processInfoBuffer);
        }
    }

    private static IReadOnlyList<ProcessSnapshot> GetProcessesFromToolhelp(ICollection<string>? processNameFilter)
    {
        var snapshotHandle = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshotHandle == IntPtr.Zero || snapshotHandle == InvalidHandleValue)
        {
            return Array.Empty<ProcessSnapshot>();
        }

        try
        {
            var result = new List<ProcessSnapshot>(256);
            var entry = new ProcessEntry32
            {
                Size = Marshal.SizeOf<ProcessEntry32>()
            };

            if (!Process32First(snapshotHandle, ref entry))
            {
                return result;
            }

            do
            {
                var processId = unchecked((int)entry.ProcessId);
                if (processId <= 0)
                {
                    continue;
                }

                var processName = Path.GetFileNameWithoutExtension(entry.ExecutableFile);
                if (string.IsNullOrWhiteSpace(processName))
                {
                    continue;
                }

                if (processNameFilter is { Count: > 0 } &&
                    !processNameFilter.Contains(processName))
                {
                    continue;
                }

                if (!ProcessIdToSessionId(processId, out var sessionId))
                {
                    sessionId = -1;
                }

                result.Add(new ProcessSnapshot(processId, processName, sessionId));
            }
            while (Process32Next(snapshotHandle, ref entry));

            return result;
        }
        finally
        {
            CloseHandle(snapshotHandle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(int processId, out int sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateProcessesW(
        IntPtr serverHandle,
        int reserved,
        int version,
        out IntPtr processInfo,
        out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsProcessInfo
    {
        public uint SessionId;
        public uint ProcessId;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ProcessName;

        public IntPtr UserSid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public int Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
        public string ExecutableFile;
    }
}

internal static class ProcessWindowFinder
{
    private const uint GwOwner = 4;

    public static bool HasMainWindow(int processId) => FindMainWindow(processId) != IntPtr.Zero;

    public static IReadOnlyDictionary<int, IntPtr> FindMainWindows(ICollection<int> processIds)
    {
        var result = new Dictionary<int, IntPtr>();
        if (processIds.Count == 0)
        {
            return result;
        }

        var remaining = new HashSet<int>(processIds);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            if (!remaining.Contains(processId) ||
                !IsWindowVisible(hwnd) ||
                GetWindow(hwnd, GwOwner) != IntPtr.Zero)
            {
                return true;
            }

            result[processId] = hwnd;
            remaining.Remove(processId);
            return remaining.Count > 0;
        }, IntPtr.Zero);

        return result;
    }

    public static IntPtr FindMainWindow(int processId)
    {
        var result = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var windowProcessId);
            if (windowProcessId != processId ||
                !IsWindowVisible(hwnd) ||
                GetWindow(hwnd, GwOwner) != IntPtr.Zero)
            {
                return true;
            }

            result = hwnd;
            return false;
        }, IntPtr.Zero);

        return result;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
}

internal static class ProcessElevation
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    public static bool TryIsElevated(int processId, out bool isElevated)
    {
        isElevated = false;
        var processHandle = IntPtr.Zero;
        var tokenHandle = IntPtr.Zero;

        try
        {
            processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (processHandle == IntPtr.Zero)
            {
                return false;
            }

            if (!OpenProcessToken(processHandle, TokenQuery, out tokenHandle))
            {
                return false;
            }

            if (!GetTokenInformation(
                    tokenHandle,
                    TokenElevation,
                    out var elevation,
                    Marshal.SizeOf<TokenElevationInfo>(),
                    out _))
            {
                return false;
            }

            isElevated = elevation.TokenIsElevated != 0;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (tokenHandle != IntPtr.Zero)
            {
                CloseHandle(tokenHandle);
            }

            if (processHandle != IntPtr.Zero)
            {
                CloseHandle(processHandle);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        out TokenElevationInfo tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevationInfo
    {
        public int TokenIsElevated;
    }
}

internal static class ProcessDiagnostics
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public static string GetArchitecture(int processId)
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            return "x86";
        }

        var processHandle = IntPtr.Zero;
        try
        {
            processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (processHandle == IntPtr.Zero)
            {
                return $"unknown(open={Marshal.GetLastWin32Error()})";
            }

            return IsWow64Process(processHandle, out var isWow64)
                ? isWow64 ? "x86" : "x64"
                : $"unknown(wow64={Marshal.GetLastWin32Error()})";
        }
        catch (Exception exception)
        {
            return $"unknown({exception.GetType().Name})";
        }
        finally
        {
            if (processHandle != IntPtr.Zero)
            {
                CloseHandle(processHandle);
            }
        }
    }

    public static string GetImagePath(int processId)
    {
        var processHandle = IntPtr.Zero;
        try
        {
            processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (processHandle == IntPtr.Zero)
            {
                return $"unknown(open={Marshal.GetLastWin32Error()})";
            }

            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(processHandle, 0, buffer, ref size)
                ? buffer.ToString()
                : $"unknown(path={Marshal.GetLastWin32Error()})";
        }
        catch (Exception exception)
        {
            return $"unknown({exception.GetType().Name})";
        }
        finally
        {
            if (processHandle != IntPtr.Zero)
            {
                CloseHandle(processHandle);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr processHandle, out bool wow64Process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr processHandle,
        int flags,
        StringBuilder executablePath,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}

internal sealed class TrayIconTargetCatalog
{
    private readonly bool _showAllTrayIcons;
    private readonly HashSet<string> _processNames = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    public TrayIconTargetCatalog(bool showAllTrayIcons)
    {
        _showAllTrayIcons = showAllTrayIcons;
    }

    public bool ShowAllTrayIcons => _showAllTrayIcons;

    public bool IsEmpty => _processNames.Count == 0;

    public void RefreshIfNeeded()
    {
        if (DateTimeOffset.UtcNow - _lastRefresh < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _lastRefresh = DateTimeOffset.UtcNow;
        Refresh();
    }

    public bool Matches(string processName) => _processNames.Contains(processName);

    public string[] GetProcessNamesSnapshot() => _processNames.ToArray();

    private void Refresh()
    {
        _processNames.Clear();

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root is null)
            {
                return;
            }

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var subKey = root.OpenSubKey(subKeyName);
                var executablePath = subKey?.GetValue("ExecutablePath") as string;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    continue;
                }

                if (!_showAllTrayIcons && !ReadIsPromoted(subKey))
                {
                    continue;
                }

                AddExecutablePath(ResolveKnownFolderPath(executablePath!));
            }

            AgentLog.Write($"Tray icon injection target catalog: ProcessNames={_processNames.Count} ShowAllTrayIcons={_showAllTrayIcons}");
        }
        catch (Exception exception)
        {
            AgentLog.Write($"Tray icon target catalog read failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static bool ReadIsPromoted(RegistryKey? subKey)
    {
        var value = subKey?.GetValue("IsPromoted");
        if (value is int intValue)
        {
            return intValue != 0;
        }

        if (value is long longValue)
        {
            return longValue != 0;
        }

        return value is string stringValue &&
               int.TryParse(stringValue, out var parsed) &&
               parsed != 0;
    }

    private void AddExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        try
        {
            var fileName = Path.GetFileNameWithoutExtension(NormalizePath(executablePath));
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                _processNames.Add(fileName);
            }
        }
        catch
        {
        }
    }

    private static string ResolveKnownFolderPath(string path)
    {
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.GetFolderPath(Environment.SpecialFolder.System)
        };

        foreach (var replacement in replacements)
        {
            if (path.StartsWith(replacement.Key, StringComparison.OrdinalIgnoreCase))
            {
                return replacement.Value + path.Substring(replacement.Key.Length);
            }
        }

        return path;
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
}

internal sealed class AgentOptions
{
    public string AgentName { get; set; } = "agent";

    public string PipeName { get; set; } = "";

    public string StopEventName { get; set; } = "";

    public string InjecteeStopEventName { get; set; } = "";

    public string GlobalStopEventName { get; set; } = "";

    public string PauseEventName { get; set; } = "";

    public string? InjecteePath { get; set; }

    public int IntervalMs { get; set; } = 2500;

    public TargetMode TargetMode { get; set; } = TargetMode.All;

    public bool ShowAllTrayIcons { get; set; }

    public bool EnableTrayIconHook { get; set; } = true;

    public bool EnableExplorerTaskbarHook { get; set; }

    public bool EnableExplorerTaskbarButtonImageCapture { get; set; }

    public static AgentOptions? Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                continue;
            }

            values[key.Substring(2)] = args[++index];
        }

        if (!values.TryGetValue("pipe", out var pipeName) || string.IsNullOrWhiteSpace(pipeName) ||
            !values.TryGetValue("stopEvent", out var stopEventName) || string.IsNullOrWhiteSpace(stopEventName) ||
            !values.TryGetValue("globalStopEvent", out var globalStopEventName) || string.IsNullOrWhiteSpace(globalStopEventName))
        {
            return null;
        }

        values.TryGetValue("injectee", out var injecteePath);
        values.TryGetValue("agentName", out var agentName);
        values.TryGetValue("injecteeStopEvent", out var injecteeStopEventName);
        values.TryGetValue("pauseEvent", out var pauseEventName);
        var interval = values.TryGetValue("interval", out var intervalValue) && int.TryParse(intervalValue, out var parsedInterval)
            ? parsedInterval
            : 2500;
        var targetMode = values.TryGetValue("targetMode", out var targetModeValue) &&
                         Enum.TryParse<TargetMode>(targetModeValue, ignoreCase: true, out var parsedTargetMode)
            ? parsedTargetMode
            : TargetMode.All;
        var showAllTrayIcons = values.TryGetValue("showAllTrayIcons", out var showAllValue) &&
                               bool.TryParse(showAllValue, out var parsedShowAll) &&
                               parsedShowAll;
        var enableTrayIconHook = !values.TryGetValue("enableTrayIconHook", out var enableTrayValue) ||
                                 !bool.TryParse(enableTrayValue, out var parsedEnableTray) ||
                                 parsedEnableTray;
        var enableExplorerTaskbarHook = values.TryGetValue("enableExplorerTaskbarHook", out var enableExplorerValue) &&
                                        bool.TryParse(enableExplorerValue, out var parsedEnableExplorer) &&
                                        parsedEnableExplorer;
        var enableExplorerTaskbarButtonImageCapture =
            values.TryGetValue("enableExplorerTaskbarButtonImageCapture", out var enableExplorerCaptureValue) &&
            bool.TryParse(enableExplorerCaptureValue, out var parsedEnableExplorerCapture) &&
            parsedEnableExplorerCapture;

        return new AgentOptions
        {
            AgentName = string.IsNullOrWhiteSpace(agentName) ? "agent" : agentName,
            PipeName = pipeName,
            StopEventName = stopEventName,
            InjecteeStopEventName = string.IsNullOrWhiteSpace(injecteeStopEventName)
                ? stopEventName
                : injecteeStopEventName,
            GlobalStopEventName = globalStopEventName,
            PauseEventName = string.IsNullOrWhiteSpace(pauseEventName) ? "" : pauseEventName,
            InjecteePath = string.IsNullOrWhiteSpace(injecteePath) ? null : injecteePath,
            IntervalMs = interval,
            TargetMode = targetMode,
            ShowAllTrayIcons = showAllTrayIcons,
            EnableTrayIconHook = enableTrayIconHook,
            EnableExplorerTaskbarHook = enableExplorerTaskbarHook,
            EnableExplorerTaskbarButtonImageCapture = enableExplorerTaskbarButtonImageCapture
        };
    }
}

internal static class AgentLog
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskBar2");
    private static readonly string LogPath = Path.Combine(LogDirectory, "tray-hook-agent.log");

    [Conditional("DEBUG")]
    public static void Write(string message)
    {
#if !DEBUG
        return;
#else
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
            }
        }
        catch
        {
        }
#endif
    }
}
