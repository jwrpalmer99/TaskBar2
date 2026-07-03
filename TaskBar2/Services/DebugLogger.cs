using System.Diagnostics;
using System.IO;

namespace TaskBar2.Services;

internal static class DebugLogger
{
    private const long MaxLogBytes = 1024 * 1024;
    private static readonly object Lock = new();
    private static readonly Dictionary<string, string> LastMessages = [];

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskBar2");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "taskbar2.log");

    public static bool IsEnabled
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    [Conditional("DEBUG")]
    public static void Write(string message)
    {
#if !DEBUG
        return;
#else
        lock (Lock)
        {
            Directory.CreateDirectory(LogDirectory);
            RotateIfNeeded();
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void WriteIfChanged(string key, string message)
    {
#if !DEBUG
        return;
#else
        lock (Lock)
        {
            if (LastMessages.TryGetValue(key, out var lastMessage) && lastMessage == message)
            {
                return;
            }

            LastMessages[key] = message;
        }

        Write(message);
#endif
    }

    public static void OpenLog()
    {
        if (!IsEnabled)
        {
            return;
        }

        Directory.CreateDirectory(LogDirectory);
        if (!File.Exists(LogPath))
        {
            File.WriteAllText(LogPath, "");
        }

        Process.Start(new ProcessStartInfo(LogPath)
        {
            UseShellExecute = true
        });
    }

    private static void RotateIfNeeded()
    {
        var file = new FileInfo(LogPath);
        if (!file.Exists || file.Length <= MaxLogBytes)
        {
            return;
        }

        var backupPath = Path.Combine(LogDirectory, "taskbar2.previous.log");
        File.Copy(LogPath, backupPath, overwrite: true);
        File.WriteAllText(LogPath, "");
    }
}
