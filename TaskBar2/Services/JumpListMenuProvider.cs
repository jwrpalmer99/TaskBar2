using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TaskBar2.Models;
using TaskBar2.Native;

namespace TaskBar2.Services;

internal static class JumpListMenuProvider
{
    private const int MaxEntriesPerSection = 8;
    private static readonly Guid ClsidApplicationDocumentLists = new("86BEC222-30F2-47E0-9F25-60D11CD75C28");
    private static readonly Guid IidIObjectArray = new("92CA9DCD-5622-4bba-A805-5E9F541BD8C9");
    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly NativeMethods.PropertyKey PkeyTitle = new()
    {
        FormatId = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"),
        PropertyId = 2
    };

    public static IReadOnlyList<JumpListMenuSection> GetSections(IReadOnlyList<TaskbarItem> items)
    {
        if (items.Count == 0)
        {
            return Array.Empty<JumpListMenuSection>();
        }

        var sections = new List<JumpListMenuSection>();
        var staticTasks = GetStaticTasks(items);

        if (ChromiumJumpListProvider.TryGetSections(items, out var chromiumSections))
        {
            sections.AddRange(chromiumSections);
            if (staticTasks.Count > 0)
            {
                sections.Add(new JumpListMenuSection("Tasks", staticTasks));
            }

            return sections;
        }

        var customSections = ShellJumpListFileProvider.GetCustomDestinationSections(items);
        if (customSections.Count > 0)
        {
            sections.AddRange(MergeStaticTasksIntoCustomSections(staticTasks, customSections));
        }
        else if (staticTasks.Count > 0)
        {
            sections.Add(new JumpListMenuSection("Tasks", staticTasks));
        }

        var fileDestinationSections = ShellJumpListFileProvider.GetAutomaticDestinationSections(items);
        if (fileDestinationSections.Count > 0)
        {
            sections.AddRange(fileDestinationSections);
            return sections;
        }

        var destinationSections = GetDestinationSections(items);
        sections.AddRange(destinationSections);
        return sections;
    }

    private static IReadOnlyList<JumpListMenuSection> MergeStaticTasksIntoCustomSections(
        IReadOnlyList<JumpListMenuEntry> staticTasks,
        IReadOnlyList<JumpListMenuSection> customSections)
    {
        if (staticTasks.Count == 0)
        {
            return customSections;
        }

        var merged = new List<JumpListMenuSection>(customSections.Count + 1);
        var addedStaticTasks = false;
        foreach (var section in customSections)
        {
            if (section.Title.Equals("Tasks", StringComparison.OrdinalIgnoreCase))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var entries = Deduplicate(section.Entries.Concat(staticTasks), seen);
                merged.Add(new JumpListMenuSection(section.Title, entries));
                addedStaticTasks = true;
            }
            else
            {
                merged.Add(section);
            }
        }

        if (!addedStaticTasks)
        {
            merged.Add(new JumpListMenuSection("Tasks", staticTasks));
        }

        return merged;
    }

    private static IReadOnlyList<JumpListMenuSection> GetDestinationSections(IReadOnlyList<TaskbarItem> items)
    {
        foreach (var appId in GetAppIdCandidates(items))
        {
            var frequent = GetDestinations(appId, AppDocListType.Frequent);
            var recent = GetDestinations(appId, AppDocListType.Recent);
            if (frequent.Count == 0 && recent.Count == 0)
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sections = new List<JumpListMenuSection>();
            frequent = Deduplicate(frequent, seen);
            if (frequent.Count > 0)
            {
                sections.Add(new JumpListMenuSection("Frequent", frequent));
            }

            recent = Deduplicate(recent, seen);
            if (recent.Count > 0)
            {
                sections.Add(new JumpListMenuSection("Recent", recent));
            }

            DebugLogger.WriteIfChanged(
                $"jump-list-loaded-{items[0].GroupKey}",
                $"Jump List destinations loaded: Group={items[0].GroupKey} AppId={appId} Frequent={frequent.Count} Recent={recent.Count}");
            return sections;
        }

        DebugLogger.WriteIfChanged(
            $"jump-list-empty-{items[0].GroupKey}",
            $"Jump List destinations unavailable: Group={items[0].GroupKey} Candidates={string.Join(" | ", GetAppIdCandidates(items).Take(8))}");
        return Array.Empty<JumpListMenuSection>();
    }

    private static IReadOnlyList<JumpListMenuEntry> Deduplicate(
        IEnumerable<JumpListMenuEntry> entries,
        HashSet<string> seen)
    {
        var result = new List<JumpListMenuEntry>();
        foreach (var entry in entries)
        {
            if (seen.Add(entry.Key))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private static IReadOnlyList<JumpListMenuEntry> GetStaticTasks(IReadOnlyList<TaskbarItem> items)
    {
        var representative = items.First();
        var processName = Path.GetFileNameWithoutExtension(representative.ProcessName).ToLowerInvariant();
        var processPath = representative.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            return Array.Empty<JumpListMenuEntry>();
        }

        return processName switch
        {
            "chrome" =>
            [
                CreateLaunchEntry("New window", processPath, "--new-window"),
                CreateLaunchEntry("New incognito window", processPath, "--incognito")
            ],
            "msedge" =>
            [
                CreateLaunchEntry("New window", processPath, "--new-window"),
                CreateLaunchEntry("New InPrivate window", processPath, "--inprivate")
            ],
            "ms-teams" or "msteams" or "teams" =>
            [
                CreateUriEntry("Schedule Meeting", "https://teams.microsoft.com/l/meeting/new"),
                CreateUriEntry("New Message", "https://teams.microsoft.com/l/chat/0/0")
            ],
            _ => Array.Empty<JumpListMenuEntry>()
        };
    }

    private static JumpListMenuEntry CreateLaunchEntry(string title, string path, string arguments)
    {
        var key = $"launch:{path}|{arguments}";
        return new JumpListMenuEntry(title, key, () => Launch(path, arguments, Path.GetDirectoryName(path) ?? ""));
    }

    private static JumpListMenuEntry CreateUriEntry(string title, string uri)
    {
        var key = $"uri:{uri}";
        return new JumpListMenuEntry(title, key, () => Launch(uri, "", ""));
    }

    private static IReadOnlyList<JumpListMenuEntry> GetDestinations(string appId, AppDocListType listType)
    {
        var documentLists = CreateApplicationDocumentLists();
        if (documentLists is null)
        {
            return Array.Empty<JumpListMenuEntry>();
        }

        object? objectArrayObject = null;
        try
        {
            var hr = documentLists.SetAppID(appId);
            if (hr < 0)
            {
                return Array.Empty<JumpListMenuEntry>();
            }

            var iid = IidIObjectArray;
            hr = documentLists.GetList(listType, MaxEntriesPerSection, ref iid, out objectArrayObject);
            if (hr < 0 || objectArrayObject is not IObjectArray objectArray)
            {
                return Array.Empty<JumpListMenuEntry>();
            }

            hr = objectArray.GetCount(out var count);
            if (hr < 0 || count == 0)
            {
                return Array.Empty<JumpListMenuEntry>();
            }

            var entries = new List<JumpListMenuEntry>();
            for (uint index = 0; index < count && entries.Count < MaxEntriesPerSection; index++)
            {
                var itemIid = IidIUnknown;
                object? item = null;
                try
                {
                    if (objectArray.GetAt(index, ref itemIid, out item) < 0 || item is null)
                    {
                        continue;
                    }

                    var entry = CreateEntry(item);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
                finally
                {
                    ReleaseComObject(item);
                }
            }

            return entries;
        }
        catch (COMException exception)
        {
            DebugLogger.WriteIfChanged(
                $"jump-list-com-error-{appId}-{listType}",
                $"Jump List query failed: AppId={appId} Type={listType} HResult=0x{exception.HResult:X8} {exception.Message}");
            return Array.Empty<JumpListMenuEntry>();
        }
        finally
        {
            ReleaseComObject(objectArrayObject);
            ReleaseComObject(documentLists);
        }
    }

    private static JumpListMenuEntry? CreateEntry(object item)
    {
        var propertyTitle = ReadTitle(item);
        if (item is IShellLinkW link)
        {
            var path = ReadShellLinkPath(link);
            var arguments = ReadShellLinkString(link.GetArguments);
            var workingDirectory = ReadShellLinkString(link.GetWorkingDirectory);
            var description = ReadShellLinkString(link.GetDescription);
            var title = FirstNonEmpty(propertyTitle, description, GetReadableTargetName(path, arguments));
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return new JumpListMenuEntry(
                title,
                $"link:{path}|{arguments}|{workingDirectory}",
                () => Launch(path, arguments, workingDirectory));
        }

        if (item is IShellItem shellItem)
        {
            var path = ReadShellItemDisplayName(shellItem, ShellItemDisplayName.FileSystemPath);
            var parsingName = ReadShellItemDisplayName(shellItem, ShellItemDisplayName.DesktopAbsoluteParsing);
            var displayName = ReadShellItemDisplayName(shellItem, ShellItemDisplayName.NormalDisplay);
            var target = FirstNonEmpty(path, parsingName);
            var title = FirstNonEmpty(propertyTitle, displayName, GetReadableTargetName(target, ""));
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            return new JumpListMenuEntry(
                title,
                $"item:{target}",
                () => Launch(target, "", File.Exists(path) ? Path.GetDirectoryName(path) ?? "" : ""));
        }

        return null;
    }

    private static IApplicationDocumentLists? CreateApplicationDocumentLists()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidApplicationDocumentLists, throwOnError: false);
            return type is null
                ? null
                : Activator.CreateInstance(type) as IApplicationDocumentLists;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> GetAppIdCandidates(IReadOnlyList<TaskbarItem> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();
        foreach (var item in items)
        {
            AddCandidate(item.AppUserModelId);

            if (item.GroupKey.StartsWith("appid:", StringComparison.OrdinalIgnoreCase))
            {
                AddCandidate(item.GroupKey["appid:".Length..]);
            }

            AddCandidate(GetImplicitAppIdFromPath(item.ProcessPath));
            AddCandidate(item.ProcessPath);

            var processName = Path.GetFileNameWithoutExtension(item.ProcessName).ToLowerInvariant();
            switch (processName)
            {
                case "chrome":
                    AddCandidate("Chrome");
                    break;
                case "msedge":
                    AddCandidate("MSEdge");
                    break;
                case "explorer":
                    AddCandidate("Microsoft.Windows.Explorer");
                    break;
            }
        }

        void AddCandidate(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
            {
                candidates.Add(value);
            }
        }

        foreach (var candidate in candidates)
        {
            yield return candidate;
        }
    }

    private static string GetImplicitAppIdFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.GetFolderPath(Environment.SpecialFolder.System)
        };

        foreach (var replacement in replacements)
        {
            if (path.StartsWith(replacement.Value, StringComparison.OrdinalIgnoreCase))
            {
                return replacement.Key + path[replacement.Value.Length..];
            }
        }

        return path;
    }

    private static string ReadTitle(object item)
    {
        if (item is not NativeMethods.IPropertyStore propertyStore)
        {
            return "";
        }

        var key = PkeyTitle;
        var propVariant = default(NativeMethods.PropVariant);
        try
        {
            return propertyStore.GetValue(ref key, out propVariant) >= 0
                ? propVariant.GetString() ?? ""
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

    private static string ReadShellItemDisplayName(IShellItem shellItem, ShellItemDisplayName displayName)
    {
        try
        {
            if (shellItem.GetDisplayName(displayName, out var pointer) < 0 || pointer == IntPtr.Zero)
            {
                return "";
            }

            try
            {
                return Marshal.PtrToStringUni(pointer) ?? "";
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        catch
        {
            return "";
        }
    }

    private static string ReadShellLinkString(Func<StringBuilder, int, int> reader)
    {
        var builder = new StringBuilder(4096);
        return reader(builder, builder.Capacity) >= 0
            ? builder.ToString()
            : "";
    }

    private static string ReadShellLinkPath(IShellLinkW link)
    {
        var builder = new StringBuilder(4096);
        return link.GetPath(builder, builder.Capacity, IntPtr.Zero, flags: 0) >= 0
            ? builder.ToString()
            : "";
    }

    private static string GetReadableTargetName(string path, string arguments)
    {
        if (!string.IsNullOrWhiteSpace(arguments) &&
            Uri.TryCreate(arguments.Trim('"'), UriKind.Absolute, out var uri))
        {
            return uri.Host.Length > 0 ? uri.Host : uri.ToString();
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var fileName = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

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
                $"jump-list-launch-error-{path}-{arguments}",
                $"Jump List launch failed: Path={path} Args={arguments} {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try
            {
                Marshal.FinalReleaseComObject(value);
            }
            catch
            {
            }
        }
    }

    private enum AppDocListType
    {
        Recent = 0,
        Frequent = 1
    }

    private enum ShellItemDisplayName : uint
    {
        NormalDisplay = 0,
        FileSystemPath = 0x80058000,
        DesktopAbsoluteParsing = 0x80028000
    }

    [ComImport]
    [Guid("3C594F9F-9F30-47A1-979A-C9E83D3D0A06")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationDocumentLists
    {
        [PreserveSig]
        int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        [PreserveSig]
        int GetList(
            AppDocListType listType,
            uint itemsDesired,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object? value);
    }

    [ComImport]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? value);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr bindContext, ref Guid bhid, ref Guid riid, out IntPtr value);

        [PreserveSig]
        int GetParent(out IShellItem parent);

        [PreserveSig]
        int GetDisplayName(ShellItemDisplayName sigdnName, out IntPtr name);

        [PreserveSig]
        int GetAttributes(uint attributesMask, out uint attributes);

        [PreserveSig]
        int Compare(IShellItem shellItem, uint hint, out int order);
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

internal sealed record JumpListMenuSection(string Title, IReadOnlyList<JumpListMenuEntry> Entries);

internal sealed record JumpListMenuEntry(string Title, string Key, Action Execute);
