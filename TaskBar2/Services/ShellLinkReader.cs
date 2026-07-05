using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace TaskBar2.Services;

internal static class ShellLinkReader
{
    private const int MaxStringLength = 4096;

    public static bool TryLoadFile(string path, out ShellLinkInfo link)
    {
        link = default;
        object? shellLinkObject = null;
        try
        {
            shellLinkObject = new ShellLink();
            if (((IPersistFile)shellLinkObject).Load(path, 0) < 0)
            {
                return false;
            }

            link = Read((IShellLinkW)shellLinkObject, "");
            return true;
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(shellLinkObject);
        }
    }

    public static bool TryLoadBytes(byte[] bytes, Func<object, string>? readTitle, out ShellLinkInfo link)
    {
        link = default;
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

            stream.Write(bytes, bytes.Length, IntPtr.Zero);
            stream.Seek(0, 0, IntPtr.Zero);

            if (((IPersistStream)shellLinkObject).Load(stream) < 0)
            {
                return false;
            }

            link = Read((IShellLinkW)shellLinkObject, readTitle?.Invoke(shellLinkObject) ?? "");
            return true;
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(stream);
            ReleaseComObject(shellLinkObject);
        }
    }

    public static ShellLinkInfo Read(IShellLinkW shellLink, string title = "")
    {
        var iconBuilder = new StringBuilder(MaxStringLength);
        var iconPath = shellLink.GetIconLocation(iconBuilder, iconBuilder.Capacity, out var iconIndex) >= 0
            ? iconBuilder.ToString().Trim()
            : "";

        return new ShellLinkInfo(
            ReadPath(shellLink),
            ReadString(shellLink.GetArguments),
            NormalizePath(ReadString(shellLink.GetWorkingDirectory)),
            title.Trim(),
            ReadString(shellLink.GetDescription),
            NormalizePath(iconPath),
            iconIndex);
    }

    public static string ReadPath(IShellLinkW shellLink) =>
        NormalizePath(ReadString(builder => shellLink.GetPath(builder, builder.Capacity, IntPtr.Zero, 0)));

    public static string ReadString(Func<StringBuilder, int, int> read)
    {
        var builder = new StringBuilder(MaxStringLength);
        return read(builder, builder.Capacity) >= 0 ? builder.ToString().Trim() : "";
    }

    private static string ReadString(Func<StringBuilder, int> read)
    {
        var builder = new StringBuilder(MaxStringLength);
        return read(builder) >= 0 ? builder.ToString().Trim() : "";
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

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CreateStreamOnHGlobal(
        IntPtr hGlobal,
        [MarshalAs(UnmanagedType.Bool)] bool fDeleteOnRelease,
        out IStream stream);

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
    internal interface IShellLinkW
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

internal readonly record struct ShellLinkInfo(
    string TargetPath,
    string Arguments,
    string WorkingDirectory,
    string Title,
    string Description,
    string IconPath,
    int IconIndex);
