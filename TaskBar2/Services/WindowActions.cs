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
}
