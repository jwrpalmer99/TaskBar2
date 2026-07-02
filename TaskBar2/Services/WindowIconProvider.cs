using System.Drawing;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using TaskBar2.Native;

namespace TaskBar2.Services;

internal static class WindowIconProvider
{
    public static string GetIconFingerprint(IntPtr hwnd, string? executablePath = null)
    {
        var iconHandle = GetWindowIconHandle(hwnd);
        if (iconHandle != IntPtr.Zero)
        {
            return $"hicon:{iconHandle.ToInt64():X}";
        }

        executablePath ??= GetExecutablePath(hwnd);
        if (executablePath is null || !File.Exists(executablePath))
        {
            return "";
        }

        try
        {
            var info = new FileInfo(executablePath);
            return $"file:{info.FullName.ToUpperInvariant()}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return $"file:{executablePath.ToUpperInvariant()}";
        }
    }

    public static ImageSource? GetIcon(IntPtr hwnd, string? executablePath = null)
    {
        var iconHandle = GetWindowIconHandle(hwnd);
        if (iconHandle != IntPtr.Zero)
        {
            return CreateImage(iconHandle);
        }

        executablePath ??= GetExecutablePath(hwnd);
        if (executablePath is null || !File.Exists(executablePath))
        {
            return null;
        }

        using var icon = Icon.ExtractAssociatedIcon(executablePath);
        return icon is null ? null : CreateImage(icon.Handle);
    }

    public static IntPtr GetIconHandleCopy(IntPtr hwnd, string? executablePath = null)
    {
        var iconHandle = GetWindowIconHandle(hwnd);
        if (iconHandle != IntPtr.Zero)
        {
            return NativeMethods.CopyIcon(iconHandle);
        }

        executablePath ??= GetExecutablePath(hwnd);
        if (executablePath is null || !File.Exists(executablePath))
        {
            return IntPtr.Zero;
        }

        using var icon = Icon.ExtractAssociatedIcon(executablePath);
        return icon is null ? IntPtr.Zero : NativeMethods.CopyIcon(icon.Handle);
    }

    private static IntPtr GetWindowIconHandle(IntPtr hwnd)
    {
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
