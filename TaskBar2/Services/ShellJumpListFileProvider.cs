using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
    private const int MaxCachedSectionSets = 64;
    private static readonly object CacheSync = new();
    private static readonly Dictionary<string, CachedDestinationSections> SectionCache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<JumpListMenuSection> GetCustomDestinationSections(IReadOnlyList<TaskbarItem> items)
    {
        var fileSet = GetDestinationFileSet(CustomDestinationsDirectory, "*.customDestinations-ms");
        if (fileSet.Files.Count == 0)
        {
            return Array.Empty<JumpListMenuSection>();
        }

        foreach (var executablePath in GetExecutableCandidates(items))
        {
            if (ShouldSkipCustomDestinations(executablePath))
            {
                continue;
            }

            var cacheKey = $"custom:{NormalizePath(executablePath)}";
            if (TryGetCachedSections(cacheKey, fileSet.Stamp, out var cachedSections))
            {
                if (cachedSections.Count > 0)
                {
                    return cachedSections;
                }

                continue;
            }

            var sections = BuildCustomDestinationSections(executablePath, fileSet.Files);
            StoreCachedSections(cacheKey, fileSet.Stamp, sections);

            if (sections.Count > 0)
            {
                return sections;
            }
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
        var fileSet = GetDestinationFileSet(AutomaticDestinationsDirectory, "*.automaticDestinations-ms");
        if (fileSet.Files.Count == 0)
        {
            return Array.Empty<JumpListMenuSection>();
        }

        foreach (var executablePath in GetExecutableCandidates(items))
        {
            var cacheKey = $"automatic:{NormalizePath(executablePath)}";
            if (TryGetCachedSections(cacheKey, fileSet.Stamp, out var cachedSections))
            {
                if (cachedSections.Count > 0)
                {
                    return cachedSections;
                }

                continue;
            }

            var sections = BuildAutomaticDestinationSections(executablePath, fileSet.Files);
            StoreCachedSections(cacheKey, fileSet.Stamp, sections);

            if (sections.Count > 0)
            {
                return sections;
            }
        }

        return Array.Empty<JumpListMenuSection>();
    }

    private static IReadOnlyList<JumpListMenuSection> BuildCustomDestinationSections(
        string executablePath,
        IReadOnlyList<string> filePaths)
    {
        var entries = ReadCustomDestinationEntries(executablePath, filePaths)
            .ToArray();

        if (entries.Length == 0)
        {
            return Array.Empty<JumpListMenuSection>();
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

        DebugLogger.WriteIfChanged(
            $"jump-list-custom-loaded-{executablePath}",
            $"Jump List custom destinations loaded: Exe={executablePath} Sections={sections.Count} Count={entries.Length} Items={string.Join(" | ", entries.Select(entry => $"{entry.SectionTitle}:{entry.Title}"))}");
        return sections;
    }

    private static IReadOnlyList<JumpListMenuSection> BuildAutomaticDestinationSections(
        string executablePath,
        IReadOnlyList<string> filePaths)
    {
        var entries = ReadAutomaticDestinationEntries(executablePath, filePaths)
            .OrderByDescending(entry => entry.IsPinned)
            .ThenBy(entry => entry.Order)
            .ToArray();
        if (entries.Length == 0)
        {
            return Array.Empty<JumpListMenuSection>();
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

        DebugLogger.WriteIfChanged(
            $"jump-list-automatic-file-loaded-{executablePath}",
            $"Jump List automatic destinations loaded from files: Exe={executablePath} Sections={sections.Count} Count={entries.Length}");
        return sections;
    }

    private static IEnumerable<ShellDestinationEntry> ReadCustomDestinationEntries(
        string executablePath,
        IReadOnlyList<string> filePaths)
    {
        var executableHint = CreateExecutableHint(executablePath);
        foreach (var filePath in filePaths)
        {
            ShellDestinationEntry[] entries;
            try
            {
                entries = ParseCustomDestinationsFile(filePath, executablePath, executableHint).ToArray();
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

    private static IEnumerable<ShellDestinationEntry> ParseCustomDestinationsFile(
        string filePath,
        string executablePath,
        ExecutableHint executableHint)
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
            if (!ContainsExecutableHint(linkBytes, executableHint))
            {
                position = Math.Max(linkStart + 1, end);
                continue;
            }

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

    private static IEnumerable<ShellDestinationEntry> ReadAutomaticDestinationEntries(
        string executablePath,
        IReadOnlyList<string> filePaths)
    {
        var executableHint = CreateExecutableHint(executablePath);
        foreach (var filePath in filePaths)
        {
            ShellDestinationEntry[] entries;
            try
            {
                entries = ParseAutomaticDestinationsFile(filePath, executablePath, executableHint).ToArray();
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

    private static IEnumerable<ShellDestinationEntry> ParseAutomaticDestinationsFile(
        string filePath,
        string executablePath,
        ExecutableHint executableHint)
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
                !ContainsExecutableHint(stream.Value, executableHint) ||
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

    private static bool TryParseShellLinkBytes(byte[] linkBytes, out ShellLinkInfo link)
    {
        link = default!;
        if (!LooksLikeShellLink(linkBytes))
        {
            return false;
        }

        if (!ShellLinkReader.TryLoadBytes(linkBytes, ReadPropertyTitle, out link))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(link.Title))
        {
            link = link with
            {
                Title = ReadEmbeddedDisplayTitle(linkBytes, link.TargetPath, link.Arguments)
            };
        }

        return !string.IsNullOrWhiteSpace(link.TargetPath);
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

        var relativeIndex = haystack.AsSpan(startAt).IndexOf(needle);
        return relativeIndex < 0 ? -1 : startAt + relativeIndex;
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

    private static DestinationFileSet GetDestinationFileSet(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return new DestinationFileSet("missing", Array.Empty<string>());
        }

        try
        {
            var files = Directory
                .EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();

            ulong hash = 14695981039346656037UL;
            foreach (var file in files)
            {
                AddHash(ref hash, file.Name);
                AddHash(ref hash, file.Length);
                AddHash(ref hash, file.LastWriteTimeUtc.Ticks);
            }

            return new DestinationFileSet(
                $"{files.Length}:{hash:X16}",
                files.Select(file => file.FullName).ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DebugLogger.WriteIfChanged(
                $"jump-list-directory-error-{directory}",
                $"Jump List directory scan failed: Directory={directory} Pattern={pattern} {exception.GetType().Name}: {exception.Message}");
            return new DestinationFileSet("error", Array.Empty<string>());
        }
    }

    private static bool TryGetCachedSections(string cacheKey, string stamp, out IReadOnlyList<JumpListMenuSection> sections)
    {
        lock (CacheSync)
        {
            if (SectionCache.TryGetValue(cacheKey, out var cached) &&
                string.Equals(cached.Stamp, stamp, StringComparison.Ordinal))
            {
                sections = cached.Sections;
                return true;
            }
        }

        sections = Array.Empty<JumpListMenuSection>();
        return false;
    }

    private static void StoreCachedSections(
        string cacheKey,
        string stamp,
        IReadOnlyList<JumpListMenuSection> sections)
    {
        lock (CacheSync)
        {
            if (SectionCache.Count >= MaxCachedSectionSets && !SectionCache.ContainsKey(cacheKey))
            {
                SectionCache.Clear();
            }

            SectionCache[cacheKey] = new CachedDestinationSections(stamp, sections);
        }
    }

    private static ExecutableHint CreateExecutableHint(string executablePath)
    {
        var fileName = Path.GetFileName(executablePath);
        var baseName = Path.GetFileNameWithoutExtension(executablePath);
        return new ExecutableHint(fileName ?? "", baseName ?? "");
    }

    private static bool ContainsExecutableHint(byte[] data, ExecutableHint hint)
    {
        if (string.IsNullOrWhiteSpace(hint.FileName))
        {
            return true;
        }

        if (!IsAscii(hint.FileName) || !IsAscii(hint.BaseName))
        {
            return true;
        }

        return ContainsAsciiIgnoreCase(data, hint.FileName) ||
               ContainsUtf16AsciiIgnoreCase(data, hint.FileName) ||
               (!string.IsNullOrWhiteSpace(hint.BaseName) &&
                (ContainsAsciiIgnoreCase(data, hint.BaseName) ||
                 ContainsUtf16AsciiIgnoreCase(data, hint.BaseName)));
    }

    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> data, string text)
    {
        if (text.Length == 0 || data.Length < text.Length)
        {
            return false;
        }

        for (var index = 0; index <= data.Length - text.Length; index++)
        {
            var matched = true;
            for (var characterIndex = 0; characterIndex < text.Length; characterIndex++)
            {
                if (!EqualsAsciiIgnoreCase(data[index + characterIndex], text[characterIndex]))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsUtf16AsciiIgnoreCase(ReadOnlySpan<byte> data, string text)
    {
        var byteLength = text.Length * 2;
        if (text.Length == 0 || data.Length < byteLength)
        {
            return false;
        }

        for (var index = 0; index <= data.Length - byteLength; index += 2)
        {
            var matched = true;
            for (var characterIndex = 0; characterIndex < text.Length; characterIndex++)
            {
                var byteIndex = index + characterIndex * 2;
                if (data[byteIndex + 1] != 0 ||
                    !EqualsAsciiIgnoreCase(data[byteIndex], text[characterIndex]))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static bool EqualsAsciiIgnoreCase(byte value, char expected)
    {
        if (expected > 127)
        {
            return false;
        }

        var expectedByte = (byte)expected;
        if (value >= (byte)'A' && value <= (byte)'Z')
        {
            value = (byte)(value + 32);
        }

        if (expectedByte >= (byte)'A' && expectedByte <= (byte)'Z')
        {
            expectedByte = (byte)(expectedByte + 32);
        }

        return value == expectedByte;
    }

    private static bool IsAscii(string value)
    {
        foreach (var character in value)
        {
            if (character > 127)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddHash(ref ulong hash, string value)
    {
        foreach (var character in value)
        {
            AddHash(ref hash, character);
        }
    }

    private static void AddHash(ref ulong hash, long value)
    {
        unchecked
        {
            for (var shift = 0; shift < 64; shift += 8)
            {
                AddHash(ref hash, (byte)(value >> shift));
            }
        }
    }

    private static void AddHash(ref ulong hash, char value)
    {
        AddHash(ref hash, (byte)value);
        AddHash(ref hash, (byte)(value >> 8));
    }

    private static void AddHash(ref ulong hash, byte value)
    {
        unchecked
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
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

    private sealed record CachedDestinationSections(string Stamp, IReadOnlyList<JumpListMenuSection> Sections);

    private sealed record DestinationFileSet(string Stamp, IReadOnlyList<string> Files);

    private sealed record ExecutableHint(string FileName, string BaseName);
}
