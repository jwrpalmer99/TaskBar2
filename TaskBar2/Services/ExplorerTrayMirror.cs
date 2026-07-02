using TaskBar2.Native;

namespace TaskBar2.Services;

internal sealed class ExplorerTrayMirror
{
    public TrayMirrorGeometry? GetGeometry()
    {
        var shellTray = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (shellTray == IntPtr.Zero || !NativeMethods.GetWindowRect(shellTray, out var shellRect))
        {
            return null;
        }

        var tray = FindDescendant(shellTray, "TrayNotifyWnd");
        if (tray == IntPtr.Zero || !NativeMethods.GetWindowRect(tray, out var trayRect))
        {
            return null;
        }

        var sourceRect = new NativeMethods.Rect
        {
            Left = trayRect.Left - shellRect.Left,
            Top = trayRect.Top - shellRect.Top,
            Right = trayRect.Right - shellRect.Left,
            Bottom = trayRect.Bottom - shellRect.Top
        };

        return new TrayMirrorGeometry(shellTray, sourceRect, trayRect);
    }

    public void ForwardClick(NativeMethods.Rect trayScreenRect, double xRatio, double yRatio, bool rightClick)
    {
        var width = trayScreenRect.Right - trayScreenRect.Left;
        var height = trayScreenRect.Bottom - trayScreenRect.Top;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var x = trayScreenRect.Left + (int)Math.Clamp(xRatio * width, 0, width - 1);
        var y = trayScreenRect.Top + (int)Math.Clamp(yRatio * height, 0, height - 1);

        NativeMethods.SetCursorPos(x, y);
        if (rightClick)
        {
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
            return;
        }

        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    private static IntPtr FindDescendant(IntPtr parent, string className)
    {
        var direct = NativeMethods.FindWindowEx(parent, IntPtr.Zero, className, null);
        if (direct != IntPtr.Zero)
        {
            return direct;
        }

        var child = IntPtr.Zero;
        while (true)
        {
            child = NativeMethods.FindWindowEx(parent, child, null, null);
            if (child == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var nested = FindDescendant(child, className);
            if (nested != IntPtr.Zero)
            {
                return nested;
            }
        }
    }
}

internal sealed record TrayMirrorGeometry(
    IntPtr SourceWindow,
    NativeMethods.Rect SourceRect,
    NativeMethods.Rect TrayScreenRect);
