using System.Drawing;
using System.Windows.Forms;
using FormsPadding = System.Windows.Forms.Padding;
using FormsTimer = System.Windows.Forms.Timer;

namespace TaskBar2.Services;

internal sealed class TaskbarHoverLabel : ToolStripDropDown
{
    private const int MaxTitleLength = 72;
    private const int MaxLines = 6;
    private const int HorizontalPadding = 12;
    private const int VerticalPadding = 7;
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(350);
    private readonly FormsTimer _autoCloseTimer;
    private Rectangle _keepOpenBounds;
    private DateTime? _leaveStartedUtc;

    public TaskbarHoverLabel(string groupKey, string title, bool lightTheme)
    {
        GroupKey = groupKey;
        AutoClose = false;
        Padding = FormsPadding.Empty;
        Margin = FormsPadding.Empty;

        var displayTitle = ClipTitle(string.IsNullOrWhiteSpace(title) ? "App" : title.Trim());
        var isMultiline = displayTitle.Contains('\n', StringComparison.Ordinal);
        var background = lightTheme
            ? Color.FromArgb(248, 248, 248)
            : Color.FromArgb(31, 31, 31);
        var foreground = lightTheme
            ? Color.FromArgb(32, 32, 32)
            : Color.White;

        BackColor = background;
        ForeColor = foreground;

        var textSize = TextRenderer.MeasureText(
            displayTitle,
            SystemFonts.MenuFont,
            new Size(460, 0),
            TextFormatFlags.NoPrefix | (isMultiline ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine));

        var label = new ToolStripLabel(displayTitle)
        {
            AutoSize = false,
            BackColor = background,
            ForeColor = foreground,
            Margin = FormsPadding.Empty,
            Padding = FormsPadding.Empty,
            Size = new Size(textSize.Width + HorizontalPadding * 2, textSize.Height + VerticalPadding * 2),
            TextAlign = isMultiline ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleCenter,
            ToolTipText = title
        };

        Items.Add(label);

        _autoCloseTimer = new FormsTimer
        {
            Interval = 125
        };
        _autoCloseTimer.Tick += (_, _) => CloseIfPointerLeft();
    }

    public string GroupKey { get; }

    public void ShowNear(Rectangle buttonBounds, Rectangle workingArea)
    {
        var width = Items[0].Width;
        var height = Items[0].Height;
        var x = buttonBounds.Left + (buttonBounds.Width - width) / 2;
        var y = buttonBounds.Top - height - 8;

        x = Math.Max(workingArea.Left + 4, Math.Min(x, workingArea.Right - width - 4));
        if (y < workingArea.Top)
        {
            y = buttonBounds.Bottom + 8;
        }

        Show(new Point(x, y));
        _keepOpenBounds = Rectangle.Union(Bounds, buttonBounds);
        _keepOpenBounds.Inflate(8, 8);
        _autoCloseTimer.Start();
    }

    protected override void OnClosed(ToolStripDropDownClosedEventArgs e)
    {
        _autoCloseTimer.Stop();
        base.OnClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoCloseTimer.Stop();
            _autoCloseTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void CloseIfPointerLeft()
    {
        if (!Visible)
        {
            _autoCloseTimer.Stop();
            return;
        }

        if (!_keepOpenBounds.Contains(Control.MousePosition))
        {
            var now = DateTime.UtcNow;
            _leaveStartedUtc ??= now;
            if (now - _leaveStartedUtc.Value >= CloseDelay)
            {
                Close(ToolStripDropDownCloseReason.CloseCalled);
            }

            return;
        }

        _leaveStartedUtc = null;
    }

    private static string ClipTitle(string title)
    {
        var lines = title
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(MaxLines)
            .Select(ClipLine)
            .ToArray();

        return lines.Length == 0 ? "App" : string.Join("\n", lines);
    }

    private static string ClipLine(string line)
    {
        if (line.Length <= MaxTitleLength)
        {
            return line;
        }

        return line[..(MaxTitleLength - 3)] + "...";
    }
}
