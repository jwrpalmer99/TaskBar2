using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using TaskBar2.Models;

namespace TaskBar2.Services;

internal static partial class PinnedTaskbarShortcutProvider
{
    private const int MaxLinkStringLength = 4096;
    private static readonly object Sync = new();
    private static PinnedShortcutCache? Cache;

    public static IReadOnlyList<TaskbarItem> GetPinnedItems()
    {
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
                Cache = new PinnedShortcutCache(signature, items);
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
        object? shellLinkObject = null;
        try
        {
            shellLinkObject = new ShellLink();
            var shellLink = (IShellLinkW)shellLinkObject;
            ((IPersistFile)shellLink).Load(shortcutFile.FullName, 0);

            var title = Path.GetFileNameWithoutExtension(shortcutFile.Name).Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var targetPath = NormalizePath(ReadString(builder => shellLink.GetPath(builder, builder.Capacity, IntPtr.Zero, 0)));
            var arguments = ReadString(builder => shellLink.GetArguments(builder, builder.Capacity));
            var workingDirectory = NormalizePath(ReadString(builder => shellLink.GetWorkingDirectory(builder, builder.Capacity)));
            var iconLocation = ReadIconLocation(shellLink);
            var iconPath = GetIconPath(iconLocation);
            var processPath = ResolveProcessPath(targetPath, iconPath, title);
            var processName = GetProcessName(processPath, arguments, title);
            var groupKey = GetPinnedGroupKey(processPath, processName, shortcutFile.FullName);
            var icon = WindowIconProvider.GetIcon(IntPtr.Zero, processPath, iconPath);

            return new TaskbarItem(
                IntPtr.Zero,
                title,
                icon,
                GetFingerprint(shortcutFile, processPath, iconPath, arguments),
                IsActive: false,
                IsMinimized: false,
                MonitorDeviceName: "",
                ProcessId: 0,
                processName,
                processPath,
                AppUserModelId: "",
                groupKey,
                iconPath,
                shortcutFile.FullName,
                arguments,
                workingDirectory);
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or IOException or ArgumentException or ExternalException)
        {
            DebugLogger.WriteIfChanged(
                $"pinned-taskbar-shortcut-error-{shortcutFile.Name}",
                $"Pinned taskbar shortcut failed to load: Shortcut={shortcutFile.FullName} {exception.GetType().Name}: {exception.Message}");
            return null;
        }
        finally
        {
            if (shellLinkObject is not null)
            {
                Marshal.FinalReleaseComObject(shellLinkObject);
            }
        }
    }

    private static string ReadString(Func<StringBuilder, int> read)
    {
        var builder = new StringBuilder(MaxLinkStringLength);
        return read(builder) == 0 ? builder.ToString().Trim() : "";
    }

    private static IconLocation ReadIconLocation(IShellLinkW shellLink)
    {
        var builder = new StringBuilder(MaxLinkStringLength);
        return shellLink.GetIconLocation(builder, builder.Capacity, out var iconIndex) == 0
            ? new IconLocation(builder.ToString().Trim(), iconIndex)
            : default;
    }

    private static string GetIconPath(IconLocation iconLocation)
    {
        var value = iconLocation.Path;
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return NormalizePath(value);
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

    private static string GetFingerprint(FileInfo shortcutFile, string processPath, string iconPath, string arguments)
    {
        var iconPart = "";
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            try
            {
                var iconFile = new FileInfo(iconPath);
                iconPart = $"{iconFile.FullName}:{iconFile.Length}:{iconFile.LastWriteTimeUtc.Ticks}";
            }
            catch
            {
                iconPart = iconPath;
            }
        }

        return $"pin:{shortcutFile.FullName}:{shortcutFile.LastWriteTimeUtc.Ticks}:{processPath}:{iconPart}:{arguments}";
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

    private readonly record struct IconLocation(string Path, int Index);

    private sealed record PinnedShortcutCache(string Signature, IReadOnlyList<TaskbarItem> Items);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        [PreserveSig]
        int GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        [PreserveSig]
        int Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

        [PreserveSig]
        int Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, bool remember);

        [PreserveSig]
        int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        [PreserveSig]
        int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);

        [PreserveSig]
        int GetIDList(out IntPtr pidl);

        [PreserveSig]
        int SetIDList(IntPtr pidl);

        [PreserveSig]
        int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);

        [PreserveSig]
        int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        [PreserveSig]
        int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);

        [PreserveSig]
        int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        [PreserveSig]
        int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);

        [PreserveSig]
        int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        [PreserveSig]
        int GetHotkey(out short hotkey);

        [PreserveSig]
        int SetHotkey(short hotkey);

        [PreserveSig]
        int GetShowCmd(out int showCommand);

        [PreserveSig]
        int SetShowCmd(int showCommand);

        [PreserveSig]
        int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int iconPathCount, out int iconIndex);

        [PreserveSig]
        int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        [PreserveSig]
        int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);

        [PreserveSig]
        int Resolve(IntPtr hwnd, uint flags);

        [PreserveSig]
        int SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
