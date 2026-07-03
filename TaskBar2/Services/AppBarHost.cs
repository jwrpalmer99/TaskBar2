using System.Runtime.InteropServices;
using System.Windows.Forms;
using TaskBar2.Native;

namespace TaskBar2.Services;

internal sealed class AppBarHost : IDisposable
{
    private const int CallbackMessage = NativeMethods.WM_APP + 42;
    private readonly IntPtr _hwnd;
    private readonly Screen _screen;
    private int _heightPixels;
    private bool _registered;

    public AppBarHost(IntPtr hwnd, Screen screen, int heightPixels)
    {
        _hwnd = hwnd;
        _screen = screen;
        _heightPixels = heightPixels;
    }

    public void Register(bool positionWindow = true)
    {
        if (_registered)
        {
            return;
        }

        var data = CreateData();
        data.uCallbackMessage = CallbackMessage;
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data);
        _registered = true;
        Position(positionWindow);
    }

    public void Position(bool positionWindow = true)
    {
        if (!_registered)
        {
            return;
        }

        var bounds = _screen.Bounds;
        var data = CreateData();
        data.uEdge = NativeMethods.ABE_BOTTOM;
        data.rc.Left = bounds.Left;
        data.rc.Right = bounds.Right;
        data.rc.Bottom = bounds.Bottom;
        data.rc.Top = bounds.Bottom - _heightPixels;

        NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref data);
        data.rc.Top = data.rc.Bottom - _heightPixels;
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref data);

        if (positionWindow)
        {
            NativeMethods.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                data.rc.Left,
                data.rc.Top,
                data.rc.Right - data.rc.Left,
                data.rc.Bottom - data.rc.Top,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        DebugLogger.WriteIfChanged($"appbar-position-{_hwnd}", $"Appbar positioned: Hwnd=0x{_hwnd.ToInt64():X} Rect={data.rc.Left},{data.rc.Top},{data.rc.Right},{data.rc.Bottom} HeightPixels={_heightPixels}");
    }

    public void RepositionWithoutChangingReservation(bool positionWindow = true)
    {
        var bounds = _screen.Bounds;
        if (positionWindow)
        {
            NativeMethods.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                bounds.Left,
                bounds.Bottom - _heightPixels,
                bounds.Width,
                _heightPixels,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        DebugLogger.WriteIfChanged($"appbar-reposition-{_hwnd}", $"Appbar repositioned: Hwnd=0x{_hwnd.ToInt64():X} Bounds={bounds} HeightPixels={_heightPixels}");
    }

    public void SetHeight(int heightPixels)
    {
        _heightPixels = heightPixels;
        Position(positionWindow: true);
    }

    public void SetReservedHeight(int heightPixels)
    {
        _heightPixels = heightPixels;
        Position(positionWindow: false);
    }

    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        var data = CreateData();
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);
        _registered = false;
    }

    private NativeMethods.AppBarData CreateData()
    {
        return new NativeMethods.AppBarData
        {
            cbSize = Marshal.SizeOf<NativeMethods.AppBarData>(),
            hWnd = _hwnd
        };
    }
}
