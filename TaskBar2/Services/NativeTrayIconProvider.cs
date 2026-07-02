using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using TaskBar2.Models;
using TaskBar2.Native;

namespace TaskBar2.Services;

internal sealed class NativeTrayIconProvider
{
    private const int RemoteBufferBytes = 4096;

    public IReadOnlyList<TrayIconItem> GetIcons()
    {
        var icons = new List<TrayIconItem>();
        var shellTray = NativeMethods.FindWindow("Shell_TrayWnd", null);
        var toolbars = FindTrayToolbars(shellTray);
        foreach (var toolbar in toolbars)
        {
            icons.AddRange(ReadToolbar(toolbar));
        }

        if (icons.Count == 0)
        {
            icons.AddRange(TrayAutomationIconProvider.GetIcons(shellTray));
        }

        DebugLogger.WriteIfChanged(
            "tray-summary",
            $"Tray refresh: Toolbars={toolbars.Count} Icons={icons.Count} IconsWithImages={icons.Count(icon => icon.Icon is not null)}");
        return icons;
    }

    public void ForwardClick(TrayIconItem item, bool rightClick, bool doubleClick = false)
    {
        if (item.ScreenRect.Right <= item.ScreenRect.Left || item.ScreenRect.Bottom <= item.ScreenRect.Top)
        {
            DebugLogger.WriteIfChanged("tray-click-invalid-rect", $"Tray click ignored because the item has no screen rectangle. ToolTip={item.ToolTip}");
            return;
        }

        var x = item.ScreenRect.Left + Math.Max(1, (item.ScreenRect.Right - item.ScreenRect.Left) / 2);
        var y = item.ScreenRect.Top + Math.Max(1, (item.ScreenRect.Bottom - item.ScreenRect.Top) / 2);

        NativeMethods.SetCursorPos(x, y);
        if (rightClick)
        {
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
            return;
        }

        if (doubleClick)
        {
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            return;
        }

        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    private static List<IntPtr> FindTrayToolbars(IntPtr shellTray)
    {
        var trayNotify = FindDescendant(shellTray, "TrayNotifyWnd");
        var overflow = NativeMethods.FindWindow("NotifyIconOverflowWindow", null);
        var roots = new[] { shellTray, trayNotify, overflow }
            .Concat(FindTopLevelWindows("Shell_SecondaryTrayWnd"))
            .Where(root => root != IntPtr.Zero)
            .Distinct()
            .ToArray();
        var toolbars = new List<IntPtr>();

        foreach (var root in roots)
        {
            foreach (var toolbar in FindDescendants(root, "ToolbarWindow32"))
            {
                var buttonCount = NativeMethods.SendMessage(toolbar, NativeMethods.TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                DebugLogger.WriteIfChanged($"tray-toolbar-{toolbar}", $"Tray toolbar found: Hwnd=0x{toolbar.ToInt64():X} Root=0x{root.ToInt64():X} ButtonCount={buttonCount}");
                if (buttonCount > 0)
                {
                    toolbars.Add(toolbar);
                }
            }
        }

        DebugLogger.WriteIfChanged(
            "tray-roots",
            $"Tray roots: ShellTray=0x{shellTray.ToInt64():X} TrayNotify=0x{trayNotify.ToInt64():X} Overflow=0x{overflow.ToInt64():X} Roots={roots.Length} ActiveToolbars={toolbars.Count}");

        if (toolbars.Count == 0)
        {
            DebugLogger.WriteIfChanged("tray-window-tree", BuildWindowTreeSummary(roots));
            DebugLogger.WriteIfChanged("tray-uia-tree", TrayAutomationDiagnostics.BuildSummary(shellTray));
        }

        return toolbars;
    }

    private static IEnumerable<IntPtr> FindTopLevelWindows(string className)
    {
        var windows = new List<IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (GetClassName(hwnd) == className)
            {
                windows.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static IEnumerable<IntPtr> FindDescendants(IntPtr parent, string className)
    {
        var child = IntPtr.Zero;
        while (true)
        {
            child = NativeMethods.FindWindowEx(parent, child, null, null);
            if (child == IntPtr.Zero)
            {
                yield break;
            }

            if (GetClassName(child) == className)
            {
                yield return child;
            }

            foreach (var descendant in FindDescendants(child, className))
            {
                yield return descendant;
            }
        }
    }

    private static IntPtr FindDescendant(IntPtr parent, string className)
    {
        if (parent == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return FindDescendants(parent, className).FirstOrDefault();
    }

    private static IReadOnlyList<TrayIconItem> ReadToolbar(IntPtr toolbar)
    {
        NativeMethods.GetWindowThreadProcessId(toolbar, out var processId);
        if (processId == 0)
        {
            DebugLogger.WriteIfChanged($"tray-process-{toolbar}", $"Tray toolbar 0x{toolbar.ToInt64():X}: process id is 0.");
            return Array.Empty<TrayIconItem>();
        }

        var process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION |
            NativeMethods.PROCESS_VM_OPERATION |
            NativeMethods.PROCESS_VM_READ |
            NativeMethods.PROCESS_VM_WRITE,
            false,
            processId);

        if (process == IntPtr.Zero)
        {
            DebugLogger.WriteIfChanged($"tray-openprocess-{toolbar}", $"Tray toolbar 0x{toolbar.ToInt64():X}: OpenProcess failed. ProcessId={processId} LastError={Marshal.GetLastWin32Error()}");
            return Array.Empty<TrayIconItem>();
        }

        var remoteBuffer = IntPtr.Zero;
        try
        {
            remoteBuffer = NativeMethods.VirtualAllocEx(
                process,
                IntPtr.Zero,
                RemoteBufferBytes,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);

            if (remoteBuffer == IntPtr.Zero)
            {
                DebugLogger.WriteIfChanged($"tray-alloc-{toolbar}", $"Tray toolbar 0x{toolbar.ToInt64():X}: VirtualAllocEx failed. ProcessId={processId} LastError={Marshal.GetLastWin32Error()}");
                return Array.Empty<TrayIconItem>();
            }

            return ReadToolbarButtons(toolbar, process, remoteBuffer);
        }
        finally
        {
            if (remoteBuffer != IntPtr.Zero)
            {
                NativeMethods.VirtualFreeEx(process, remoteBuffer, 0, NativeMethods.MEM_RELEASE);
            }

            NativeMethods.CloseHandle(process);
        }
    }

    private static IReadOnlyList<TrayIconItem> ReadToolbarButtons(IntPtr toolbar, IntPtr process, IntPtr remoteBuffer)
    {
        var count = NativeMethods.SendMessage(toolbar, NativeMethods.TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
        var imageList = NativeMethods.SendMessage(toolbar, NativeMethods.TB_GETIMAGELIST, IntPtr.Zero, IntPtr.Zero);
        var icons = new List<TrayIconItem>();
        DebugLogger.WriteIfChanged($"tray-read-{toolbar}", $"Tray toolbar read: Hwnd=0x{toolbar.ToInt64():X} ButtonCount={count} ImageList=0x{imageList.ToInt64():X}");

        if (!NativeMethods.GetWindowRect(toolbar, out var toolbarRect))
        {
            DebugLogger.WriteIfChanged($"tray-rect-{toolbar}", $"Tray toolbar 0x{toolbar.ToInt64():X}: GetWindowRect failed. LastError={Marshal.GetLastWin32Error()}");
            return icons;
        }

        for (var index = 0; index < count; index++)
        {
            if (!TryReadButton(toolbar, process, remoteBuffer, index, out var button))
            {
                DebugLogger.WriteIfChanged($"tray-button-{toolbar}-{index}", $"Tray toolbar 0x{toolbar.ToInt64():X}: TB_GETBUTTON/ReadProcessMemory failed for index {index}. LastError={Marshal.GetLastWin32Error()}");
                continue;
            }

            var screenRect = TryReadItemRect(toolbar, process, remoteBuffer, index, toolbarRect);
            if (screenRect is null)
            {
                DebugLogger.WriteIfChanged($"tray-itemrect-{toolbar}-{index}", $"Tray toolbar 0x{toolbar.ToInt64():X}: TB_GETITEMRECT/ReadProcessMemory failed for index {index}. CommandId={button.idCommand} Bitmap={button.iBitmap} LastError={Marshal.GetLastWin32Error()}");
                continue;
            }

            icons.Add(new TrayIconItem(
                toolbar,
                new TrayIconBounds(screenRect.Value.Left, screenRect.Value.Top, screenRect.Value.Right, screenRect.Value.Bottom),
                GetToolbarIcon(imageList, button.iBitmap),
                ReadButtonText(toolbar, process, remoteBuffer, button.idCommand)));
        }

        return icons;
    }

    private static bool TryReadButton(IntPtr toolbar, IntPtr process, IntPtr remoteBuffer, int index, out NativeMethods.TbButton button)
    {
        button = default;
        NativeMethods.SendMessage(toolbar, NativeMethods.TB_GETBUTTON, new IntPtr(index), remoteBuffer);
        var bytes = new byte[Marshal.SizeOf<NativeMethods.TbButton>()];
        if (!NativeMethods.ReadProcessMemory(process, remoteBuffer, bytes, (nuint)bytes.Length, out _))
        {
            return false;
        }

        button = BytesToStructure<NativeMethods.TbButton>(bytes);
        return true;
    }

    private static NativeMethods.Rect? TryReadItemRect(
        IntPtr toolbar,
        IntPtr process,
        IntPtr remoteBuffer,
        int index,
        NativeMethods.Rect toolbarRect)
    {
        NativeMethods.SendMessage(toolbar, NativeMethods.TB_GETITEMRECT, new IntPtr(index), remoteBuffer);
        var bytes = new byte[Marshal.SizeOf<NativeMethods.Rect>()];
        if (!NativeMethods.ReadProcessMemory(process, remoteBuffer, bytes, (nuint)bytes.Length, out _))
        {
            return null;
        }

        var itemRect = BytesToStructure<NativeMethods.Rect>(bytes);
        return new NativeMethods.Rect
        {
            Left = toolbarRect.Left + itemRect.Left,
            Top = toolbarRect.Top + itemRect.Top,
            Right = toolbarRect.Left + itemRect.Right,
            Bottom = toolbarRect.Top + itemRect.Bottom
        };
    }

    private static string ReadButtonText(IntPtr toolbar, IntPtr process, IntPtr remoteBuffer, int commandId)
    {
        var length = NativeMethods.SendMessage(toolbar, NativeMethods.TB_GETBUTTONTEXTW, new IntPtr(commandId), remoteBuffer).ToInt32();
        if (length <= 0)
        {
            return "";
        }

        var bytes = new byte[Math.Min(RemoteBufferBytes, (length + 1) * 2)];
        if (!NativeMethods.ReadProcessMemory(process, remoteBuffer, bytes, (nuint)bytes.Length, out _))
        {
            return "";
        }

        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static ImageSource? GetToolbarIcon(IntPtr imageList, int imageIndex)
    {
        if (imageList == IntPtr.Zero || imageIndex < 0)
        {
            DebugLogger.WriteIfChanged($"tray-icon-invalid-{imageIndex}", $"Tray icon skipped: ImageList=0x{imageList.ToInt64():X} ImageIndex={imageIndex}");
            return null;
        }

        var iconHandle = NativeMethods.ImageList_GetIcon(imageList, imageIndex, NativeMethods.ILD_TRANSPARENT);
        if (iconHandle == IntPtr.Zero)
        {
            DebugLogger.WriteIfChanged($"tray-icon-get-{imageList}-{imageIndex}", $"ImageList_GetIcon failed: ImageList=0x{imageList.ToInt64():X} ImageIndex={imageIndex} LastError={Marshal.GetLastWin32Error()}");
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(iconHandle, Int32Rect.Empty, null);
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DestroyIcon(iconHandle);
        }
    }

    private static T BytesToStructure<T>(byte[] bytes) where T : struct
    {
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var className = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        return className.ToString();
    }

    private static string BuildWindowTreeSummary(IEnumerable<IntPtr> roots)
    {
        var builder = new StringBuilder();
        builder.Append("Tray window tree:");
        foreach (var root in roots)
        {
            AppendWindowTree(builder, root, 0, maxDepth: 5, maxNodes: 120);
        }

        return builder.ToString();
    }

    private static int AppendWindowTree(StringBuilder builder, IntPtr hwnd, int depth, int maxDepth, int maxNodes, int count = 0)
    {
        if (hwnd == IntPtr.Zero || depth > maxDepth || count >= maxNodes)
        {
            return count;
        }

        builder.Append(" | ");
        builder.Append(new string('>', depth));
        builder.Append("0x");
        builder.Append(hwnd.ToInt64().ToString("X"));
        builder.Append(':');
        builder.Append(GetClassName(hwnd));
        count++;

        var child = IntPtr.Zero;
        while (count < maxNodes)
        {
            child = NativeMethods.FindWindowEx(hwnd, child, null, null);
            if (child == IntPtr.Zero)
            {
                break;
            }

            count = AppendWindowTree(builder, child, depth + 1, maxDepth, maxNodes, count);
        }

        return count;
    }
}
