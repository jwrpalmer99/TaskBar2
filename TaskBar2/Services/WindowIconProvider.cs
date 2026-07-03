using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using TaskBar2.Native;

namespace TaskBar2.Services;

internal static class WindowIconProvider
{
    private const int DefaultIconSize = 32;
    private static readonly string ExplorerExecutablePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "explorer.exe");

    public static string GetIconFingerprint(
        IntPtr hwnd,
        string? executablePath = null,
        string? iconPath = null,
        int iconIndex = 0)
    {
        var sourcePath = GetIconSourcePath(hwnd, executablePath, iconPath);
        var iconHandle = IsExplorerPath(sourcePath)
            ? IntPtr.Zero
            : GetWindowIconHandle(hwnd);
        if (iconHandle != IntPtr.Zero)
        {
            return $"hicon:{iconHandle.ToInt64():X}";
        }

        if (sourcePath is null)
        {
            return "";
        }

        try
        {
            var info = new FileInfo(sourcePath);
            return $"file:{info.FullName.ToUpperInvariant()}:{info.Length}:{info.LastWriteTimeUtc.Ticks}:index:{iconIndex}";
        }
        catch
        {
            return $"file:{sourcePath.ToUpperInvariant()}:index:{iconIndex}";
        }
    }

    public static ImageSource? GetIcon(
        IntPtr hwnd,
        string? executablePath = null,
        string? iconPath = null,
        int iconIndex = 0,
        int desiredSize = DefaultIconSize)
    {
        var sourcePath = GetIconSourcePath(hwnd, executablePath, iconPath);
        var iconHandle = IsExplorerPath(sourcePath)
            ? IntPtr.Zero
            : GetWindowIconHandle(hwnd);
        if (iconHandle != IntPtr.Zero)
        {
            if (!IsIconAtLeastSize(iconHandle, desiredSize) &&
                sourcePath is not null &&
                TryExtractIconHandle(sourcePath, iconIndex, desiredSize, out var replacementIcon))
            {
                try
                {
                    return CreateImage(replacementIcon);
                }
                finally
                {
                    NativeMethods.DestroyIcon(replacementIcon);
                }
            }

            return CreateImage(iconHandle);
        }

        if (sourcePath is null)
        {
            return null;
        }

        if (TryExtractIconHandle(sourcePath, iconIndex, desiredSize, out var extractedIcon))
        {
            try
            {
                return CreateImage(extractedIcon);
            }
            finally
            {
                NativeMethods.DestroyIcon(extractedIcon);
            }
        }

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(sourcePath);
            return icon is null ? null : CreateImage(icon.Handle);
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or UnauthorizedAccessException or ExternalException)
        {
            return null;
        }
    }

    public static IntPtr GetIconHandleCopy(
        IntPtr hwnd,
        string? executablePath = null,
        string? iconPath = null,
        int iconIndex = 0,
        int desiredSize = DefaultIconSize)
    {
        var sourcePath = GetIconSourcePath(hwnd, executablePath, iconPath);
        var iconHandle = IsExplorerPath(sourcePath)
            ? IntPtr.Zero
            : GetWindowIconHandle(hwnd);
        if (iconHandle != IntPtr.Zero)
        {
            if (!IsIconAtLeastSize(iconHandle, desiredSize) &&
                sourcePath is not null &&
                TryExtractIconHandle(sourcePath, iconIndex, desiredSize, out var replacementIcon))
            {
                return replacementIcon;
            }

            var copiedIcon = NativeMethods.CopyIcon(iconHandle);
            if (copiedIcon != IntPtr.Zero)
            {
                return copiedIcon;
            }
        }

        if (sourcePath is null)
        {
            return IntPtr.Zero;
        }

        if (TryExtractIconHandle(sourcePath, iconIndex, desiredSize, out var extractedIcon))
        {
            return extractedIcon;
        }

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(sourcePath);
            return icon is null ? IntPtr.Zero : NativeMethods.CopyIcon(icon.Handle);
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or UnauthorizedAccessException or ExternalException)
        {
            return IntPtr.Zero;
        }
    }

    private static string? GetIconSourcePath(IntPtr hwnd, string? executablePath, string? iconPath)
    {
        executablePath ??= GetExecutablePath(hwnd);

        if (IsExplorerProcess(executablePath, iconPath))
        {
            return File.Exists(ExplorerExecutablePath) ? ExplorerExecutablePath : executablePath;
        }

        foreach (var path in new[] { iconPath, executablePath })
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }
        }

        return executablePath is not null && File.Exists(executablePath)
            ? executablePath
            : null;
    }

    private static bool IsExplorerProcess(string? executablePath, string? iconPath) =>
        IsExplorerPath(executablePath) ||
        IsExplorerPath(iconPath);

    private static bool IsExplorerPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFileName(path), "explorer.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IntPtr GetWindowIconHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var icon = GetWindowIconHandle(hwnd, NativeMethods.ICON_BIG);
        if (icon != IntPtr.Zero)
        {
            return icon;
        }

        icon = GetWindowIconHandle(hwnd, NativeMethods.ICON_SMALL2);
        if (icon != IntPtr.Zero)
        {
            return icon;
        }

        icon = GetWindowIconHandle(hwnd, NativeMethods.ICON_SMALL);
        if (icon != IntPtr.Zero)
        {
            return icon;
        }

        var large = NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICON);
        if (large != IntPtr.Zero)
        {
            return large;
        }

        return NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICONSM);
    }

    private static IntPtr GetWindowIconHandle(IntPtr hwnd, int iconKind)
    {
        NativeMethods.SendMessageTimeout(
            hwnd,
            NativeMethods.WM_GETICON,
            iconKind,
            IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG,
            50,
            out var result);

        return result;
    }

    private static bool TryExtractIconHandle(string sourcePath, int iconIndex, int desiredSize, out IntPtr iconHandle)
    {
        iconHandle = IntPtr.Zero;
        var size = Math.Clamp(desiredSize, 16, 256);
        if (TryShellExtractIconHandle(sourcePath, iconIndex, size, out iconHandle))
        {
            return true;
        }

        var handles = new[] { IntPtr.Zero };
        var iconIds = new[] { 0 };
        uint extracted;
        try
        {
            extracted = NativeMethods.PrivateExtractIcons(
                sourcePath,
                iconIndex,
                size,
                size,
                handles,
                iconIds,
                1,
                0);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (extracted == 0 || extracted == uint.MaxValue || handles[0] == IntPtr.Zero)
        {
            return false;
        }

        iconHandle = handles[0];
        return true;
    }

    private static bool TryShellExtractIconHandle(string sourcePath, int iconIndex, int desiredSize, out IntPtr iconHandle)
    {
        iconHandle = IntPtr.Zero;
        var packedSize = (uint)(desiredSize | (desiredSize << 16));
        IntPtr largeIcon = IntPtr.Zero;
        IntPtr smallIcon = IntPtr.Zero;
        try
        {
            if (NativeMethods.SHDefExtractIcon(sourcePath, iconIndex, 0, out largeIcon, out smallIcon, packedSize) < 0)
            {
                return false;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (smallIcon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(smallIcon);
            }
        }

        if (largeIcon == IntPtr.Zero)
        {
            return false;
        }

        iconHandle = largeIcon;
        return true;
    }

    private static bool IsIconAtLeastSize(IntPtr iconHandle, int desiredSize)
    {
        if (desiredSize <= 0)
        {
            return true;
        }

        try
        {
            using var icon = Icon.FromHandle(iconHandle);
            return icon.Width >= desiredSize && icon.Height >= desiredSize;
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException)
        {
            return true;
        }
    }

    private static ImageSource CreateImage(IntPtr iconHandle)
    {
        var image = Imaging.CreateBitmapSourceFromHIcon(iconHandle, Int32Rect.Empty, null);
        image.Freeze();
        return image;
    }

    private static string? GetExecutablePath(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return null;
        }

        var processHandle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            processId);
        if (processHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var builder = new StringBuilder(1024);
            var length = builder.Capacity;
            return NativeMethods.QueryFullProcessImageName(processHandle, 0, builder, ref length)
                ? builder.ToString()
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(processHandle);
        }
    }
}
