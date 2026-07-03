using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace TaskBar2.Services;

internal sealed class TrayIconHookServer : IDisposable
{
    private const int ProtocolVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Task? _serverTask;

    public TrayIconHookServer()
    {
        PipeName = $"TaskBar2.TrayIconHook.{Process.GetCurrentProcess().SessionId}";
    }

    public string PipeName { get; }

    public void Start()
    {
        if (_serverTask is not null)
        {
            return;
        }

        WriteEndpointFile();
        _serverTask = Task.Run(() => ListenAsync(_cancellationTokenSource.Token));
        DebugLogger.Write($"Tray hook bridge listening: Pipe=\\\\.\\pipe\\{PipeName} Protocol={ProtocolVersion}");
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        try
        {
            _serverTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        _cancellationTokenSource.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                if (HookProcessingPauseService.IsPaused)
                {
                    continue;
                }

                await ReadClientAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                DebugLogger.WriteIfChanged(
                    "tray-hook-server-error",
                    $"Tray hook bridge error: {exception.GetType().Name}: {exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ReadClientAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (HookProcessingPauseService.IsPaused)
            {
                return;
            }

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (HookProcessingPauseService.IsPaused)
            {
                return;
            }

            ProcessMessage(line);
        }
    }

    private static void ProcessMessage(string json)
    {
        try
        {
            var messageType = GetMessageType(json);
            if (string.Equals(messageType, "TaskbarState", StringComparison.OrdinalIgnoreCase))
            {
                ProcessTaskbarStateMessage(json);
                return;
            }

            if (string.Equals(messageType, "ExplorerTaskbarSnapshot", StringComparison.OrdinalIgnoreCase))
            {
                ProcessExplorerTaskbarSnapshotMessage(json);
                return;
            }

            var message = JsonSerializer.Deserialize<TrayIconHookMessage>(json, JsonOptions);
            if (message is null)
            {
                return;
            }

            LogMissingSourceMetadataShape(json, message);
            var changed = TrayIconSnapshotStore.Apply(message);
            if (changed)
            {
                var summary = TrayIconSnapshotStore.GetSummary();
                DebugLogger.WriteIfChanged(
                    "tray-hook-summary",
                    $"Tray hook snapshots: Count={summary.Count} Visible={summary.VisibleCount} IconsWithImages={summary.IconsWithImages}");
            }
        }
        catch (JsonException exception)
        {
            DebugLogger.WriteIfChanged(
                "tray-hook-json-error",
                $"Tray hook bridge ignored invalid JSON: {exception.Message}");
        }
    }

    private static string GetMessageType(string json)
    {
        const string propertyName = "\"messageType\"";
        var propertyIndex = json.IndexOf(propertyName, StringComparison.OrdinalIgnoreCase);
        if (propertyIndex < 0)
        {
            return "";
        }

        var colonIndex = json.IndexOf(':', propertyIndex + propertyName.Length);
        if (colonIndex < 0)
        {
            return "";
        }

        var valueStart = json.IndexOf('"', colonIndex + 1);
        if (valueStart < 0)
        {
            return "";
        }

        for (var valueEnd = valueStart + 1; valueEnd < json.Length; valueEnd++)
        {
            if (json[valueEnd] == '"' && json[valueEnd - 1] != '\\')
            {
                return json.Substring(valueStart + 1, valueEnd - valueStart - 1);
            }
        }

        return "";
    }

    private static void ProcessTaskbarStateMessage(string json)
    {
        var message = JsonSerializer.Deserialize<TaskbarStateHookMessage>(json, JsonOptions);
        if (message is null)
        {
            return;
        }

        TaskbarStateSnapshotStore.Apply(message);
    }

    private static void ProcessExplorerTaskbarSnapshotMessage(string json)
    {
        var message = JsonSerializer.Deserialize<ExplorerTaskbarSnapshotMessage>(json, JsonOptions);
        if (message is null)
        {
            return;
        }

        ExplorerTaskbarSnapshotStore.Apply(message);
    }

    private void WriteEndpointFile()
    {
        Directory.CreateDirectory(DebugLogger.LogDirectory);
        var endpoint = new TrayIconHookEndpoint(
            ProtocolVersion,
            PipeName,
            $"\\\\.\\pipe\\{PipeName}",
            Environment.ProcessId,
            Process.GetCurrentProcess().SessionId);

        File.WriteAllText(
            Path.Combine(DebugLogger.LogDirectory, "tray-hook-endpoint.json"),
            JsonSerializer.Serialize(endpoint, JsonOptions));
    }

    private static void LogMissingSourceMetadataShape(string json, TrayIconHookMessage message)
    {
#if !DEBUG
        return;
#else
        if (message.SourceProcessId != 0 ||
            !string.IsNullOrWhiteSpace(message.SourceProcessName) ||
            !string.IsNullOrWhiteSpace(message.SourceProcessPath))
        {
            return;
        }

        var hasSourceProcessId = json.Contains("\"sourceProcessId\"", StringComparison.OrdinalIgnoreCase);
        var hasSourceProcessName = json.Contains("\"sourceProcessName\"", StringComparison.OrdinalIgnoreCase);
        var hasSourceProcessPath = json.Contains("\"sourceProcessPath\"", StringComparison.OrdinalIgnoreCase);
        var key = string.IsNullOrWhiteSpace(message.Guid)
            ? $"hwnd:{message.OwnerHwnd:X}:{message.IconId}"
            : $"guid:{message.Guid}";

        DebugLogger.WriteIfChanged(
            $"tray-hook-missing-source-{key}",
            "Tray hook message missing source metadata: " +
            $"Owner=0x{message.OwnerHwnd:X} IconId={message.IconId} Guid={message.Guid} " +
            $"HasSourceProcessId={hasSourceProcessId} " +
            $"HasSourceProcessName={hasSourceProcessName} " +
            $"HasSourceProcessPath={hasSourceProcessPath}");
#endif
    }

    private sealed record TrayIconHookEndpoint(
        int ProtocolVersion,
        string PipeName,
        string FullPipeName,
        int ProcessId,
        int SessionId);
}
