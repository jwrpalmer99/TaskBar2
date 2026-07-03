using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using OpenMcdf;
using TaskBar2.Models;
using TaskBar2.Native;

namespace TaskBar2.Services;

internal static class ShellJumpListFileProvider
{
    private const int MaxEntriesPerSection = 8;
    private const int MaxScannedLinkBytes = 4 * 1024 * 1024;
    private static readonly string AutomaticDestinationsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft",
        "Windows",
        "Recent",
        "AutomaticDestinations");
    private static readonly string CustomDestinationsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft",
        "Windows",
        "Recent",
        "CustomDestinations");
    private static readonly NativeMethods.PropertyKey PkeyTitle = new()
    {
        FormatId = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"),
        PropertyId = 2
    };
    private static readonly byte[] ShellLinkGuidMarker =
    [
        0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46
    ];
    private static readonly byte[] CategoryFooterSignature = [0xAB, 0xFB, 0xBF, 0xBA];

    public static IReadOnlyList<JumpListMenuSection> GetCustomDestinationSections(IReadOnlyList<TaskbarItem> items)
    {
        foreach (var executablePath in GetExecutableCandidates(items))
        {
            if (ShouldSkipCustomDestinations(executablePath))
            {
                continue;
            }

            var entries = ReadCustomDestinationEntries(executablePath)
                .ToArray();

            if (entries.Length == 0)
            {
                continue;
            }

            var sections = new List<JumpListMenuSection>();
            foreach (var group in entries.GroupBy(entry => entry.SectionTitle, StringComparer.OrdinalIgnoreCase))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var menuEntries = group
                    .Select(CreateMenuEntry)
                    .Where(entry => entry is not null && seen.Add(entry.Key))
                    .Cast<JumpListMenuEntry>()
                    .Take(MaxEntriesPerSection)
                    .ToArray();
                if (menuEntries.Length > 0)
                {
                    sections.Add(new JumpListMenuSection(group.Key, menuEntries));
                }
            }

            if (sections.Count == 0)
            {
                continue;
            }

            DebugLogger.WriteIfChanged(
                $"jump-list-custom-loaded-{executablePath}",
                $"Jump List custom destinations loaded: Exe={executablePath} Sections={sections.Count} Count={entries.Length} Items={string.Join(" | ", entries.Select(entry => $"{entry.SectionTitle}:{entry.Title}"))}");
            return sections;
        }

        return Array.Empty<JumpListMenuSection>();
    }

    private static bool ShouldSkipCustomDestinations(string executablePath)
    {
        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        return executableName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<JumpListMenuSection> GetAutomaticDestinationSections(IReadOnlyList<TaskbarItem> items)
    {
        foreach (var executablePath in GetExecutableCandidates(items))
        {
            var entries = ReadAutomaticDestinationEntries(executablePath)
                .OrderByDescending(entry => entry.IsPinned)
                .ThenBy(entry => entry.Order)
                .ToArray();
            if (entries.Length == 0)
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sections = new List<JumpListMenuSection>();
            var pinned = entries
                .Where(entry => entry.IsPinned)
                .Select(CreateMenuEntry)
                .Where(entry => entry is not null && seen.Add(entry.Key))
                .Cast<JumpListMenuEntry>()
                .Take(MaxEntriesPerSection)
                .ToArray();
            if (pinned.Length > 0)
            {
                sections.Add(new JumpListMenuSection("Pinned", pinned));
            }

            var recent = entries
                .Where(entry => !entry.IsPinned)
                .Select(CreateMenuEntry)
                .Where(entry => entry is not null && seen.Add(entry.Key))
                .Cast<JumpListMenuEntry>()
                .Take(MaxEntriesPerSection)
                .ToArray();
            if (recent.Length > 0)
            {
                sections.Add(new JumpListMenuSection("Recent", recent));
            }

            if (sections.Count == 0)
            {
                continue;
            }

            DebugLogger.WriteIfChanged(
                $"jump-list-automatic-file-loaded-{executablePath}",
                $"Jump List automatic destinations loaded from files: Exe={executablePath} Sections={sections.Count} Count={entries.Length}");
            return sections;
        }

        return Array.Empty<JumpListMenuSection>();
    }

    private static IEnumerable<ShellDestinationEntry> ReadCustomDestinationEntries(string executablePath)
    {
        if (!Directory.Exists(CustomDestinationsDirectory))
        {
            yield break;
        }

        foreach (var filePath in EnumerateFiles(CustomDestinationsDirectory, "*.customDestinations-ms"))
        {
            ShellDestinationEntry[] entries;
            try
            {
                entries = ParseCustomDestinationsFile(filePath, executablePath).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException or ArgumentException)
            {
                DebugLogger.WriteIfChanged(
                    $"jump-list-custom-file-error-{Path.GetFileName(filePath)}",
                    $"Jump List custom destination file skipped: File={filePath} {exception.GetType().Name}: {exception.Message}");
                continue;
            }

            if (entries.Length == 0)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                yield return entry;
            }

            yield break;
        }
    }

    private static IEnumerable<ShellDestinationEntry> ParseCustomDestinationsFile(string filePath, string executablePath)
    {
        var data = ReadFileWithSharing(filePath);
        var position = 0;
        var categoryStart = 0;
        var currentSectionTitle = ExtractCustomCategoryTitle(data, categoryStart, data.Length);
        while (position < data.Length)
        {
            var linkStart = FindNextShellLinkStart(data, position);
            if (linkStart < 0)
            {
                yield break;
            }

            var searchFrom = linkStart + 4 + ShellLinkGuidMarker.Length;
            var nextLinkStart = FindNextShellLinkStart(data, searchFrom);
            var nextFooter = IndexOf(data, CategoryFooterSignature, searchFrom);
            var end = data.Length;
            if (nextLinkStart >= 0)
            {
                end = Math.Min(end, nextLinkStart);
            }

            if (nextFooter >= 0)
            {
                end = Math.Min(end, nextFooter);
            }

            if (end <= linkStart || end - linkStart > MaxScannedLinkBytes)
            {
                position = linkStart + 1;
                continue;
            }

            var linkBytes = data[linkStart..end];
            if (TryParseShellLinkBytes(linkBytes, out var link) &&
                PathsRoughlyMatch(link.TargetPath, executablePath))
            {
                yield return new ShellDestinationEntry(
                    currentSectionTitle,
                    GetEntryTitle(link),
                    link.TargetPath,
                    link.Arguments,
                    link.WorkingDirectory,
                    link.IconPath,
                    link.IconIndex,
                    IsPinned: false,
                    Order: int.MaxValue);
            }

            if (nextFooter >= 0 && end == nextFooter)
            {
                categoryStart = nextFooter + CategoryFooterSignature.Length;
                currentSectionTitle = ExtractCustomCategoryTitle(data, categoryStart, data.Length);
            }

            position = Math.Max(linkStart + 1, end);
        }
    }

    private static IEnumerable<ShellDestinationEntry> ReadAutomaticDestinationEntries(string executablePath)
    {
        if (!Directory.Exists(AutomaticDestinationsDirectory))
        {
            yield break;
        }

        foreach (var filePath in EnumerateFiles(AutomaticDestinationsDirectory, "*.automaticDestinations-ms"))
        {
            ShellDestinationEntry[] entries;
            try
            {
                entries = ParseAutomaticDestinationsFile(filePath, executablePath).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException or ArgumentException or OpenMcdf.FileFormatException)
            {
                DebugLogger.WriteIfChanged(
                    $"jump-list-automatic-file-error-{Path.GetFileName(filePath)}",
                    $"Jump List automatic destination file skipped: File={filePath} {exception.GetType().Name}: {exception.Message}");
                continue;
            }

            if (entries.Length == 0)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                yield return entry;
            }

            yield break;
        }
    }

    private static IEnumerable<ShellDestinationEntry> ParseAutomaticDestinationsFile(string filePath, string executablePath)
    {
        var streams = ReadCompoundStreams(filePath);
        if (!streams.TryGetValue("DestList", out var destListBytes))
        {
            yield break;
        }

        var destListEntries = ParseDestList(destListBytes);
        foreach (var stream in streams)
        {
            if (stream.Key.Equals("DestList", StringComparison.OrdinalIgnoreCase) ||
                !uint.TryParse(stream.Key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var entryNumber) ||
                !TryParseShellLinkBytes(stream.Value, out var link) ||
                !PathsRoughlyMatch(link.TargetPath, executablePath))
            {
                continue;
            }

            destListEntries.TryGetValue(entryNumber, out var destListEntry);
            yield return new ShellDestinationEntry(
                destListEntry?.IsPinned == true ? "Pinned" : "Recent",
                GetEntryTitle(link),
                link.TargetPath,
                link.Arguments,
                link.WorkingDirectory,
                link.IconPath,
                link.IconIndex,
                destListEntry?.IsPinned ?? false,
                destListEntry?.Order ?? int.MaxValue);
        }
    }

    private static Dictionary<string, byte[]> ReadCompoundStreams(string filePath)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var root = RootStorage.Open(fileStream, StorageModeFlags.LeaveOpen);
        foreach (var entry in root.EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream ||
                entry.Length <= 0 ||
                entry.Length > MaxScannedLinkBytes)
            {
                continue;
            }

            using var stream = root.OpenStream(entry.Name);
            using var memory = new MemoryStream((int)entry.Length);
            stream.CopyTo(memory);
            result[entry.Name] = memory.ToArray();
        }

        return result;
    }

    private static Dictionary<uint, DestListEntry> ParseDestList(byte[] data)
    {
        var result = new Dictionary<uint, DestListEntry>();
        if (data.Length < 32)
        {
            return result;
        }

        var version = BitConverter.ToUInt32(data, 0);
        var entryCount = Math.Min(BitConverter.ToUInt32(data, 4), 4096);
        var offset = 32;
        var order = 0;
        while (offset + 112 <= data.Length && order < entryCount)
        {
            var entryStart = offset;
            var pathSizeOffset = version == 1 ? entryStart + 112 : entryStart + 128;
            if (pathSizeOffset + 2 > data.Length)
            {
                break;
            }

            var entryNumber = BitConverter.ToUInt32(data, entryStart + 88);
            var pinStatus = BitConverter.ToInt32(data, entryStart + 108);
            var pathCharCount = BitConverter.ToUInt16(data, pathSizeOffset);
            var pathByteLength = pathCharCount * 2;
            var pathStart = pathSizeOffset + 2;
            if (pathStart + pathByteLength > data.Length)
            {
                break;
            }

            var entryLength = pathStart + pathByteLength - entryStart;
            if (version != 1)
            {
                entryLength += 4;
            }

            result[entryNumber] = new DestListEntry(pinStatus != -1, order);
            offset = entryStart + entryLength;
            order++;
        }

        return result;
    }

    private static JumpListMenuEntry? CreateMenuEntry(ShellDestinationEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Title) || string.IsNullOrWhiteSpace(entry.TargetPath))
        {
            return null;
        }

        var key = $"shell-file:{entry.TargetPath}|{entry.Arguments}|{entry.WorkingDirectory}";
        return new JumpListMenuEntry(
            entry.Title,
            key,
            () => Launch(entry.TargetPath, entry.Arguments, entry.WorkingDirectory));
    }

    private static string ExtractCustomCategoryTitle(byte[] data, int start, int limit)
    {
        var end = Math.Min(limit, IndexOfOrLength(data, CategoryFooterSignature, start));
        var firstLink = FindNextShellLinkStart(data, start);
        if (firstLink >= start && firstLink < end)
        {
            end = firstLink;
        }

        var candidates = new List<string>();
        for (var offset = start; offset <= end - 4; offset++)
        {
            if (TryReadLengthPrefixedUnicode(data, offset, sizeof(short), end, 80, out var shortValue) &&
                IsUsefulCategoryTitle(shortValue))
            {
                candidates.Add(shortValue);
            }

            if (TryReadLengthPrefixedUnicode(data, offset, sizeof(int), end, 80, out var intValue) &&
                IsUsefulCategoryTitle(intValue))
            {
                candidates.Add(intValue);
            }
        }

        return candidates.Count > 0 ? candidates[^1] : "Tasks";
    }

    private static bool TryReadLengthPrefixedUnicode(
        byte[] data,
        int offset,
        int prefixBytes,
        int limit,
        int maxCharacters,
        out string value)
    {
        value = "";
        if (offset < 0 || offset + prefixBytes > limit)
        {
            return false;
        }

        var charCount = prefixBytes == sizeof(short)
            ? BitConverter.ToUInt16(data, offset)
            : BitConverter.ToInt32(data, offset);
        if (charCount <= 0 || charCount > maxCharacters)
        {
            return false;
        }

        var byteCount = charCount * 2;
        var stringOffset = offset + prefixBytes;
        if (byteCount < 2 || stringOffset + byteCount > limit)
        {
            return false;
        }

        try
        {
            value = Encoding.Unicode.GetString(data, stringOffset, byteCount).Trim('\0', ' ', '\t', '\r', '\n');
            return true;
        }
        catch
        {
            value = "";
            return false;
        }
    }

    private static bool IsUsefulCategoryTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2 || value.Length > 80)
        {
            return false;
        }

        if (value.Any(character => char.IsControl(character) && !char.IsWhiteSpace(character)))
        {
            return false;
        }

        return !value.Contains('\\') &&
               !value.Contains("%", StringComparison.Ordinal) &&
               !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static int IndexOfOrLength(byte[] haystack, byte[] needle, int startAt)
    {
        var index = IndexOf(haystack, needle, startAt);
        return index >= 0 ? index : haystack.Length;
    }

    private static IEnumerable<string> GetExecutableCandidates(IReadOnlyList<TaskbarItem> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            foreach (var candidate in new[] { item.ProcessPath, item.IconPath, item.LaunchPath })
            {
                var normalized = NormalizePath(candidate);
                if (string.IsNullOrWhiteSpace(normalized) ||
                    !string.Equals(Path.GetExtension(normalized), ".exe", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(normalized) ||
                    !seen.Add(normalized))
                {
                    continue;
                }

                yield return normalized;
            }
        }
    }

    private static IReadOnlyList<string> EnumerateFiles(string directory, string pattern)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DebugLogger.WriteIfChanged(
                $"jump-list-directory-error-{directory}",
                $"Jump List directory scan failed: Directory={directory} Pattern={pattern} {exception.GetType().Name}: {exception.Message}");
            return Array.Empty<string>();
        }
    }

    private static bool TryParseShellLinkBytes(byte[] linkBytes, out ShellLinkInfo link)
    {
        link = default!;
        if (!LooksLikeShellLink(linkBytes))
        {
            return false;
        }

        object? shellLinkObject = null;
        IStream? stream = null;
        try
        {
            shellLinkObject = new ShellLink();
            var hr = CreateStreamOnHGlobal(IntPtr.Zero, fDeleteOnRelease: true, out stream);
            if (hr < 0 || stream is null)
            {
                return false;
            }

            stream.Write(linkBytes, linkBytes.Length, IntPtr.Zero);
            stream.Seek(0, 0, IntPtr.Zero);

            hr = ((IPersistStream)shellLinkObject).Load(stream);
            if (hr < 0)
            {
                return false;
            }

            var shellLink = (IShellLinkW)shellLinkObject;
            var targetPath = NormalizePath(ReadString(builder => shellLink.GetPath(builder, builder.Capacity, IntPtr.Zero, 0)));
            var arguments = ReadString(builder => shellLink.GetArguments(builder, builder.Capacity));
            var workingDirectory = NormalizePath(ReadString(builder => shellLink.GetWorkingDirectory(builder, builder.Capacity)));
            var description = ReadString(builder => shellLink.GetDescription(builder, builder.Capacity));
            var title = FirstNonEmpty(
                ReadPropertyTitle(shellLinkObject),
                ReadEmbeddedDisplayTitle(linkBytes, targetPath, arguments));
            var iconPath = ReadIconLocation(shellLink, out var iconIndex);

            link = new ShellLinkInfo(targetPath, arguments, workingDirectory, title, description, iconPath, iconIndex);
            return !string.IsNullOrWhiteSpace(targetPath);
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (stream is not null && Marshal.IsComObject(stream))
            {
                Marshal.FinalReleaseComObject(stream);
            }

            if (shellLinkObject is not null && Marshal.IsComObject(shellLinkObject))
            {
                Marshal.FinalReleaseComObject(shellLinkObject);
            }
        }
    }

    private static string ReadEmbeddedDisplayTitle(byte[] linkBytes, string targetPath, string arguments)
    {
        var candidates = new List<string>();
        for (var offset = 0; offset <= linkBytes.Length - 6; offset++)
        {
            var charCount = BitConverter.ToInt32(linkBytes, offset);
            if (charCount <= 0 || charCount > 160)
            {
                continue;
            }

            var byteCount = charCount * 2;
            var stringOffset = offset + sizeof(int);
            if (byteCount < 2 || stringOffset + byteCount > linkBytes.Length)
            {
                continue;
            }

            string value;
            try
            {
                value = Encoding.Unicode.GetString(linkBytes, stringOffset, byteCount).Trim('\0', ' ', '\t', '\r', '\n');
            }
            catch
            {
                continue;
            }

            if (IsUsefulEmbeddedTitle(value, targetPath, arguments))
            {
                candidates.Add(value);
            }
        }

        return candidates.Count == 0 ? "" : candidates[^1];
    }

    private static bool IsUsefulEmbeddedTitle(string value, string targetPath, string arguments)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2 || value.Length > 140)
        {
            return false;
        }

        if (value.Any(character => char.IsControl(character) && !char.IsWhiteSpace(character)))
        {
            return false;
        }

        if (value.Contains('\\') ||
            value.Contains("&C:", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("%ProgramFiles%", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
            value.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var targetName = Path.GetFileNameWithoutExtension(targetPath);
        if (value.Equals(targetName, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        return true;
    }

    private static bool LooksLikeShellLink(byte[] data) =>
        data.Length >= 0x4C &&
        BitConverter.ToUInt32(data, 0) == 0x4C &&
        ShellLinkGuidMarker.AsSpan().SequenceEqual(data.AsSpan(4, ShellLinkGuidMarker.Length));

    private static int FindNextShellLinkStart(byte[] data, int startAt)
    {
        var markerAt = Math.Max(4, startAt + 4);
        while (markerAt < data.Length)
        {
            var marker = IndexOf(data, ShellLinkGuidMarker, markerAt);
            if (marker < 0)
            {
                return -1;
            }

            var linkStart = marker - 4;
            if (linkStart >= startAt &&
                linkStart >= 0 &&
                BitConverter.ToUInt32(data, linkStart) == 0x4C)
            {
                return linkStart;
            }

            markerAt = marker + 1;
        }

        return -1;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int startAt)
    {
        if (startAt < 0 || startAt > haystack.Length - needle.Length)
        {
            return -1;
        }

        for (var index = startAt; index <= haystack.Length - needle.Length; index++)
        {
            var matched = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (haystack[index + offset] == needle[offset])
                {
                    continue;
                }

                matched = false;
                break;
            }

            if (matched)
            {
                return index;
            }
        }

        return -1;
    }

    private static string ReadString(Func<StringBuilder, int> read)
    {
        var builder = new StringBuilder(4096);
        return read(builder) >= 0 ? builder.ToString().Trim() : "";
    }

    private static string ReadPropertyTitle(object shellLinkObject)
    {
        if (shellLinkObject is not NativeMethods.IPropertyStore propertyStore)
        {
            return "";
        }

        var key = PkeyTitle;
        var propVariant = default(NativeMethods.PropVariant);
        try
        {
            return propertyStore.GetValue(ref key, out propVariant) >= 0
                ? propVariant.GetString()?.Trim() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
        finally
        {
            NativeMethods.PropVariantClear(ref propVariant);
        }
    }

    private static string ReadIconLocation(IShellLinkW shellLink, out int iconIndex)
    {
        var builder = new StringBuilder(4096);
        return shellLink.GetIconLocation(builder, builder.Capacity, out iconIndex) >= 0
            ? NormalizePath(builder.ToString())
            : "";
    }

    private static string GetEntryTitle(ShellLinkInfo link)
    {
        if (!string.IsNullOrWhiteSpace(link.Title))
        {
            return link.Title;
        }

        if (!string.IsNullOrWhiteSpace(link.Description))
        {
            return link.Description;
        }

        if (!string.IsNullOrWhiteSpace(link.Arguments) &&
            Uri.TryCreate(link.Arguments.Trim().Trim('"'), UriKind.Absolute, out var uri))
        {
            return uri.Host.Length > 0 ? uri.Host : uri.ToString();
        }

        var fileName = Path.GetFileNameWithoutExtension(link.TargetPath);
        return string.IsNullOrWhiteSpace(fileName) ? link.TargetPath : fileName;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static bool PathsRoughlyMatch(string linkTarget, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(linkTarget) || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var normalizedLinkTarget = NormalizePath(linkTarget);
        var normalizedExecutablePath = NormalizePath(executablePath);
        if (string.Equals(normalizedLinkTarget, normalizedExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            Path.GetFileName(normalizedLinkTarget),
            Path.GetFileName(normalizedExecutablePath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        try
        {
            return Path.GetFullPath(expanded).TrimEnd('\\');
        }
        catch
        {
            return expanded;
        }
    }

    private static byte[] ReadFileWithSharing(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void Launch(string path, string arguments, string workingDirectory)
    {
        try
        {
            var startInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Arguments = arguments
            };

            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            DebugLogger.WriteIfChanged(
                $"jump-list-file-launch-error-{path}-{arguments}",
                $"Jump List destination launch failed: Path={path} Args={arguments} {exception.GetType().Name}: {exception.Message}");
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CreateStreamOnHGlobal(IntPtr hGlobal, [MarshalAs(UnmanagedType.Bool)] bool fDeleteOnRelease, out IStream stream);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    [ComImport]
    [Guid("00000109-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistStream
    {
        [PreserveSig]
        int GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        [PreserveSig]
        int Load(IStream stream);

        [PreserveSig]
        int Save(IStream stream, [MarshalAs(UnmanagedType.Bool)] bool clearDirty);

        [PreserveSig]
        int GetSizeMax(out long size);
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

    private sealed record ShellLinkInfo(
        string TargetPath,
        string Arguments,
        string WorkingDirectory,
        string Title,
        string Description,
        string IconPath,
        int IconIndex);

    private sealed record ShellDestinationEntry(
        string SectionTitle,
        string Title,
        string TargetPath,
        string Arguments,
        string WorkingDirectory,
        string IconPath,
        int IconIndex,
        bool IsPinned,
        int Order);

    private sealed record DestListEntry(bool IsPinned, int Order);
}
