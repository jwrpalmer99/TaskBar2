using System.IO;
using System.Text.RegularExpressions;
using TaskBar2.Models;

namespace TaskBar2.Services;

internal static partial class PinnedTaskbarShortcutProvider
{
    private static readonly TimeSpan SignatureRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly object Sync = new();
    private static PinnedShortcutCache? Cache;

    public static IReadOnlyList<TaskbarItem> GetPinnedItems()
    {
        var now = DateTimeOffset.UtcNow;
        lock (Sync)
        {
            if (Cache is not null && now - Cache.LastChecked < SignatureRefreshInterval)
            {
                return Cache.Items;
            }
        }

        var folder = GetPinnedTaskbarFolder();
        if (!Directory.Exists(folder))
        {
            return Array.Empty<TaskbarItem>();
        }

        try
        {
            var files = Directory
                .EnumerateFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .OrderBy(file => file.CreationTimeUtc)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var signature = string.Join("|", files.Select(file => $"{file.FullName}:{file.LastWriteTimeUtc.Ticks}:{file.Length}"));

            lock (Sync)
            {
                if (Cache is not null && string.Equals(Cache.Signature, signature, StringComparison.Ordinal))
                {
                    Cache = Cache with { LastChecked = now };
                    return Cache.Items;
                }
            }

            var items = files
                .Select(TryCreatePinnedItem)
                .Where(item => item is not null)
                .Cast<TaskbarItem>()
                .ToArray();

            lock (Sync)
            {
                Cache = new PinnedShortcutCache(signature, items, now);
            }

            DebugLogger.WriteIfChanged(
                "pinned-taskbar-shortcuts",
                "Pinned taskbar shortcuts loaded: " +
                $"Count={items.Length} Items={string.Join(" | ", items.Select(item => $"{item.Title}:{item.ProcessName}:{item.ProcessPath}:{item.LaunchPath}"))}");
            return items;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DebugLogger.WriteIfChanged(
                "pinned-taskbar-shortcuts-error",
                $"Pinned taskbar shortcut scan failed: {exception.GetType().Name}: {exception.Message}");
            return Array.Empty<TaskbarItem>();
        }
    }

    private static TaskbarItem? TryCreatePinnedItem(FileInfo shortcutFile)
    {
        try
        {
            if (!ShellLinkReader.TryLoadFile(shortcutFile.FullName, out var link))
            {
                return null;
            }

            var title = Path.GetFileNameWithoutExtension(shortcutFile.Name).Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var targetPath = link.TargetPath;
            var arguments = link.Arguments;
            var workingDirectory = link.WorkingDirectory;
            var iconPath = link.IconPath;
            var processPath = ResolveProcessPath(targetPath, iconPath, title);
            var processName = GetProcessName(processPath, arguments, title);
            var groupKey = GetPinnedGroupKey(processPath, processName, shortcutFile.FullName);
            var icon = WindowIconProvider.GetIcon(IntPtr.Zero, processPath, iconPath, link.IconIndex);

            return new TaskbarItem(
                IntPtr.Zero,
                title,
                icon,
                GetFingerprint(shortcutFile, processPath, iconPath, link.IconIndex, arguments),
                IsActive: false,
                IsMinimized: false,
                MonitorDeviceName: "",
                ProcessId: 0,
                processName,
                processPath,
                AppUserModelId: "",
                groupKey,
                iconPath,
                link.IconIndex,
                shortcutFile.FullName,
                arguments,
                workingDirectory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or ArgumentException)
        {
            DebugLogger.WriteIfChanged(
                $"pinned-taskbar-shortcut-error-{shortcutFile.Name}",
                $"Pinned taskbar shortcut failed to load: Shortcut={shortcutFile.FullName} {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    private static string ResolveProcessPath(string targetPath, string iconPath, string title)
    {
        if (!string.IsNullOrWhiteSpace(targetPath) && File.Exists(targetPath))
        {
            return targetPath;
        }

        if (!string.IsNullOrWhiteSpace(iconPath) &&
            File.Exists(iconPath) &&
            string.Equals(Path.GetExtension(iconPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return iconPath;
        }

        return Normalize(title) switch
        {
            "file explorer" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            "microsoft edge" => FirstExistingPath(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")),
            "google chrome" => FirstExistingPath(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe")),
            _ => ""
        };
    }

    private static string GetProcessName(string processPath, string arguments, string title)
    {
        var processStart = ProcessStartRegex().Match(arguments);
        if (processStart.Success)
        {
            return Path.GetFileNameWithoutExtension(processStart.Groups["name"].Value);
        }

        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return Path.GetFileNameWithoutExtension(processPath);
        }

        return title;
    }

    private static string GetPinnedGroupKey(string processPath, string processName, string shortcutPath)
    {
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return "path:" + processPath.ToUpperInvariant();
        }

        if (!string.IsNullOrWhiteSpace(processName))
        {
            return "process:" + processName.ToUpperInvariant();
        }

        return "pin:" + shortcutPath.ToUpperInvariant();
    }

    private static string GetFingerprint(FileInfo shortcutFile, string processPath, string iconPath, int iconIndex, string arguments)
    {
        var iconPart = "";
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            try
            {
                var iconFile = new FileInfo(iconPath);
                iconPart = $"{iconFile.FullName}:{iconFile.Length}:{iconFile.LastWriteTimeUtc.Ticks}:{iconIndex}";
            }
            catch
            {
                iconPart = $"{iconPath}:{iconIndex}";
            }
        }

        return $"pin:{shortcutFile.FullName}:{shortcutFile.LastWriteTimeUtc.Ticks}:{processPath}:{iconPart}:index:{iconIndex}:{arguments}";
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))).TrimEnd('\\');
        }
        catch
        {
            return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        }
    }

    private static string Normalize(string value) =>
        string.Join(
            " ",
            value.Trim()
                .ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string FirstExistingPath(params string[] paths) =>
        paths.FirstOrDefault(File.Exists) ?? paths.FirstOrDefault() ?? "";

    private static string GetPinnedTaskbarFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Internet Explorer",
            "Quick Launch",
            "User Pinned",
            "TaskBar");

    [GeneratedRegex("--processStart\\s+\"?(?<name>[^\"\\s]+)\"?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProcessStartRegex();

    private sealed record PinnedShortcutCache(string Signature, IReadOnlyList<TaskbarItem> Items, DateTimeOffset LastChecked);
}
