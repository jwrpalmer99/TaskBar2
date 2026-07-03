using System.Diagnostics;
using System.IO;

namespace TaskBar2.Services;

internal static class DebugLogger
{
    private const long MaxLogBytes = 1024 * 1024;
    private const int MaxRememberedMessages = 512;
    private const int MaxLogMessageLength = 4096;
    private static readonly object Lock = new();
    private static readonly Dictionary<string, ulong> LastMessageHashes = [];

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
        message = TrimMessage(message);
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
        var messageHash = HashMessage(message);
        lock (Lock)
        {
            if (LastMessageHashes.TryGetValue(key, out var lastMessageHash) && lastMessageHash == messageHash)
            {
                return;
            }

            if (!LastMessageHashes.ContainsKey(key) && LastMessageHashes.Count >= MaxRememberedMessages)
            {
                LastMessageHashes.Clear();
            }

            LastMessageHashes[key] = messageHash;
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

    private static string TrimMessage(string message)
    {
        if (message.Length <= MaxLogMessageLength)
        {
            return message;
        }

        return message[..MaxLogMessageLength] + $"... [truncated {message.Length - MaxLogMessageLength} chars]";
    }

    private static ulong HashMessage(string message)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var character in message)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash;
    }
}
