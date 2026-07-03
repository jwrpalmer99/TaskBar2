using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskBar2.Models;
using FormsControl = System.Windows.Forms.Control;
using FormsPadding = System.Windows.Forms.Padding;
using DrawingColor = System.Drawing.Color;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using WpfPixelFormats = System.Windows.Media.PixelFormats;

namespace TaskBar2.Services;

internal sealed class TrayOverflowFlyout : ToolStripDropDown
{
    private readonly OverflowControl _control;
    private readonly ToolStripControlHost _host;

    public TrayOverflowFlyout(
        IReadOnlyList<TrayIconItem> items,
        double scale,
        bool lightTheme,
        Action<TrayIconItem, bool> click,
        Action<TrayIconItem> doubleClick)
    {
        AutoClose = true;
        Padding = FormsPadding.Empty;
        Margin = FormsPadding.Empty;
        BackColor = lightTheme
            ? DrawingColor.FromArgb(248, 248, 248)
            : DrawingColor.FromArgb(31, 31, 31);

        _control = new OverflowControl(
            items,
            scale,
            lightTheme,
            item =>
            {
                Close(ToolStripDropDownCloseReason.ItemClicked);
                click(item, false);
            },
            item =>
            {
                Close(ToolStripDropDownCloseReason.ItemClicked);
                click(item, true);
            },
            item =>
            {
                Close(ToolStripDropDownCloseReason.ItemClicked);
                doubleClick(item);
            });
        _host = new ToolStripControlHost(_control)
        {
            AutoSize = false,
            Margin = FormsPadding.Empty,
            Padding = FormsPadding.Empty,
            Size = _control.Size
        };
        _control.ItemsChanged += (_, _) =>
        {
            _host.Size = _control.Size;
            Size = _host.Size;
        };
        _control.ItemsEmpty += (_, _) => Close(ToolStripDropDownCloseReason.CloseCalled);
        Items.Add(_host);
    }

    public void UpdateItems(IReadOnlyList<TrayIconItem> items)
    {
        _control.UpdateItems(items);
    }

    public void ShowNear(Rectangle buttonScreenBounds, Rectangle workingArea)
    {
        var width = _host.Width;
        var height = _host.Height;
        var x = buttonScreenBounds.Left + (buttonScreenBounds.Width - width) / 2;
        var y = buttonScreenBounds.Top - height - 8;

        x = Math.Max(workingArea.Left + 4, Math.Min(x, workingArea.Right - width - 4));
        if (y < workingArea.Top)
        {
            y = buttonScreenBounds.Bottom + 8;
        }

        Show(new Point(x, y));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _control.DisposeItems();
        }

        base.Dispose(disposing);
    }

    private sealed class OverflowControl : FormsControl
    {
        private const int MaxColumns = 5;
        private readonly double _scale;
        private readonly bool _lightTheme;
        private readonly Action<TrayIconItem> _leftClick;
        private readonly Action<TrayIconItem> _rightClick;
        private readonly Action<TrayIconItem> _doubleClick;
        private readonly ToolTip _toolTip = new();
        private List<OverflowItem> _items = [];
        private int _hoverIndex = -1;

        public OverflowControl(
            IReadOnlyList<TrayIconItem> items,
            double scale,
            bool lightTheme,
            Action<TrayIconItem> leftClick,
            Action<TrayIconItem> rightClick,
            Action<TrayIconItem> doubleClick)
        {
            _scale = scale;
            _lightTheme = lightTheme;
            _leftClick = leftClick;
            _rightClick = rightClick;
            _doubleClick = doubleClick;

            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);

            UpdateItems(items);
        }

        public event EventHandler? ItemsChanged;

        public event EventHandler? ItemsEmpty;

        public void UpdateItems(IReadOnlyList<TrayIconItem> items)
        {
            DisposeItems();
            _items = items.Select(item => new OverflowItem(item, CreateBitmap(item.Icon))).ToList();
            _hoverIndex = -1;
            UpdateControlSize();

            if (_items.Count == 0)
            {
                ItemsEmpty?.Invoke(this, EventArgs.Empty);
                return;
            }

            ItemsChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }

        public void DisposeItems()
        {
            foreach (var item in _items)
            {
                item.Bitmap?.Dispose();
            }

            _items.Clear();
            _toolTip.SetToolTip(this, null);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeItems();
                _toolTip.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            ConfigureHighQualityGraphics(e.Graphics);
            e.Graphics.Clear(GetBackgroundColor());
            LayoutItems();

            for (var index = 0; index < _items.Count; index++)
            {
                DrawItem(e.Graphics, _items[index], index == _hoverIndex);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            LayoutItems();
            var hoverIndex = _items.FindIndex(item => item.Bounds.Contains(e.Location));
            if (hoverIndex == _hoverIndex)
            {
                return;
            }

            _hoverIndex = hoverIndex;
            Cursor = hoverIndex >= 0 ? Cursors.Hand : Cursors.Default;
            _toolTip.SetToolTip(this, hoverIndex >= 0 ? _items[hoverIndex].Item.ToolTip : null);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex == -1)
            {
                return;
            }

            _hoverIndex = -1;
            Cursor = Cursors.Default;
            _toolTip.SetToolTip(this, null);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Clicks > 1)
            {
                return;
            }

            LayoutItems();
            var item = _items.FirstOrDefault(item => item.Bounds.Contains(e.Location));
            if (item is null)
            {
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                _leftClick(item.Item);
            }
            else if (e.Button == MouseButtons.Right)
            {
                _rightClick(item.Item);
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            LayoutItems();
            var item = _items.FirstOrDefault(item => item.Bounds.Contains(e.Location));
            if (item is not null)
            {
                _doubleClick(item.Item);
            }
        }

        private void UpdateControlSize()
        {
            var columns = Math.Max(1, Math.Min(MaxColumns, _items.Count));
            var rows = Math.Max(1, (int)Math.Ceiling(_items.Count / (double)columns));
            var padding = GetPadding();
            var buttonSize = GetButtonSize();
            var gap = GetGap();
            Size = new Size(
                padding * 2 + columns * buttonSize + (columns - 1) * gap,
                padding * 2 + rows * buttonSize + (rows - 1) * gap);
        }

        private void LayoutItems()
        {
            var columns = Math.Max(1, Math.Min(MaxColumns, _items.Count));
            var padding = GetPadding();
            var buttonSize = GetButtonSize();
            var gap = GetGap();
            for (var index = 0; index < _items.Count; index++)
            {
                var row = index / columns;
                var column = index % columns;
                _items[index].Bounds = new Rectangle(
                    padding + column * (buttonSize + gap),
                    padding + row * (buttonSize + gap),
                    buttonSize,
                    buttonSize);
            }
        }

        private void DrawItem(Graphics graphics, OverflowItem item, bool hovered)
        {
            if (hovered)
            {
                using var hoverBrush = new SolidBrush(_lightTheme
                    ? DrawingColor.FromArgb(228, 228, 228)
                    : DrawingColor.FromArgb(58, 58, 58));
                using var path = RoundedRectangle(item.Bounds, Math.Max(4, (int)Math.Round(4 * _scale)));
                graphics.FillPath(hoverBrush, path);
            }

            var iconSize = GetIconSize();
            var iconBounds = new Rectangle(
                item.Bounds.Left + (item.Bounds.Width - iconSize) / 2,
                item.Bounds.Top + (item.Bounds.Height - iconSize) / 2,
                iconSize,
                iconSize);
            if (item.Bitmap is not null)
            {
                graphics.DrawImage(item.Bitmap, iconBounds);
                return;
            }

            TextRenderer.DrawText(
                graphics,
                item.Item.Glyph,
                SystemFonts.MessageBoxFont,
                item.Bounds,
                _lightTheme ? DrawingColor.FromArgb(32, 32, 32) : DrawingColor.WhiteSmoke,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private DrawingColor GetBackgroundColor() =>
            _lightTheme
                ? DrawingColor.FromArgb(248, 248, 248)
                : DrawingColor.FromArgb(31, 31, 31);

        private int GetPadding() => Math.Max(8, (int)Math.Round(10 * _scale));

        private int GetGap() => Math.Max(4, (int)Math.Round(5 * _scale));

        private int GetButtonSize() => Math.Max(30, (int)Math.Round(34 * _scale));

        private int GetIconSize() => Math.Max(16, (int)Math.Round(18 * _scale));

        private static Bitmap? CreateBitmap(ImageSource? source)
        {
            if (source is not BitmapSource bitmapSource ||
                bitmapSource.PixelWidth <= 0 ||
                bitmapSource.PixelHeight <= 0)
            {
                return null;
            }

            try
            {
                BitmapSource converted = bitmapSource;
                if (converted.Format != WpfPixelFormats.Pbgra32)
                {
                    converted = new FormatConvertedBitmap(bitmapSource, WpfPixelFormats.Pbgra32, null, 0);
                }

                var width = converted.PixelWidth;
                var height = converted.PixelHeight;
                var stride = checked(width * 4);
                var pixels = new byte[checked(stride * height)];
                converted.CopyPixels(pixels, stride, 0);

                var bitmap = new Bitmap(width, height, DrawingPixelFormat.Format32bppPArgb);
                var data = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    DrawingPixelFormat.Format32bppPArgb);
                try
                {
                    Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                return bitmap;
            }
            catch (Exception exception) when (exception is NotSupportedException or ExternalException or ArgumentException)
            {
                DebugLogger.WriteIfChanged(
                    "tray-overflow-icon-convert-error",
                    $"Tray overflow icon conversion failed: {exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        private static void ConfigureHighQualityGraphics(Graphics graphics)
        {
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class OverflowItem(TrayIconItem item, Bitmap? bitmap)
        {
            public TrayIconItem Item { get; } = item;

            public Bitmap? Bitmap { get; } = bitmap;

            public Rectangle Bounds { get; set; }
        }
    }
}
