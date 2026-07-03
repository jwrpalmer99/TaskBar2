using System.Drawing;
using System.Runtime.InteropServices;
using TaskBar2.Native;

namespace TaskBar2.Services;

internal static class WindowActions
{
    public static void Activate(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        NativeMethods.SetForegroundWindow(hwnd);
    }

    public static void ActivateOrMinimize(IntPtr hwnd)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == hwnd && !NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
            return;
        }

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        NativeMethods.SetForegroundWindow(hwnd);
    }

    public static void Minimize(IntPtr hwnd)
    {
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
    }

    public static void Close(IntPtr hwnd)
    {
        NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public static bool MoveToMonitor(IntPtr hwnd, Rectangle targetWorkArea)
    {
        if (hwnd == IntPtr.Zero || targetWorkArea.Width <= 0 || targetWorkArea.Height <= 0)
        {
            return false;
        }

        var wasMinimized = NativeMethods.IsIconic(hwnd);
        var wasMaximized = NativeMethods.IsZoomed(hwnd);
        if (wasMinimized || wasMaximized)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var sourceWorkArea = GetWindowWorkArea(hwnd, targetWorkArea);
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var moveWidth = Math.Min(width, targetWorkArea.Width);
        var moveHeight = Math.Min(height, targetWorkArea.Height);
        var x = CalculateTranslatedPosition(
            rect.Left,
            width,
            sourceWorkArea.Left,
            sourceWorkArea.Width,
            targetWorkArea.Left,
            targetWorkArea.Width,
            moveWidth);
        var y = CalculateTranslatedPosition(
            rect.Top,
            height,
            sourceWorkArea.Top,
            sourceWorkArea.Height,
            targetWorkArea.Top,
            targetWorkArea.Height,
            moveHeight);

        var moved = NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            x,
            y,
            moveWidth,
            moveHeight,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        if (wasMaximized)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MAXIMIZE);
        }

        return moved;
    }

    public static void ShowSystemMenu(IntPtr hwnd)
    {
        var menu = NativeMethods.GetSystemMenu(hwnd, false);
        if (menu == IntPtr.Zero || !NativeMethods.GetCursorPos(out var point))
        {
            return;
        }

        NativeMethods.SetForegroundWindow(hwnd);
        var command = NativeMethods.TrackPopupMenu(
            menu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
            point.X,
            point.Y,
            0,
            hwnd,
            IntPtr.Zero);

        if (command != 0)
        {
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_SYSCOMMAND, new IntPtr(command), IntPtr.Zero);
        }
    }

    private static Rectangle GetWindowWorkArea(IntPtr hwnd, Rectangle fallback)
    {
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return fallback;
        }

        var info = new NativeMethods.MonitorInfoEx
        {
            cbSize = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
            szDevice = ""
        };

        return NativeMethods.GetMonitorInfo(monitor, ref info)
            ? Rectangle.FromLTRB(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom)
            : fallback;
    }

    private static int CalculateTranslatedPosition(
        int currentStart,
        int currentExtent,
        int sourceStart,
        int sourceExtent,
        int targetStart,
        int targetExtent,
        int targetItemExtent)
    {
        var sourceTravel = sourceExtent - currentExtent;
        var targetTravel = targetExtent - targetItemExtent;
        if (sourceTravel <= 0 || targetTravel <= 0)
        {
            return targetStart + Math.Max(0, targetTravel / 2);
        }

        var ratio = Math.Clamp((double)(currentStart - sourceStart) / sourceTravel, 0, 1);
        return targetStart + (int)Math.Round(targetTravel * ratio);
    }
}
