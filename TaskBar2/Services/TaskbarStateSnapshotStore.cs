using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskBar2.Services;

internal static class TaskbarStateSnapshotStore
{
    private const int TbpfNoProgress = 0;
    private const int TbpfIndeterminate = 1;
    private const int TbpfNormal = 2;
    private static readonly object Sync = new();
    private static readonly Dictionary<IntPtr, TaskbarButtonState> States = [];

    public static event EventHandler? StateChanged;

    public static event EventHandler<TaskbarStateChangedEventArgs>? StateChangedDetailed;

    public static bool Apply(TaskbarStateHookMessage message)
    {
        var hwnd = new IntPtr(message.Hwnd);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var changed = false;
        lock (Sync)
        {
            States.TryGetValue(hwnd, out var existing);
            var updated = existing ?? TaskbarButtonState.Empty;

            if (string.Equals(message.Operation, "SetOverlayIcon", StringComparison.OrdinalIgnoreCase))
            {
                var overlayBytes = DecodeIconBytes(message.OverlayPngBase64);
                updated = updated with
                {
                    OverlayIcon = DecodeIcon(overlayBytes),
                    OverlayPngBytes = overlayBytes,
                    OverlayDescription = message.OverlayDescription ?? ""
                };
            }
            else if (string.Equals(message.Operation, "SetProgressState", StringComparison.OrdinalIgnoreCase))
            {
                updated = updated with
                {
                    ProgressState = message.ProgressState,
                    ProgressCompleted = message.ProgressState == TbpfNoProgress ? 0UL : updated.ProgressCompleted,
                    ProgressTotal = message.ProgressState == TbpfNoProgress ? 0UL : updated.ProgressTotal
                };
            }
            else if (string.Equals(message.Operation, "SetProgressValue", StringComparison.OrdinalIgnoreCase))
            {
                updated = updated with
                {
                    ProgressState = updated.ProgressState is TbpfNoProgress or TbpfIndeterminate
                        ? TbpfNormal
                        : updated.ProgressState,
                    ProgressCompleted = message.ProgressCompleted,
                    ProgressTotal = message.ProgressTotal
                };
            }
            else
            {
                return false;
            }

            if (updated == TaskbarButtonState.Empty)
            {
                changed = States.Remove(hwnd);
            }
            else if (!Equals(existing, updated))
            {
                States[hwnd] = updated;
                changed = true;
            }
        }

        if (changed)
        {
            DebugLogger.WriteIfChanged(
                $"taskbar-state-{message.Hwnd}",
                "Taskbar state updated: " +
                $"Operation={message.Operation} Hwnd=0x{message.Hwnd:X} " +
                $"ProgressState={message.ProgressState} Progress={message.ProgressCompleted}/{message.ProgressTotal} " +
                $"HasOverlay={!string.IsNullOrWhiteSpace(message.OverlayPngBase64)} OverlayDescription={message.OverlayDescription} " +
                $"SourcePid={message.SourceProcessId} Process={message.SourceProcessName} Path={message.SourceProcessPath}");
            StateChanged?.Invoke(null, EventArgs.Empty);
            StateChangedDetailed?.Invoke(null, new TaskbarStateChangedEventArgs(hwnd, message.Operation ?? ""));
        }

        return changed;
    }

    public static bool TryGetState(IntPtr hwnd, out TaskbarButtonState state)
    {
        lock (Sync)
        {
            if (States.TryGetValue(hwnd, out var existing))
            {
                state = existing;
                return true;
            }

            state = TaskbarButtonState.Empty;
            return false;
        }
    }

    private static byte[]? DecodeIconBytes(string? iconPngBase64)
    {
        if (string.IsNullOrWhiteSpace(iconPngBase64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(iconPngBase64);
        }
        catch (FormatException exception)
        {
            DebugLogger.WriteIfChanged(
                "taskbar-state-decode-error",
                $"Taskbar overlay icon decode failed: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    private static ImageSource? DecodeIcon(byte[]? iconPngBytes)
    {
        if (iconPngBytes is null || iconPngBytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(iconPngBytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or COMException)
        {
            DebugLogger.WriteIfChanged(
                "taskbar-state-decode-error",
                $"Taskbar overlay icon decode failed: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }
}

internal sealed record TaskbarStateChangedEventArgs(IntPtr Hwnd, string Operation);

public sealed record TaskbarButtonState(
    ImageSource? OverlayIcon,
    byte[]? OverlayPngBytes,
    string OverlayDescription,
    int ProgressState,
    ulong ProgressCompleted,
    ulong ProgressTotal)
{
    public static TaskbarButtonState Empty { get; } = new(null, null, "", 0, 0, 0);
}

internal sealed class TaskbarStateHookMessage
{
    public int ProtocolVersion { get; set; } = 1;

    public string? MessageType { get; set; }

    public string? Operation { get; set; }

    public long Hwnd { get; set; }

    public int ProgressState { get; set; }

    public ulong ProgressCompleted { get; set; }

    public ulong ProgressTotal { get; set; }

    public string? OverlayPngBase64 { get; set; }

    public string? OverlayDescription { get; set; }

    public int SourceProcessId { get; set; }

    public string? SourceProcessName { get; set; }

    public string? SourceProcessPath { get; set; }
}
