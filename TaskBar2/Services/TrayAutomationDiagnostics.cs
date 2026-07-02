using System.Text;
using System.Windows.Automation;

namespace TaskBar2.Services;

internal static class TrayAutomationDiagnostics
{
    public static string BuildSummary(IntPtr shellTray)
    {
        if (shellTray == IntPtr.Zero)
        {
            return "Tray UIA tree: Shell_TrayWnd not found.";
        }

        try
        {
            var root = AutomationElement.FromHandle(shellTray);
            if (root is null)
            {
                return $"Tray UIA tree: AutomationElement.FromHandle failed for 0x{shellTray.ToInt64():X}.";
            }

            var builder = new StringBuilder();
            builder.Append("Tray UIA tree:");
            AppendElement(builder, root, 0, maxDepth: 7, maxNodes: 160);
            return builder.ToString();
        }
        catch (Exception exception)
        {
            return $"Tray UIA tree failed: {exception.GetType().Name}: {exception.Message}";
        }
    }

    private static int AppendElement(StringBuilder builder, AutomationElement element, int depth, int maxDepth, int maxNodes, int count = 0)
    {
        if (depth > maxDepth || count >= maxNodes)
        {
            return count;
        }

        var current = element.Current;
        builder.Append(" | ");
        builder.Append(new string('>', depth));
        builder.Append(current.ControlType.ProgrammaticName.Replace("ControlType.", ""));
        builder.Append(":\"");
        builder.Append((current.Name ?? "").Replace("|", "/"));
        builder.Append("\" Class=\"");
        builder.Append(current.ClassName ?? "");
        builder.Append("\" Rect=");
        builder.Append(current.BoundingRectangle);
        count++;

        var walker = TreeWalker.RawViewWalker;
        var child = walker.GetFirstChild(element);
        while (child is not null && count < maxNodes)
        {
            count = AppendElement(builder, child, depth + 1, maxDepth, maxNodes, count);
            child = walker.GetNextSibling(child);
        }

        return count;
    }
}
