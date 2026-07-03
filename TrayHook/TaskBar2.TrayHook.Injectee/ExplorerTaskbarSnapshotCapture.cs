using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TaskBar2.TrayHook.Injectee;

internal static class ExplorerTaskbarSnapshotCapture
{
    private const int CaptureIntervalMs = 2000;
    private const int MaxDepth = 14;
    private const int MaxNodes = 600;
    private const int MaxButtons = 96;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const int MkLButton = 0x0001;
    private static readonly string[] TaskbarRootClasses = ["Shell_TrayWnd", "Shell_SecondaryTrayWnd"];
    private static readonly string[] ExcludedNamePrefixes =
    [
        "Start",
        "Search",
        "Task view",
        "Widgets",
        "Copilot",
        "Show hidden icons",
        "Show desktop",
        "Notification center",
        "Quick settings",
        "Running applications",
        "Clock "
    ];

    private static readonly object Sync = new();
    private static ManualResetEvent? _stopEvent;
    private static Thread? _thread;
    private static ManualResetEvent? _commandStopEvent;
    private static Thread? _commandThread;
    private static string _lastSignature = "";
    private static string _pauseEventName = "";
    private static bool _enableButtonImageCapture;

    public static void StartIfExplorer(bool enableButtonImageCapture, string pauseEventName)
    {
        if (!string.Equals(Process.GetCurrentProcess().ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (Sync)
        {
            _enableButtonImageCapture = enableButtonImageCapture;
            _pauseEventName = pauseEventName;
            if (_thread is not null)
            {
                return;
            }

            _stopEvent = new ManualResetEvent(false);
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "TaskBar2 Explorer taskbar snapshot"
            };
            _thread.Start(_stopEvent);

            _commandStopEvent = new ManualResetEvent(false);
            _commandThread = new Thread(RunCommandServer)
            {
                IsBackground = true,
                Name = "TaskBar2 Explorer taskbar command"
            };
            _commandThread.Start(_commandStopEvent);
        }
    }

    public static void Stop()
    {
        Thread? thread;
        ManualResetEvent? stopEvent;
        Thread? commandThread;
        ManualResetEvent? commandStopEvent;
        lock (Sync)
        {
            thread = _thread;
            stopEvent = _stopEvent;
            commandThread = _commandThread;
            commandStopEvent = _commandStopEvent;
            _thread = null;
            _stopEvent = null;
            _commandThread = null;
            _commandStopEvent = null;
        }

        try
        {
            stopEvent?.Set();
            commandStopEvent?.Set();
            thread?.Join(1000);
            commandThread?.Join(1000);
        }
        catch
        {
        }
        finally
        {
            stopEvent?.Dispose();
            commandStopEvent?.Dispose();
        }
    }

    private static void Run(object? state)
    {
        if (state is not WaitHandle stopEvent)
        {
            return;
        }

        using var pauseEvent = OpenOptionalEvent(_pauseEventName);
        while (!stopEvent.WaitOne(0))
        {
            if (pauseEvent is not null && pauseEvent.WaitOne(0))
            {
                stopEvent.WaitOne(500);
                continue;
            }

            CaptureOnce();
            stopEvent.WaitOne(CaptureIntervalMs);
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
            WriteLog($"Optional pause event not found. Event={eventName}");
            return null;
        }
    }

    private static void RunCommandServer(object? state)
    {
        if (state is not WaitHandle stopEvent)
        {
            return;
        }

        var pipeName = GetCommandPipeName();
        WriteLog($"Explorer taskbar command server starting. Pipe={pipeName}");
        while (!stopEvent.WaitOne(0))
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                var wait = pipe.BeginWaitForConnection(null, null);
                while (!wait.AsyncWaitHandle.WaitOne(100))
                {
                    if (stopEvent.WaitOne(0))
                    {
                        return;
                    }
                }

                pipe.EndWaitForConnection(wait);
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var response = HandleCommand(line);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
                {
                    AutoFlush = true
                };
                writer.WriteLine(response);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (IOException exception)
            {
                WriteLog($"Explorer taskbar command pipe IO error: {exception.Message}");
            }
            catch (Exception exception)
            {
                WriteLog($"Explorer taskbar command error: {exception.GetType().Name}: {exception.Message}");
                Thread.Sleep(250);
            }
        }
    }

    private static string HandleCommand(string json)
    {
        var command = ExplorerTaskbarMenuCommand.TryParse(json);
        if (command is null)
        {
            WriteLog($"Explorer taskbar command ignored: invalid payload. Bytes={json.Length}");
            return CreateCommandResponse(false, "Invalid command payload.");
        }

        if (string.Equals(command.Operation, "ActivateTaskbarButton", StringComparison.OrdinalIgnoreCase))
        {
            var activateHandled = TryActivateTaskbarButton(command, out var activateDetail);
            WriteLog($"Explorer taskbar activate command handled={activateHandled}. {activateDetail}");
            return CreateCommandResponse(activateHandled, activateDetail);
        }

        WriteLog($"Explorer taskbar command ignored: unsupported operation {command.Operation}");
        return CreateCommandResponse(false, $"Unsupported operation {command.Operation}.");
    }

    private static bool TryActivateTaskbarButton(ExplorerTaskbarMenuCommand command, out string detail)
    {
        detail = "";
        if (!UiaAdapter.TryEnsureLoaded(out var error))
        {
            detail = error;
            return false;
        }

        var match = FindCommandTarget(command);
        if (match is null)
        {
            detail =
                $"No matching Explorer taskbar button. RuntimeId={command.RuntimeId} Name={command.Name} Root=0x{command.RootHwnd:X}";
            return false;
        }

        if (UiaAdapter.TryInvoke(match.Element, out var invokeDetail))
        {
            detail = $"Invoked UIA taskbar button. Button={match.Current.Name} Root=0x{match.Root.Hwnd.ToInt64():X}";
            return true;
        }

        if (TryPostLeftClick(match, out var postDetail))
        {
            detail =
                $"Posted taskbar left-click after UIA invoke failed. Button={match.Current.Name} Root=0x{match.Root.Hwnd.ToInt64():X} Invoke={invokeDetail} {postDetail}";
            return true;
        }

        detail =
            $"Taskbar activate failed. Button={match.Current.Name} Root=0x{match.Root.Hwnd.ToInt64():X} Invoke={invokeDetail} {postDetail}";
        return false;
    }

    private static string CreateCommandResponse(bool handled, string detail) =>
        "{\"Handled\":" + (handled ? "true" : "false") + ",\"Detail\":\"" + EscapeJson(detail) + "\"}";

    private static string EscapeJson(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var builder = new StringBuilder(value.Length + 16);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                default:
                    if (ch < ' ')
                    {
                        builder.Append("\\u");
                        builder.Append(((int)ch).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private static bool TryPostLeftClick(ExplorerCommandTarget match, out string detail)
    {
        var centerX = (match.Current.BoundingRectangle.Left + match.Current.BoundingRectangle.Right) / 2.0;
        var centerY = (match.Current.BoundingRectangle.Top + match.Current.BoundingRectangle.Bottom) / 2.0;
        var screenPoint = new NativePoint
        {
            X = (int)Math.Round(centerX),
            Y = (int)Math.Round(centerY)
        };
        var clientPoint = screenPoint;
        if (!ScreenToClient(match.Root.Hwnd, ref clientPoint))
        {
            detail =
                $"ScreenToClient failed. ScreenPoint={screenPoint.X},{screenPoint.Y} LastError={Marshal.GetLastWin32Error()}";
            return false;
        }

        var lParam = MakeLParam(clientPoint.X, clientPoint.Y);
        var down = PostMessage(match.Root.Hwnd, WmLButtonDown, new IntPtr(MkLButton), lParam);
        var up = PostMessage(match.Root.Hwnd, WmLButtonUp, IntPtr.Zero, lParam);
        detail =
            $"ScreenPoint={screenPoint.X},{screenPoint.Y} ClientPoint={clientPoint.X},{clientPoint.Y} Down={down} Up={up} LastError={Marshal.GetLastWin32Error()}";
        return down && up;
    }

    private static ExplorerCommandTarget? FindCommandTarget(ExplorerTaskbarMenuCommand command)
    {
        foreach (var root in FindTaskbarRoots())
        {
            if (command.RootHwnd != 0 && root.Hwnd.ToInt64() != command.RootHwnd)
            {
                continue;
            }

            object? rootElement;
            try
            {
                rootElement = UiaAdapter.FromHandle(root.Hwnd);
            }
            catch
            {
                continue;
            }

            if (rootElement is null)
            {
                continue;
            }

            var visited = 0;
            var match = FindCommandTarget(rootElement, root, command, depth: 0, ref visited);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static ExplorerCommandTarget? FindCommandTarget(
        object element,
        ExplorerTaskbarRoot root,
        ExplorerTaskbarMenuCommand command,
        int depth,
        ref int visited)
    {
        if (depth > MaxDepth || visited >= MaxNodes)
        {
            return null;
        }

        visited++;
        UiaElementInfo current;
        try
        {
            current = UiaAdapter.GetCurrent(element);
        }
        catch
        {
            return null;
        }

        if (IsCommandMatch(element, current, command))
        {
            return new ExplorerCommandTarget(root, element, current);
        }

        object? child;
        try
        {
            child = UiaAdapter.GetFirstChild(element);
        }
        catch
        {
            return null;
        }

        while (child is not null && visited < MaxNodes)
        {
            var match = FindCommandTarget(child, root, command, depth + 1, ref visited);
            if (match is not null)
            {
                return match;
            }

            try
            {
                child = UiaAdapter.GetNextSibling(child);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static bool IsCommandMatch(object element, UiaElementInfo current, ExplorerTaskbarMenuCommand command)
    {
        if (!IsTaskbarButtonCandidate(current))
        {
            return false;
        }

        var runtimeId = GetRuntimeId(element);
        if (!string.IsNullOrWhiteSpace(command.RuntimeId) &&
            string.Equals(runtimeId, command.RuntimeId, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(command.Name) ||
            !string.Equals(current.Name ?? "", command.Name, StringComparison.Ordinal))
        {
            return false;
        }

        var left = (int)Math.Round(current.BoundingRectangle.Left);
        var top = (int)Math.Round(current.BoundingRectangle.Top);
        var right = (int)Math.Round(current.BoundingRectangle.Right);
        var bottom = (int)Math.Round(current.BoundingRectangle.Bottom);
        return Math.Abs(left - command.Left) <= 4 &&
               Math.Abs(top - command.Top) <= 4 &&
               Math.Abs(right - command.Right) <= 4 &&
               Math.Abs(bottom - command.Bottom) <= 4;
    }

    private static void CaptureOnce()
    {
        var buttons = new List<ExplorerTaskbarButtonSnapshot>();
        var roots = FindTaskbarRoots();
        var error = "";
        if (!UiaAdapter.TryEnsureLoaded(out error))
        {
            var errorSignature = BuildSignature(roots, buttons, error);
            if (!string.Equals(errorSignature, _lastSignature, StringComparison.Ordinal))
            {
                _lastSignature = errorSignature;
                TrayIconHookEntryPoint.EnqueueMessage(ToJson(roots, buttons, error));
            }

            return;
        }

        foreach (var root in roots)
        {
            try
            {
                var rootElement = UiaAdapter.FromHandle(root.Hwnd);
                if (rootElement is null)
                {
                    continue;
                }

                var visitedNodes = 0;
                CollectButtons(rootElement, root, buttons, depth: 0, ref visitedNodes);
            }
            catch (Exception exception) when (exception is InvalidOperationException or COMException or TargetInvocationException)
            {
                error = $"{exception.GetType().Name}: {exception.Message}";
            }
        }

        var signature = BuildSignature(roots, buttons, error);
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastSignature = signature;
        TrayIconHookEntryPoint.EnqueueMessage(ToJson(roots, buttons, error));
    }

    private static void CollectButtons(
        object element,
        ExplorerTaskbarRoot root,
        List<ExplorerTaskbarButtonSnapshot> buttons,
        int depth,
        ref int visitedNodes)
    {
        if (depth > MaxDepth || visitedNodes >= MaxNodes || buttons.Count >= MaxButtons)
        {
            return;
        }

        visitedNodes++;
        TryAddButton(element, root, buttons);

        object? child = null;
        try
        {
            child = UiaAdapter.GetFirstChild(element);
        }
        catch (TargetInvocationException)
        {
            return;
        }

        while (child is not null && visitedNodes < MaxNodes && buttons.Count < MaxButtons)
        {
            CollectButtons(child, root, buttons, depth + 1, ref visitedNodes);
            try
            {
                child = UiaAdapter.GetNextSibling(child);
            }
            catch (TargetInvocationException)
            {
                return;
            }
        }
    }

    private static void TryAddButton(
        object element,
        ExplorerTaskbarRoot root,
        List<ExplorerTaskbarButtonSnapshot> buttons)
    {
        UiaElementInfo current;
        try
        {
            current = UiaAdapter.GetCurrent(element);
        }
        catch (TargetInvocationException)
        {
            return;
        }

        if (!IsTaskbarButtonCandidate(current))
        {
            return;
        }

        if (current.BoundingRectangle.IsEmpty ||
            current.BoundingRectangle.Width <= 0 ||
            current.BoundingRectangle.Height <= 0)
        {
            return;
        }

        var buttonImage = _enableButtonImageCapture
            ? CaptureButtonImage(current.BoundingRectangle)
            : ButtonImageCapture.Empty;

        buttons.Add(new ExplorerTaskbarButtonSnapshot(
            GetRuntimeId(element),
            current.Name ?? "",
            current.ClassName ?? "",
            current.AutomationId ?? "",
            current.ControlType ?? "",
            current.NativeWindowHandle,
            root.Hwnd,
            root.ClassName,
            (int)Math.Round(current.BoundingRectangle.Left),
            (int)Math.Round(current.BoundingRectangle.Top),
            (int)Math.Round(current.BoundingRectangle.Right),
            (int)Math.Round(current.BoundingRectangle.Bottom),
            buttonImage.PngBase64,
            buttonImage.Fingerprint));
    }

    private static bool IsTaskbarButtonCandidate(UiaElementInfo current)
    {
        var controlType = current.ControlType;
        var className = current.ClassName ?? "";
        var automationId = current.AutomationId ?? "";
        var name = current.Name ?? "";

        if (className.StartsWith("SystemTray.", StringComparison.Ordinal) ||
            className.IndexOf("Tray", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        if (ExcludedNamePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.Equals(controlType, "ControlType.Button", StringComparison.Ordinal) &&
            !string.Equals(controlType, "ControlType.ListItem", StringComparison.Ordinal) &&
            !string.Equals(controlType, "ControlType.MenuItem", StringComparison.Ordinal))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(name) ||
               className.IndexOf("Task", StringComparison.OrdinalIgnoreCase) >= 0 ||
               automationId.IndexOf("Task", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static IReadOnlyList<ExplorerTaskbarRoot> FindTaskbarRoots()
    {
        var roots = new List<ExplorerTaskbarRoot>();
        EnumWindows((hwnd, _) =>
        {
            var className = GetClassName(hwnd);
            if (!TaskbarRootClasses.Contains(className, StringComparer.Ordinal))
            {
                return true;
            }

            GetWindowRect(hwnd, out var rect);
            roots.Add(new ExplorerTaskbarRoot(
                hwnd,
                className,
                rect.Left,
                rect.Top,
                rect.Right,
                rect.Bottom));
            return true;
        }, IntPtr.Zero);

        return roots;
    }

    private static string GetCommandPipeName() =>
        $"TaskBar2.ExplorerTaskbarCommand.{Process.GetCurrentProcess().SessionId}";

    private static IntPtr MakeLParam(int x, int y) =>
        new(unchecked((int)(((ushort)y << 16) | (ushort)x)));

    private static void WriteLog(string message)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TaskBar2");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "tray-hook-injectee.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Process.GetCurrentProcess().Id}:{Process.GetCurrentProcess().ProcessName}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static string GetRuntimeId(object element)
    {
        try
        {
            return string.Join(".", UiaAdapter.GetRuntimeId(element) ?? Array.Empty<int>());
        }
        catch
        {
            return "";
        }
    }

    private static string BuildSignature(
        IReadOnlyList<ExplorerTaskbarRoot> roots,
        IReadOnlyList<ExplorerTaskbarButtonSnapshot> buttons,
        string error)
    {
        var builder = new StringBuilder();
        builder.Append(error);
        foreach (var root in roots)
        {
            builder.Append('|').Append(root.Hwnd.ToInt64()).Append(':').Append(root.Left).Append(',').Append(root.Top);
        }

        foreach (var button in buttons)
        {
            builder.Append('|')
                .Append(button.RuntimeId)
                .Append(':')
                .Append(button.Name)
                .Append(':')
                .Append(button.Left)
                .Append(',')
                .Append(button.Top)
                .Append(',')
                .Append(button.Right)
                .Append(',')
                .Append(button.Bottom);
            if (!string.IsNullOrWhiteSpace(button.ButtonIconFingerprint))
            {
                builder.Append(':').Append(button.ButtonIconFingerprint);
            }
        }

        return builder.ToString();
    }

    private static ButtonImageCapture CaptureButtonImage(UiaRect boundingRectangle)
    {
        try
        {
            var buttonWidth = (int)Math.Round(boundingRectangle.Right - boundingRectangle.Left);
            var buttonHeight = (int)Math.Round(boundingRectangle.Bottom - boundingRectangle.Top);
            if (buttonWidth < 16 || buttonHeight < 16)
            {
                return ButtonImageCapture.Empty;
            }

            var captureSize = Math.Min(48, Math.Min(buttonWidth - 8, buttonHeight - 12));
            captureSize = Math.Max(16, captureSize);
            var sourceX = (int)Math.Round(boundingRectangle.Left + (buttonWidth - captureSize) / 2.0);
            var sourceY = (int)Math.Round(boundingRectangle.Top + (buttonHeight - captureSize) / 2.0);

            using var bitmap = new Bitmap(captureSize, captureSize, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    sourceX,
                    sourceY,
                    0,
                    0,
                    new Size(captureSize, captureSize),
                    CopyPixelOperation.SourceCopy);
            }

            var trimmedBitmap = TryTrimBackground(bitmap);
            if (trimmedBitmap is null)
            {
                return ButtonImageCapture.Empty;
            }

            using (trimmedBitmap)
            using (var squareBitmap = PadToSquare(trimmedBitmap))
            using (var stream = new MemoryStream())
            {
                squareBitmap.Save(stream, ImageFormat.Png);
                var bytes = stream.ToArray();
                return new ButtonImageCapture(
                    Convert.ToBase64String(bytes),
                    ComputeFingerprint(bytes));
            }
        }
        catch
        {
            return ButtonImageCapture.Empty;
        }
    }

    private static Bitmap? TryTrimBackground(Bitmap source)
    {
        var background = AverageCornerColor(source);
        var left = source.Width;
        var top = source.Height;
        var right = -1;
        var bottom = -1;
        var foregroundPixels = 0;

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = source.GetPixel(x, y);
                if (pixel.A <= 12 || ColorDistance(pixel, background) <= 22)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
                foregroundPixels++;
            }
        }

        var foregroundWidth = right - left + 1;
        var foregroundHeight = bottom - top + 1;
        if (right < left ||
            bottom < top ||
            foregroundPixels < 48 ||
            foregroundWidth < 12 ||
            foregroundHeight < 12)
        {
            return null;
        }

        const int padding = 2;
        left = Math.Max(0, left - padding);
        top = Math.Max(0, top - padding);
        right = Math.Min(source.Width - 1, right + padding);
        bottom = Math.Min(source.Height - 1, bottom + padding);

        var width = right - left + 1;
        var height = bottom - top + 1;
        var removeBackground = FindConnectedBackgroundPixels(source, background, left, top, width, height);
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (removeBackground[x, y])
                {
                    result.SetPixel(x, y, Color.Transparent);
                    continue;
                }

                result.SetPixel(x, y, source.GetPixel(left + x, top + y));
            }
        }

        return result;
    }

    private static bool[,] FindConnectedBackgroundPixels(
        Bitmap source,
        Color background,
        int left,
        int top,
        int width,
        int height)
    {
        var remove = new bool[width, height];
        var queue = new Queue<Point>();

        void TryEnqueue(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height || remove[x, y])
            {
                return;
            }

            var pixel = source.GetPixel(left + x, top + y);
            if (pixel.A > 12 && ColorDistance(pixel, background) > 26)
            {
                return;
            }

            remove[x, y] = true;
            queue.Enqueue(new Point(x, y));
        }

        for (var x = 0; x < width; x++)
        {
            TryEnqueue(x, 0);
            TryEnqueue(x, height - 1);
        }

        for (var y = 1; y < height - 1; y++)
        {
            TryEnqueue(0, y);
            TryEnqueue(width - 1, y);
        }

        while (queue.Count > 0)
        {
            var point = queue.Dequeue();
            TryEnqueue(point.X - 1, point.Y);
            TryEnqueue(point.X + 1, point.Y);
            TryEnqueue(point.X, point.Y - 1);
            TryEnqueue(point.X, point.Y + 1);
        }

        return remove;
    }

    private static Bitmap PadToSquare(Bitmap source)
    {
        if (source.Width == source.Height)
        {
            return new Bitmap(source);
        }

        var size = Math.Max(source.Width, source.Height);
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.DrawImageUnscaled(
            source,
            (size - source.Width) / 2,
            (size - source.Height) / 2);
        return bitmap;
    }

    private static Color AverageCornerColor(Bitmap bitmap)
    {
        var colors = new[]
        {
            bitmap.GetPixel(0, 0),
            bitmap.GetPixel(bitmap.Width - 1, 0),
            bitmap.GetPixel(0, bitmap.Height - 1),
            bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1)
        };

        return Color.FromArgb(
            colors.Sum(color => color.A) / colors.Length,
            colors.Sum(color => color.R) / colors.Length,
            colors.Sum(color => color.G) / colors.Length,
            colors.Sum(color => color.B) / colors.Length);
    }

    private static int ColorDistance(Color left, Color right) =>
        Math.Abs(left.R - right.R) +
        Math.Abs(left.G - right.G) +
        Math.Abs(left.B - right.B);

    private static string ComputeFingerprint(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }

    private static string ToJson(
        IReadOnlyList<ExplorerTaskbarRoot> roots,
        IReadOnlyList<ExplorerTaskbarButtonSnapshot> buttons,
        string error)
    {
        var process = Process.GetCurrentProcess();
        var builder = new StringBuilder(4096);
        builder.Append('{');
        Append(builder, "protocolVersion", "1", quoteValue: false);
        Append(builder, "messageType", "ExplorerTaskbarSnapshot");
        Append(builder, "sourceProcessId", process.Id.ToString(), quoteValue: false);
        Append(builder, "sourceProcessName", SafeProcessName(process));
        Append(builder, "sourceProcessPath", SafeProcessPath(process));
        Append(builder, "capturedAtUtc", DateTime.UtcNow.ToString("O"));
        Append(builder, "error", error);
        AppendRoots(builder, roots);
        AppendButtons(builder, buttons);
        builder.Length--;
        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendRoots(StringBuilder builder, IReadOnlyList<ExplorerTaskbarRoot> roots)
    {
        builder.Append("\"roots\":[");
        foreach (var root in roots)
        {
            builder.Append('{');
            Append(builder, "hwnd", root.Hwnd.ToInt64().ToString(), quoteValue: false);
            Append(builder, "className", root.ClassName);
            Append(builder, "left", root.Left.ToString(), quoteValue: false);
            Append(builder, "top", root.Top.ToString(), quoteValue: false);
            Append(builder, "right", root.Right.ToString(), quoteValue: false);
            Append(builder, "bottom", root.Bottom.ToString(), quoteValue: false);
            builder.Length--;
            builder.Append("},");
        }

        if (roots.Count > 0)
        {
            builder.Length--;
        }

        builder.Append("],");
    }

    private static void AppendButtons(StringBuilder builder, IReadOnlyList<ExplorerTaskbarButtonSnapshot> buttons)
    {
        builder.Append("\"buttons\":[");
        foreach (var button in buttons)
        {
            builder.Append('{');
            Append(builder, "runtimeId", button.RuntimeId);
            Append(builder, "name", button.Name);
            Append(builder, "className", button.ClassName);
            Append(builder, "automationId", button.AutomationId);
            Append(builder, "controlType", button.ControlType);
            Append(builder, "nativeWindowHandle", button.NativeWindowHandle.ToString(), quoteValue: false);
            Append(builder, "rootHwnd", button.RootHwnd.ToInt64().ToString(), quoteValue: false);
            Append(builder, "rootClassName", button.RootClassName);
            Append(builder, "left", button.Left.ToString(), quoteValue: false);
            Append(builder, "top", button.Top.ToString(), quoteValue: false);
            Append(builder, "right", button.Right.ToString(), quoteValue: false);
            Append(builder, "bottom", button.Bottom.ToString(), quoteValue: false);
            Append(builder, "buttonIconPngBase64", button.ButtonIconPngBase64);
            Append(builder, "buttonIconFingerprint", button.ButtonIconFingerprint);
            builder.Length--;
            builder.Append("},");
        }

        if (buttons.Count > 0)
        {
            builder.Length--;
        }

        builder.Append("],");
    }

    private static void Append(StringBuilder builder, string name, string value, bool quoteValue = true)
    {
        builder.Append('"');
        builder.Append(name);
        builder.Append("\":");
        if (quoteValue)
        {
            builder.Append('"');
            builder.Append(Escape(value));
            builder.Append('"');
        }
        else
        {
            builder.Append(value);
        }

        builder.Append(',');
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(character < ' '
                        ? "\\u" + ((int)character).ToString("x4")
                        : character);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string SafeProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private static string SafeProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var builder = new StringBuilder(256);
        GetClassName(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref NativePoint lpPoint);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private sealed class UiaElementInfo
    {
        public string Name { get; set; } = "";

        public string ClassName { get; set; } = "";

        public string AutomationId { get; set; } = "";

        public string ControlType { get; set; } = "";

        public int NativeWindowHandle { get; set; }

        public UiaRect BoundingRectangle { get; set; }
    }

    private sealed class ExplorerCommandTarget
    {
        public ExplorerCommandTarget(ExplorerTaskbarRoot root, object element, UiaElementInfo current)
        {
            Root = root;
            Element = element;
            Current = current;
        }

        public ExplorerTaskbarRoot Root { get; }

        public object Element { get; }

        public UiaElementInfo Current { get; }
    }

    private sealed class ExplorerTaskbarMenuCommand
    {
        public string Operation { get; private set; } = "";

        public string RuntimeId { get; private set; } = "";

        public string Name { get; private set; } = "";

        public long RootHwnd { get; private set; }

        public int NativeWindowHandle { get; private set; }

        public int Left { get; private set; }

        public int Top { get; private set; }

        public int Right { get; private set; }

        public int Bottom { get; private set; }

        public int AnchorX { get; private set; }

        public int AnchorY { get; private set; }

        public static ExplorerTaskbarMenuCommand? TryParse(string json)
        {
            try
            {
                return new ExplorerTaskbarMenuCommand
                {
                    Operation = ReadString(json, "Operation"),
                    RuntimeId = ReadString(json, "RuntimeId"),
                    Name = ReadString(json, "Name"),
                    RootHwnd = ReadLong(json, "RootHwnd"),
                    NativeWindowHandle = ReadInt(json, "NativeWindowHandle"),
                    Left = ReadInt(json, "Left"),
                    Top = ReadInt(json, "Top"),
                    Right = ReadInt(json, "Right"),
                    Bottom = ReadInt(json, "Bottom"),
                    AnchorX = ReadInt(json, "AnchorX"),
                    AnchorY = ReadInt(json, "AnchorY")
                };
            }
            catch
            {
                return null;
            }
        }

        private static string ReadString(string json, string name)
        {
            var valueStart = FindValueStart(json, name);
            if (valueStart < 0 || valueStart >= json.Length || json[valueStart] != '"')
            {
                return "";
            }

            var builder = new StringBuilder();
            for (var index = valueStart + 1; index < json.Length; index++)
            {
                var character = json[index];
                if (character == '"')
                {
                    return builder.ToString();
                }

                if (character != '\\' || index + 1 >= json.Length)
                {
                    builder.Append(character);
                    continue;
                }

                var escaped = json[++index];
                switch (escaped)
                {
                    case '"':
                    case '\\':
                    case '/':
                        builder.Append(escaped);
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u' when index + 4 < json.Length:
                        var hex = json.Substring(index + 1, 4);
                        if (ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code))
                        {
                            builder.Append((char)code);
                        }

                        index += 4;
                        break;
                }
            }

            return "";
        }

        private static int ReadInt(string json, string name) =>
            unchecked((int)ReadLong(json, name));

        private static long ReadLong(string json, string name)
        {
            var valueStart = FindValueStart(json, name);
            if (valueStart < 0)
            {
                return 0;
            }

            var valueEnd = valueStart;
            while (valueEnd < json.Length && (json[valueEnd] == '-' || char.IsDigit(json[valueEnd])))
            {
                valueEnd++;
            }

            return valueEnd > valueStart && long.TryParse(json.Substring(valueStart, valueEnd - valueStart), out var value)
                ? value
                : 0;
        }

        private static int FindValueStart(string json, string name)
        {
            var pattern = "\"" + name + "\"";
            var property = json.IndexOf(pattern, StringComparison.Ordinal);
            if (property < 0)
            {
                return -1;
            }

            var colon = json.IndexOf(':', property + pattern.Length);
            if (colon < 0)
            {
                return -1;
            }

            var index = colon + 1;
            while (index < json.Length && char.IsWhiteSpace(json[index]))
            {
                index++;
            }

            return index;
        }
    }

    private readonly struct UiaRect
    {
        public UiaRect(bool isEmpty, double width, double height, double left, double top, double right, double bottom)
        {
            IsEmpty = isEmpty;
            Width = width;
            Height = height;
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public bool IsEmpty { get; }

        public double Width { get; }

        public double Height { get; }

        public double Left { get; }

        public double Top { get; }

        public double Right { get; }

        public double Bottom { get; }
    }

    private readonly struct ButtonImageCapture
    {
        public static ButtonImageCapture Empty { get; } = new("", "");

        public ButtonImageCapture(string pngBase64, string fingerprint)
        {
            PngBase64 = pngBase64;
            Fingerprint = fingerprint;
        }

        public string PngBase64 { get; }

        public string Fingerprint { get; }
    }

    private static class UiaAdapter
    {
        private static readonly object Sync = new();
        private static bool _loadAttempted;
        private static string _loadError = "";
        private static Type? _automationElementType;
        private static MethodInfo? _fromHandle;
        private static MethodInfo? _getRuntimeId;
        private static MethodInfo? _getCurrentPattern;
        private static MethodInfo? _invoke;
        private static MethodInfo? _getFirstChild;
        private static MethodInfo? _getNextSibling;
        private static PropertyInfo? _current;
        private static PropertyInfo? _name;
        private static PropertyInfo? _className;
        private static PropertyInfo? _automationId;
        private static PropertyInfo? _controlType;
        private static PropertyInfo? _controlTypeProgrammaticName;
        private static PropertyInfo? _nativeWindowHandle;
        private static PropertyInfo? _boundingRectangle;
        private static PropertyInfo? _rectIsEmpty;
        private static PropertyInfo? _rectWidth;
        private static PropertyInfo? _rectHeight;
        private static PropertyInfo? _rectLeft;
        private static PropertyInfo? _rectTop;
        private static PropertyInfo? _rectRight;
        private static PropertyInfo? _rectBottom;
        private static object? _rawViewWalker;
        private static object? _invokePattern;

        public static bool TryEnsureLoaded(out string error)
        {
            lock (Sync)
            {
                if (_loadAttempted)
                {
                    error = _loadError;
                    return _loadError.Length == 0;
                }

                _loadAttempted = true;
                try
                {
                    var clientAssembly = LoadFrameworkAssembly("UIAutomationClient");
                    LoadFrameworkAssembly("UIAutomationTypes");
                    LoadFrameworkAssembly("WindowsBase");

                    _automationElementType = clientAssembly.GetType("System.Windows.Automation.AutomationElement", throwOnError: true);
                    var invokePatternType = clientAssembly.GetType("System.Windows.Automation.InvokePattern", throwOnError: true);
                    var treeWalkerType = clientAssembly.GetType("System.Windows.Automation.TreeWalker", throwOnError: true);
                    _fromHandle = _automationElementType.GetMethod(
                        "FromHandle",
                        BindingFlags.Public | BindingFlags.Static,
                        binder: null,
                        types: [typeof(IntPtr)],
                        modifiers: null);
                    _getRuntimeId = _automationElementType.GetMethod("GetRuntimeId", BindingFlags.Public | BindingFlags.Instance);
                    _invokePattern =
                        invokePatternType.GetProperty("Pattern", BindingFlags.Public | BindingFlags.Static)
                            ?.GetValue(null, null) ??
                        invokePatternType.GetField("Pattern", BindingFlags.Public | BindingFlags.Static)
                            ?.GetValue(null);
                    var automationPatternType = _invokePattern?.GetType();
                    if (automationPatternType is not null)
                    {
                        _getCurrentPattern = _automationElementType.GetMethod(
                            "GetCurrentPattern",
                            BindingFlags.Public | BindingFlags.Instance,
                            binder: null,
                            types: [automationPatternType],
                            modifiers: null);
                    }

                    _invoke = invokePatternType.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance);
                    _current = _automationElementType.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
                    var currentType = _current?.PropertyType ?? throw new MissingMemberException("AutomationElement.Current");
                    _name = currentType.GetProperty("Name");
                    _className = currentType.GetProperty("ClassName");
                    _automationId = currentType.GetProperty("AutomationId");
                    _controlType = currentType.GetProperty("ControlType");
                    _nativeWindowHandle = currentType.GetProperty("NativeWindowHandle");
                    _boundingRectangle = currentType.GetProperty("BoundingRectangle");

                    var controlTypeType = _controlType?.PropertyType;
                    _controlTypeProgrammaticName = controlTypeType?.GetProperty("ProgrammaticName");

                    var rectType = _boundingRectangle?.PropertyType ?? throw new MissingMemberException("AutomationElement.Current.BoundingRectangle");
                    _rectIsEmpty = rectType.GetProperty("IsEmpty");
                    _rectWidth = rectType.GetProperty("Width");
                    _rectHeight = rectType.GetProperty("Height");
                    _rectLeft = rectType.GetProperty("Left");
                    _rectTop = rectType.GetProperty("Top");
                    _rectRight = rectType.GetProperty("Right");
                    _rectBottom = rectType.GetProperty("Bottom");

                    _rawViewWalker =
                        treeWalkerType.GetProperty("RawViewWalker", BindingFlags.Public | BindingFlags.Static)
                            ?.GetValue(null, null) ??
                        treeWalkerType.GetField("RawViewWalker", BindingFlags.Public | BindingFlags.Static)
                            ?.GetValue(null);
                    _getFirstChild = treeWalkerType.GetMethod(
                        "GetFirstChild",
                        BindingFlags.Public | BindingFlags.Instance,
                        binder: null,
                        types: [_automationElementType],
                        modifiers: null);
                    _getNextSibling = treeWalkerType.GetMethod(
                        "GetNextSibling",
                        BindingFlags.Public | BindingFlags.Instance,
                        binder: null,
                        types: [_automationElementType],
                        modifiers: null);

                    if (_fromHandle is null ||
                        _getRuntimeId is null ||
                        _getFirstChild is null ||
                        _getNextSibling is null ||
                        _current is null ||
                        _rawViewWalker is null)
                    {
                        throw new MissingMemberException("Required UIAutomation members were not found.");
                    }
                }
                catch (Exception exception)
                {
                    _loadError = $"UIAutomation load failed: {exception.GetType().Name}: {exception.Message}";
                }

                error = _loadError;
                return _loadError.Length == 0;
            }
        }

        private static Assembly LoadFrameworkAssembly(string shortName)
        {
            try
            {
                return Assembly.Load($"{shortName}, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
            }
            catch
            {
            }

            foreach (var path in GetFrameworkAssemblyPaths(shortName))
            {
                try
                {
                    if (File.Exists(path))
                    {
                        return Assembly.LoadFrom(path);
                    }
                }
                catch
                {
                }
            }

            return Assembly.Load(shortName);
        }

        private static IEnumerable<string> GetFrameworkAssemblyPaths(string shortName)
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var frameworkFolder = Environment.Is64BitProcess ? "Framework64" : "Framework";
            yield return Path.Combine(windows, "Microsoft.NET", frameworkFolder, "v4.0.30319", "WPF", shortName + ".dll");
            yield return Path.Combine(windows, "Microsoft.NET", "assembly", "GAC_MSIL", shortName, $"v4.0_4.0.0.0__31bf3856ad364e35", shortName + ".dll");
            yield return Path.Combine(windows, "assembly", "GAC_MSIL", shortName, "3.0.0.0__31bf3856ad364e35", shortName + ".dll");
        }

        public static object? FromHandle(IntPtr hwnd)
        {
            if (!TryEnsureLoaded(out _))
            {
                return null;
            }

            return _fromHandle?.Invoke(null, [hwnd]);
        }

        public static object? GetFirstChild(object element) =>
            _getFirstChild?.Invoke(_rawViewWalker, [element]);

        public static object? GetNextSibling(object element) =>
            _getNextSibling?.Invoke(_rawViewWalker, [element]);

        public static int[]? GetRuntimeId(object element) =>
            _getRuntimeId?.Invoke(element, null) as int[];

        public static bool TryInvoke(object element, out string detail)
        {
            if (!TryEnsureLoaded(out var error))
            {
                detail = error;
                return false;
            }

            if (_getCurrentPattern is null || _invokePattern is null || _invoke is null)
            {
                detail = "InvokePattern reflection members are unavailable.";
                return false;
            }

            try
            {
                var pattern = _getCurrentPattern.Invoke(element, [_invokePattern]);
                if (pattern is null)
                {
                    detail = "InvokePattern is not supported by the matched taskbar button.";
                    return false;
                }

                _invoke.Invoke(pattern, null);
                detail = "InvokePattern.Invoke succeeded.";
                return true;
            }
            catch (TargetInvocationException exception)
            {
                detail = $"InvokePattern failed: {exception.InnerException?.GetType().Name ?? exception.GetType().Name}: {exception.InnerException?.Message ?? exception.Message}";
                return false;
            }
            catch (Exception exception)
            {
                detail = $"InvokePattern failed: {exception.GetType().Name}: {exception.Message}";
                return false;
            }
        }

        public static UiaElementInfo GetCurrent(object element)
        {
            var current = _current?.GetValue(element, null)
                ?? throw new InvalidOperationException("AutomationElement.Current returned null.");
            var rect = _boundingRectangle?.GetValue(current, null);
            return new UiaElementInfo
            {
                Name = ReadString(_name, current),
                ClassName = ReadString(_className, current),
                AutomationId = ReadString(_automationId, current),
                ControlType = ReadControlType(current),
                NativeWindowHandle = ReadInt(_nativeWindowHandle, current),
                BoundingRectangle = new UiaRect(
                    ReadBool(_rectIsEmpty, rect),
                    ReadDouble(_rectWidth, rect),
                    ReadDouble(_rectHeight, rect),
                    ReadDouble(_rectLeft, rect),
                    ReadDouble(_rectTop, rect),
                    ReadDouble(_rectRight, rect),
                    ReadDouble(_rectBottom, rect))
            };
        }

        private static string ReadControlType(object current)
        {
            var controlType = _controlType?.GetValue(current, null);
            return ReadString(_controlTypeProgrammaticName, controlType);
        }

        private static string ReadString(PropertyInfo? property, object? target)
        {
            if (property is null || target is null)
            {
                return "";
            }

            try
            {
                return property.GetValue(target, null) as string ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static int ReadInt(PropertyInfo? property, object? target)
        {
            if (property is null || target is null)
            {
                return 0;
            }

            try
            {
                return Convert.ToInt32(property.GetValue(target, null));
            }
            catch
            {
                return 0;
            }
        }

        private static bool ReadBool(PropertyInfo? property, object? target)
        {
            if (property is null || target is null)
            {
                return false;
            }

            try
            {
                return Convert.ToBoolean(property.GetValue(target, null));
            }
            catch
            {
                return false;
            }
        }

        private static double ReadDouble(PropertyInfo? property, object? target)
        {
            if (property is null || target is null)
            {
                return 0;
            }

            try
            {
                return Convert.ToDouble(property.GetValue(target, null));
            }
            catch
            {
                return 0;
            }
        }
    }

    private sealed class ExplorerTaskbarRoot
    {
        public ExplorerTaskbarRoot(IntPtr hwnd, string className, int left, int top, int right, int bottom)
        {
            Hwnd = hwnd;
            ClassName = className;
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public IntPtr Hwnd { get; }

        public string ClassName { get; }

        public int Left { get; }

        public int Top { get; }

        public int Right { get; }

        public int Bottom { get; }
    }

    private sealed class ExplorerTaskbarButtonSnapshot
    {
        public ExplorerTaskbarButtonSnapshot(
            string runtimeId,
            string name,
            string className,
            string automationId,
            string controlType,
            int nativeWindowHandle,
            IntPtr rootHwnd,
            string rootClassName,
            int left,
            int top,
            int right,
            int bottom,
            string buttonIconPngBase64,
            string buttonIconFingerprint)
        {
            RuntimeId = runtimeId;
            Name = name;
            ClassName = className;
            AutomationId = automationId;
            ControlType = controlType;
            NativeWindowHandle = nativeWindowHandle;
            RootHwnd = rootHwnd;
            RootClassName = rootClassName;
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
            ButtonIconPngBase64 = buttonIconPngBase64;
            ButtonIconFingerprint = buttonIconFingerprint;
        }

        public string RuntimeId { get; }

        public string Name { get; }

        public string ClassName { get; }

        public string AutomationId { get; }

        public string ControlType { get; }

        public int NativeWindowHandle { get; }

        public IntPtr RootHwnd { get; }

        public string RootClassName { get; }

        public int Left { get; }

        public int Top { get; }

        public int Right { get; }

        public int Bottom { get; }

        public string ButtonIconPngBase64 { get; }

        public string ButtonIconFingerprint { get; }
    }
}
