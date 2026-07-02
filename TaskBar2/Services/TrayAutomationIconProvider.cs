using System.Drawing;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskBar2.Models;
using TaskBar2.Native;

namespace TaskBar2.Services;

internal static class TrayAutomationIconProvider
{
    private static readonly string[] TrayButtonClassPrefixes =
    [
        "SystemTray."
    ];

    public static IReadOnlyList<TrayIconItem> GetIcons(IntPtr shellTray)
    {
        if (shellTray == IntPtr.Zero)
        {
            return Array.Empty<TrayIconItem>();
        }

        try
        {
            var root = AutomationElement.FromHandle(shellTray);
            if (root is null)
            {
                return Array.Empty<TrayIconItem>();
            }

            var items = new List<TrayIconItem>();
            CollectTrayButtons(root, items, depth: 0, maxDepth: 9);
            DebugLogger.WriteIfChanged("tray-uia-icons", $"Tray UIA icons: Count={items.Count}");
            return items;
        }
        catch (Exception exception)
        {
            DebugLogger.WriteIfChanged("tray-uia-icons-error", $"Tray UIA icon read failed: {exception.GetType().Name}: {exception.Message}");
            return Array.Empty<TrayIconItem>();
        }
    }

    private static void CollectTrayButtons(AutomationElement element, List<TrayIconItem> items, int depth, int maxDepth)
    {
        if (depth > maxDepth)
        {
            return;
        }

        var current = element.Current;
        if (current.ControlType == ControlType.Button &&
            TrayButtonClassPrefixes.Any(prefix => (current.ClassName ?? "").StartsWith(prefix, StringComparison.Ordinal)) &&
            !string.Equals(current.ClassName, "SystemTray.ShowDesktopButton", StringComparison.Ordinal))
        {
            var rect = current.BoundingRectangle;
            if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
            {
                var name = current.Name ?? "";
                if (!name.StartsWith("Clock ", StringComparison.OrdinalIgnoreCase))
                {
                    var iconRect = FindIconRect(element, rect);
                    var icon = CaptureScreenRect(iconRect);
                    if (icon is not null)
                    {
                        items.Add(new TrayIconItem(
                            IntPtr.Zero,
                            new TrayIconBounds(
                                (int)Math.Round(rect.Left),
                                (int)Math.Round(rect.Top),
                                (int)Math.Round(rect.Right),
                                (int)Math.Round(rect.Bottom)),
                            icon,
                            name));
                    }
                }
            }
        }

        var walker = TreeWalker.RawViewWalker;
        var child = walker.GetFirstChild(element);
        while (child is not null)
        {
            CollectTrayButtons(child, items, depth + 1, maxDepth);
            child = walker.GetNextSibling(child);
        }
    }

    private static System.Windows.Rect FindIconRect(AutomationElement button, System.Windows.Rect buttonRect)
    {
        var walker = TreeWalker.RawViewWalker;
        var child = walker.GetFirstChild(button);
        while (child is not null)
        {
            var current = child.Current;
            if ((current.ControlType == ControlType.Image || current.ControlType == ControlType.Text) &&
                !current.BoundingRectangle.IsEmpty &&
                current.BoundingRectangle.Width > 0 &&
                current.BoundingRectangle.Height > 0)
            {
                return current.BoundingRectangle;
            }

            var nested = FindIconRect(child, System.Windows.Rect.Empty);
            if (!nested.IsEmpty)
            {
                return nested;
            }

            child = walker.GetNextSibling(child);
        }

        if (buttonRect.IsEmpty)
        {
            return System.Windows.Rect.Empty;
        }

        const double fallbackSize = 24;
        return new System.Windows.Rect(
            buttonRect.Left + Math.Max(0, (buttonRect.Width - fallbackSize) / 2),
            buttonRect.Top + Math.Max(0, (buttonRect.Height - fallbackSize) / 2),
            Math.Min(fallbackSize, buttonRect.Width),
            Math.Min(fallbackSize, buttonRect.Height));
    }

    private static ImageSource? CaptureScreenRect(System.Windows.Rect rect)
    {
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        var x = (int)Math.Round(rect.Left);
        var y = (int)Math.Round(rect.Top);
        var width = Math.Max(1, (int)Math.Round(rect.Width));
        var height = Math.Max(1, (int)Math.Round(rect.Height));

        try
        {
            using var bitmap = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
            }

            var handle = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                NativeMethods.DeleteObject(handle);
            }
        }
        catch (Exception exception)
        {
            DebugLogger.WriteIfChanged("tray-uia-capture-error", $"Tray icon pixel capture failed: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }
}
