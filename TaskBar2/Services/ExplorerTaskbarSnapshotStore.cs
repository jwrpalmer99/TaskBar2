using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TaskBar2.Models;
using TaskBar2.Native;

namespace TaskBar2.Services;

internal static class ExplorerTaskbarSnapshotStore
{
    private const int MinimumUsableButtonImageBytes = 512;
    private static readonly object Sync = new();
    private static IReadOnlyList<ExplorerTaskbarButtonItem> Buttons = Array.Empty<ExplorerTaskbarButtonItem>();
    private static readonly Dictionary<string, string> InvalidatedImageFingerprintsByGroupKey = [];
    private static string _lastSignature = "";
    private static DateTimeOffset _lastProxyRefreshRequested = DateTimeOffset.MinValue;

    public static event EventHandler? SnapshotChanged;

    public static void Apply(ExplorerTaskbarSnapshotMessage message)
    {
        var buttons = message.Buttons
            .Where(button => button.Right > button.Left && button.Bottom > button.Top)
            .Where(IsUsableTaskbarButton)
            .Select(button => new ExplorerTaskbarButtonItem(
                button.RuntimeId ?? "",
                button.Name ?? "",
                button.ClassName ?? "",
                button.AutomationId ?? "",
                button.ControlType ?? "",
                button.NativeWindowHandle,
                new IntPtr(button.RootHwnd),
                button.RootClassName ?? "",
                button.Left,
                button.Top,
                button.Right,
                button.Bottom,
                DecodeBytes(button.ButtonIconPngBase64),
                button.ButtonIconFingerprint ?? ""))
            .ToArray();

        var signature = BuildSignature(message, buttons);
        int retainedCount;
        lock (Sync)
        {
            retainedCount = Buttons.Count;
            if (buttons.Length == 0 && retainedCount > 0)
            {
                if (signature != _lastSignature)
                {
                    _lastSignature = signature;
                    DebugLogger.WriteIfChanged(
                        "explorer-taskbar-snapshot-retained",
                        "Explorer taskbar snapshot ignored because it had no usable app buttons: " +
                        $"Roots={message.Roots.Count} RawButtons={message.Buttons.Count} RetainedButtons={retainedCount} Error={message.Error ?? ""} " +
                        $"RawItems={string.Join(" || ", message.Buttons.Take(12).Select(FormatRawButtonForLog))}");
                }

                return;
            }

            Buttons = buttons;
        }

        if (signature == _lastSignature)
        {
            return;
        }

        _lastSignature = signature;
        SnapshotChanged?.Invoke(null, EventArgs.Empty);
        DebugLogger.WriteIfChanged(
            "explorer-taskbar-snapshot",
            "Explorer taskbar snapshot: " +
            $"Roots={message.Roots.Count} Buttons={buttons.Length} Error={message.Error ?? ""} " +
            $"Items={string.Join(" || ", buttons.Take(24).Select(FormatButtonForLog))}");
    }

    public static bool TryForwardClick(IReadOnlyList<TaskbarItem> groupItems, bool rightClick)
    {
        var cursor = System.Windows.Forms.Control.MousePosition;
        return TryForwardClick(groupItems, rightClick, cursor.X, cursor.Y);
    }

    public static bool TryForwardClick(IReadOnlyList<TaskbarItem> groupItems, bool rightClick, int anchorX, int anchorY)
    {
        if ((!AppSettingsService.Current.EnableExperimentalExplorerTaskbarHook &&
             !AppSettingsService.Current.EnableExperimentalExplorerTaskbarMenuProxy) ||
            groupItems.Count == 0)
        {
            return false;
        }

        ExplorerTaskbarButtonItem[] buttons;
        lock (Sync)
        {
            buttons = Buttons.ToArray();
        }

        var match = FindUniqueMatch(groupItems, buttons);
        if (match is null)
        {
            DebugLogger.WriteIfChanged(
                $"explorer-taskbar-proxy-no-match-{BuildGroupKey(groupItems)}",
                "Explorer taskbar proxy skipped: " +
                $"Group={BuildGroupKey(groupItems)} Titles={string.Join(" | ", groupItems.Select(item => item.Title))} " +
                $"Processes={string.Join(" | ", groupItems.Select(item => item.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase))} " +
                $"Buttons={string.Join(" | ", buttons.Take(16).Select(button => button.Name))}");
            return false;
        }

        var groupKey = BuildGroupKey(groupItems);
        if (!rightClick)
        {
            if (!AppSettingsService.Current.EnableExperimentalExplorerTaskbarHook)
            {
                return false;
            }

            if (TrySendTaskbarButtonCommand(match, "ActivateTaskbarButton", anchorX, anchorY, out var activateDetail, responseTimeoutMs: 1200))
            {
                DebugLogger.WriteIfChanged(
                    $"explorer-taskbar-activate-sent-{groupKey}",
                    "Explorer taskbar activate proxy handled: " +
                    $"Group={groupKey} Button={match.Name} RuntimeId={match.RuntimeId} Root=0x{match.RootHwnd.ToInt64():X} Detail={activateDetail}");
                return true;
            }

            DebugLogger.WriteIfChanged(
                $"explorer-taskbar-activate-failed-{groupKey}",
                "Explorer taskbar activate proxy failed; falling back to direct window activation: " +
                $"Group={groupKey} Button={match.Name} Error={activateDetail}");
            RequestProxyRefresh($"activate failed for {groupKey}: {activateDetail}");
            return false;
        }

        if (!AppSettingsService.Current.EnableExperimentalExplorerTaskbarMenuProxy)
        {
            DebugLogger.WriteIfChanged(
                $"explorer-taskbar-context-menu-disabled-{groupKey}",
                "Explorer taskbar context-menu proxy disabled by setting: " +
                $"Group={BuildGroupKey(groupItems)} Button={match.Name}");
            return false;
        }

        if (!TrySendTaskbarButtonCommand(match, "ShowTaskbarContextMenu", anchorX, anchorY, out var contextError))
        {
            DebugLogger.WriteIfChanged(
                $"explorer-taskbar-context-menu-send-failed-{groupKey}",
                "Explorer taskbar context-menu proxy send failed: " +
                $"Group={groupKey} Button={match.Name} Anchor={anchorX},{anchorY} Error={contextError}");
            RequestProxyRefresh($"context menu failed for {groupKey}: {contextError}");
            return false;
        }

        DebugLogger.WriteIfChanged(
            $"explorer-taskbar-context-menu-sent-{groupKey}",
            "Explorer taskbar context-menu proxy sent: " +
            $"Group={groupKey} Button={match.Name} RuntimeId={match.RuntimeId} Root=0x{match.RootHwnd.ToInt64():X} Anchor={anchorX},{anchorY} Detail={contextError}");
        return true;
    }

    public static void InvalidateButtonImage(IReadOnlyList<TaskbarItem> groupItems, string reason)
    {
        if (groupItems.Count == 0)
        {
            return;
        }

        var groupKey = BuildGroupKey(groupItems);
        ExplorerTaskbarButtonItem[] buttons;
        lock (Sync)
        {
            buttons = Buttons.ToArray();
        }

        var match = FindUniqueMatch(groupItems, buttons);
        if (match?.ButtonIconPngBytes is null || match.ButtonIconPngBytes.Length == 0)
        {
            return;
        }

        var fingerprint = GetButtonImageFingerprint(match);
        lock (Sync)
        {
            InvalidatedImageFingerprintsByGroupKey[groupKey] = fingerprint;
        }

        DebugLogger.WriteIfChanged(
            $"explorer-button-image-invalidated-{groupKey}",
            "Explorer taskbar button image invalidated: " +
            $"Group={groupKey} Button={match.Name} Fingerprint={fingerprint} Reason={reason}");
    }

    public static bool TryGetButtonImage(IReadOnlyList<TaskbarItem> groupItems, out ExplorerTaskbarButtonImage image)
    {
        image = default;
        if (!AppSettingsService.Current.EnableExperimentalExplorerTaskbarHook || groupItems.Count == 0)
        {
            return false;
        }

        ExplorerTaskbarButtonItem[] buttons;
        lock (Sync)
        {
            buttons = Buttons.ToArray();
        }

        var match = FindUniqueMatch(groupItems, buttons);
        if (match?.ButtonIconPngBytes is null || match.ButtonIconPngBytes.Length == 0)
        {
            return false;
        }

        var fingerprint = GetButtonImageFingerprint(match);
        var groupKey = BuildGroupKey(groupItems);
        lock (Sync)
        {
            if (InvalidatedImageFingerprintsByGroupKey.TryGetValue(groupKey, out var invalidatedFingerprint))
            {
                if (string.Equals(invalidatedFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    DebugLogger.WriteIfChanged(
                        $"explorer-button-image-skipped-invalidated-{groupKey}",
                        "Explorer taskbar button image skipped because it was invalidated by app taskbar state: " +
                        $"Group={groupKey} Button={match.Name} Fingerprint={fingerprint}");
                    return false;
                }

                InvalidatedImageFingerprintsByGroupKey.Remove(groupKey);
            }
        }

        if (match.ButtonIconPngBytes.Length < MinimumUsableButtonImageBytes)
        {
            DebugLogger.WriteIfChanged(
                $"explorer-button-image-rejected-{groupKey}",
                "Explorer taskbar button image rejected because it is too small to be a real icon capture: " +
                $"Group={groupKey} Button={match.Name} Bytes={match.ButtonIconPngBytes.Length} Fingerprint={match.ButtonIconFingerprint}");
            return false;
        }

        image = new ExplorerTaskbarButtonImage(
            match.ButtonIconPngBytes,
            fingerprint,
            match.Name);
        return true;
    }

    private static string GetButtonImageFingerprint(ExplorerTaskbarButtonItem button) =>
        string.IsNullOrWhiteSpace(button.ButtonIconFingerprint)
            ? button.RuntimeId
            : button.ButtonIconFingerprint;

    private static ExplorerTaskbarButtonItem? FindUniqueMatch(
        IReadOnlyList<TaskbarItem> groupItems,
        IReadOnlyList<ExplorerTaskbarButtonItem> buttons)
    {
        var scored = buttons
            .Select(button => new ScoredExplorerButton(button, ScoreButton(groupItems, button)))
            .Where(scoredButton => scoredButton.Score >= 60)
            .OrderByDescending(scoredButton => scoredButton.Score)
            .ToArray();

        if (scored.Length == 0)
        {
            return null;
        }

        if (scored.Length > 1 && scored[0].Score == scored[1].Score)
        {
            return null;
        }

        return scored[0].Button;
    }

    private static int ScoreButton(IReadOnlyList<TaskbarItem> groupItems, ExplorerTaskbarButtonItem button)
    {
        var buttonName = Normalize(button.Name);
        if (buttonName.Length == 0)
        {
            return 0;
        }

        var best = 0;
        foreach (var item in groupItems)
        {
            var title = Normalize(item.Title);
            if (title.Length >= 6)
            {
                if (buttonName.Equals(title, StringComparison.Ordinal))
                {
                    best = Math.Max(best, 120);
                }
                else if (buttonName.Contains(title))
                {
                    best = Math.Max(best, 100);
                }
                else if (title.Contains(buttonName) && buttonName.Length >= 6)
                {
                    best = Math.Max(best, 80);
                }
            }

            var processName = Normalize(Path.GetFileNameWithoutExtension(item.ProcessName));
            if (processName.Length >= 4 && buttonName.Contains(processName))
            {
                best = Math.Max(best, 75);
            }

            foreach (var alias in GetProcessAliases(processName))
            {
                if (buttonName.Contains(alias))
                {
                    best = Math.Max(best, 85);
                }
            }

            foreach (var token in SplitTokens(item.AppUserModelId))
            {
                if (buttonName.Contains(token))
                {
                    best = Math.Max(best, 65);
                }
            }
        }

        return best;
    }

    private static IEnumerable<string> GetProcessAliases(string processName)
    {
        switch (processName)
        {
            case "devenv":
                yield return "visual studio";
                break;
            case "code":
                yield return "visual studio code";
                break;
            case "explorer":
                yield return "file explorer";
                break;
            case "msedge":
                yield return "edge";
                yield return "microsoft edge";
                break;
            case "chrome":
                yield return "chrome";
                yield return "google chrome";
                break;
            case "discord":
                yield return "discord";
                break;
            case "mailbird":
                yield return "mailbird";
                break;
            case "processlasso":
                yield return "process lasso";
                break;
        }
    }

    private static IEnumerable<string> SplitTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var token in value.Split(['.', '!', '_', '-', ' ', '\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = Normalize(token);
            if (normalized.Length >= 4)
            {
                yield return normalized;
            }
        }
    }

    private static string Normalize(string value) =>
        string.Join(
            " ",
            value.Trim()
                .ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string BuildSignature(
        ExplorerTaskbarSnapshotMessage message,
        IReadOnlyList<ExplorerTaskbarButtonItem> buttons) =>
        $"{message.Error}|{string.Join("|", buttons.Select(button => $"{button.RuntimeId}:{button.Name}:{button.Left},{button.Top},{button.Right},{button.Bottom}:{button.ButtonIconFingerprint}"))}";

    private static string FormatButtonForLog(ExplorerTaskbarButtonItem button) =>
        $"{button.Name} [{button.ControlType}/{button.ClassName}/{button.AutomationId}] Rect={button.Left},{button.Top},{button.Right},{button.Bottom} ExplorerIcon={button.ButtonIconPngBytes is { Length: > 0 }}";

    private static string FormatRawButtonForLog(ExplorerTaskbarButtonSnapshotMessage button) =>
        $"{button.Name} [{button.ControlType}/{button.ClassName}/{button.AutomationId}] Rect={button.Left},{button.Top},{button.Right},{button.Bottom} ExplorerIcon={!string.IsNullOrWhiteSpace(button.ButtonIconPngBase64)}";

    private static bool IsUsableTaskbarButton(ExplorerTaskbarButtonSnapshotMessage button)
    {
        var controlType = button.ControlType ?? "";
        var className = button.ClassName ?? "";
        var name = button.Name ?? "";

        if (!string.Equals(controlType, "ControlType.Button", StringComparison.Ordinal) &&
            !string.Equals(controlType, "ControlType.ListItem", StringComparison.Ordinal) &&
            !string.Equals(controlType, "ControlType.MenuItem", StringComparison.Ordinal))
        {
            return false;
        }

        if (className.StartsWith("SystemTray.", StringComparison.Ordinal) ||
            className.IndexOf("Tray", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        return !name.StartsWith("Running applications", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildGroupKey(IReadOnlyList<TaskbarItem> groupItems) =>
        groupItems[0].GroupKey;

    private static bool TryPostTaskbarLeftClick(ExplorerTaskbarButtonItem button, out string detail)
    {
        var screenX = (button.Left + button.Right) / 2;
        var screenY = (button.Top + button.Bottom) / 2;
        var point = new NativeMethods.Point
        {
            X = screenX,
            Y = screenY
        };

        if (!NativeMethods.ScreenToClient(button.RootHwnd, ref point))
        {
            detail = $"ScreenPoint={screenX},{screenY} ScreenToClientLastError={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
            return false;
        }

        var lParam = MakeLParam(point.X, point.Y);
        var down = NativeMethods.PostMessage(
            button.RootHwnd,
            NativeMethods.WM_LBUTTONDOWN,
            new IntPtr(NativeMethods.MK_LBUTTON),
            lParam);
        var up = NativeMethods.PostMessage(
            button.RootHwnd,
            NativeMethods.WM_LBUTTONUP,
            IntPtr.Zero,
            lParam);
        detail = $"ScreenPoint={screenX},{screenY} ClientPoint={point.X},{point.Y} Down={down} Up={up} LastError={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
        return down && up;
    }

    private static IntPtr MakeLParam(int x, int y) =>
        new(unchecked((int)(((ushort)y << 16) | (ushort)x)));

    private static bool TrySendTaskbarButtonCommand(
        ExplorerTaskbarButtonItem button,
        string operation,
        int anchorX,
        int anchorY,
        out string error,
        int responseTimeoutMs = 1800)
    {
        error = "";
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                GetCommandPipeName(),
                PipeDirection.InOut,
                PipeOptions.None);
            pipe.Connect(250);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
            {
                AutoFlush = true
            };
            writer.WriteLine(JsonSerializer.Serialize(new ExplorerTaskbarMenuCommand
            {
                ProtocolVersion = 1,
                Operation = operation,
                RuntimeId = button.RuntimeId,
                Name = button.Name,
                RootHwnd = button.RootHwnd.ToInt64(),
                NativeWindowHandle = button.NativeWindowHandle,
                Left = button.Left,
                Top = button.Top,
                Right = button.Right,
                Bottom = button.Bottom,
                AnchorX = anchorX,
                AnchorY = anchorY
            }));

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var responseTask = reader.ReadLineAsync();
            if (!responseTask.Wait(responseTimeoutMs))
            {
                error = "Timed out waiting for Explorer taskbar command response.";
                return false;
            }

            var responseLine = responseTask.Result;
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                error = "Explorer taskbar command response was empty.";
                return false;
            }

            var response = JsonSerializer.Deserialize<ExplorerTaskbarCommandResponse>(responseLine);
            if (response is null)
            {
                error = $"Explorer taskbar command response was invalid: {responseLine}";
                return false;
            }

            error = response.Detail ?? "";
            return response.Handled;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException or JsonException or AggregateException)
        {
            error = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static void RequestProxyRefresh(string reason)
    {
        var shouldRefresh = false;
        lock (Sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastProxyRefreshRequested >= TimeSpan.FromSeconds(10))
            {
                _lastProxyRefreshRequested = now;
                Buttons = Array.Empty<ExplorerTaskbarButtonItem>();
                _lastSignature = "";
                shouldRefresh = true;
            }
        }

        if (!shouldRefresh)
        {
            return;
        }

        DebugLogger.Write($"Explorer taskbar proxy refresh requested: {reason}");
        AppCommands.RestartHooks();
        SnapshotChanged?.Invoke(null, EventArgs.Empty);
    }

    private static string GetCommandPipeName() =>
        $"TaskBar2.ExplorerTaskbarCommand.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    private static byte[]? DecodeBytes(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed record ScoredExplorerButton(ExplorerTaskbarButtonItem Button, int Score);
}

internal sealed record ExplorerTaskbarButtonItem(
    string RuntimeId,
    string Name,
    string ClassName,
    string AutomationId,
    string ControlType,
    int NativeWindowHandle,
    IntPtr RootHwnd,
    string RootClassName,
    int Left,
    int Top,
    int Right,
    int Bottom,
    byte[]? ButtonIconPngBytes,
    string ButtonIconFingerprint);

internal sealed class ExplorerTaskbarMenuCommand
{
    public int ProtocolVersion { get; set; }

    public string Operation { get; set; } = "";

    public string RuntimeId { get; set; } = "";

    public string Name { get; set; } = "";

    public long RootHwnd { get; set; }

    public int NativeWindowHandle { get; set; }

    public int Left { get; set; }

    public int Top { get; set; }

    public int Right { get; set; }

    public int Bottom { get; set; }

    public int AnchorX { get; set; }

    public int AnchorY { get; set; }
}

internal sealed class ExplorerTaskbarCommandResponse
{
    public bool Handled { get; set; }

    public string? Detail { get; set; }
}

internal readonly record struct ExplorerTaskbarButtonImage(
    byte[] PngBytes,
    string Fingerprint,
    string ButtonName);

internal sealed class ExplorerTaskbarSnapshotMessage
{
    public int ProtocolVersion { get; set; }

    public string? MessageType { get; set; }

    public int SourceProcessId { get; set; }

    public string? SourceProcessName { get; set; }

    public string? SourceProcessPath { get; set; }

    public string? CapturedAtUtc { get; set; }

    public string? Error { get; set; }

    public List<ExplorerTaskbarRootSnapshotMessage> Roots { get; set; } = [];

    public List<ExplorerTaskbarButtonSnapshotMessage> Buttons { get; set; } = [];
}

internal sealed class ExplorerTaskbarRootSnapshotMessage
{
    public long Hwnd { get; set; }

    public string? ClassName { get; set; }

    public int Left { get; set; }

    public int Top { get; set; }

    public int Right { get; set; }

    public int Bottom { get; set; }
}

internal sealed class ExplorerTaskbarButtonSnapshotMessage
{
    public string? RuntimeId { get; set; }

    public string? Name { get; set; }

    public string? ClassName { get; set; }

    public string? AutomationId { get; set; }

    public string? ControlType { get; set; }

    public int NativeWindowHandle { get; set; }

    public long RootHwnd { get; set; }

    public string? RootClassName { get; set; }

    public int Left { get; set; }

    public int Top { get; set; }

    public int Right { get; set; }

    public int Bottom { get; set; }

    public string? ButtonIconPngBase64 { get; set; }

    public string? ButtonIconFingerprint { get; set; }
}
