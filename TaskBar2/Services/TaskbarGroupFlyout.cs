using System.Drawing;
using System.Windows.Forms;
using TaskBar2.Models;
using TaskBar2.Native;
using FormsControl = System.Windows.Forms.Control;
using FormsPadding = System.Windows.Forms.Padding;
using FormsTimer = System.Windows.Forms.Timer;

namespace TaskBar2.Services;

internal sealed class TaskbarGroupFlyout : ToolStripDropDown
{
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(350);
    private readonly PreviewControl _previewControl;
    private readonly ToolStripControlHost _host;
    private readonly FormsTimer _autoCloseTimer;
    private Rectangle _keepOpenBounds;
    private DateTime? _leaveStartedUtc;

    public TaskbarGroupFlyout(IReadOnlyList<TaskbarItem> items, Action<TaskbarItem> activate)
    {
        AutoClose = false;
        Padding = FormsPadding.Empty;
        Margin = FormsPadding.Empty;
        BackColor = Color.FromArgb(31, 31, 31);

        _previewControl = new PreviewControl(items, item =>
        {
            Close(ToolStripDropDownCloseReason.ItemClicked);
            activate(item);
        });
        _host = new ToolStripControlHost(_previewControl)
        {
            AutoSize = false,
            Margin = FormsPadding.Empty,
            Padding = FormsPadding.Empty,
            Size = _previewControl.Size
        };
        Items.Add(_host);

        _autoCloseTimer = new FormsTimer
        {
            Interval = 75
        };
        _autoCloseTimer.Tick += (_, _) => CloseIfPointerLeft();
    }

    public string GroupKey => _previewControl.GroupKey;

    public void ShowNear(Rectangle buttonBounds, Rectangle workingArea)
    {
        var width = _host.Width;
        var height = _host.Height;
        var x = buttonBounds.Left + (buttonBounds.Width - width) / 2;
        var y = buttonBounds.Top - height - 8;

        x = Math.Max(workingArea.Left + 4, Math.Min(x, workingArea.Right - width - 4));
        if (y < workingArea.Top)
        {
            y = buttonBounds.Bottom + 8;
        }

        Show(new Point(x, y));
        _previewControl.SetThumbnailDestination(Handle);
        _keepOpenBounds = Rectangle.Union(Bounds, buttonBounds);
        _keepOpenBounds.Inflate(12, 12);
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
            _previewControl.ClearThumbnails();
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

        if (!_keepOpenBounds.Contains(FormsControl.MousePosition))
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

    private sealed class PreviewControl : FormsControl
    {
        private const int ItemWidth = 224;
        private const int ItemHeight = 166;
        private const int PreviewHeight = 118;
        private const int PaddingSize = 10;
        private const int Gap = 8;
        private const int MaxColumns = 4;
        private readonly List<PreviewItem> _items;
        private readonly Action<TaskbarItem> _activate;
        private readonly FormsTimer _thumbnailRefreshTimer;
        private IntPtr _thumbnailDestination;
        private int _hoverIndex = -1;

        public PreviewControl(IReadOnlyList<TaskbarItem> items, Action<TaskbarItem> activate)
        {
            _items = items.Select(item => new PreviewItem(item)).ToList();
            _activate = activate;
            GroupKey = _items.FirstOrDefault()?.Item.GroupKey ?? "";

            var columns = Math.Max(1, Math.Min(MaxColumns, _items.Count));
            var rows = (int)Math.Ceiling(_items.Count / (double)columns);
            Size = new Size(
                PaddingSize * 2 + columns * ItemWidth + (columns - 1) * Gap,
                PaddingSize * 2 + rows * ItemHeight + (rows - 1) * Gap);

            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);

            _thumbnailRefreshTimer = new FormsTimer
            {
                Interval = 500
            };
            _thumbnailRefreshTimer.Tick += (_, _) => UpdateThumbnails();
        }

        public string GroupKey { get; }

        public void SetThumbnailDestination(IntPtr destination)
        {
            if (_thumbnailDestination == destination && destination != IntPtr.Zero)
            {
                return;
            }

            ClearThumbnails();
            _thumbnailDestination = destination;
            RegisterThumbnails();
            UpdateThumbnails();
            _thumbnailRefreshTimer.Start();
        }

        public void ClearThumbnails()
        {
            _thumbnailRefreshTimer.Stop();
            foreach (var item in _items)
            {
                if (item.Thumbnail != IntPtr.Zero)
                {
                    NativeMethods.DwmUnregisterThumbnail(item.Thumbnail);
                    item.Thumbnail = IntPtr.Zero;
                }

                if (item.IconHandle != IntPtr.Zero)
                {
                    NativeMethods.DestroyIcon(item.IconHandle);
                    item.IconHandle = IntPtr.Zero;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(Color.FromArgb(31, 31, 31));
            LayoutItems();

            for (var index = 0; index < _items.Count; index++)
            {
                var item = _items[index];
                DrawItem(e.Graphics, item, index == _hoverIndex);
            }

            UpdateThumbnails();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hoverIndex = _items.FindIndex(item => item.Bounds.Contains(e.Location));
            if (hoverIndex == _hoverIndex)
            {
                return;
            }

            _hoverIndex = hoverIndex;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            var item = _items.FirstOrDefault(preview => preview.Bounds.Contains(e.Location));
            if (item is not null)
            {
                _activate(item.Item);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ClearThumbnails();
                _thumbnailRefreshTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        private void LayoutItems()
        {
            var columns = Math.Max(1, Math.Min(MaxColumns, _items.Count));
            for (var index = 0; index < _items.Count; index++)
            {
                var row = index / columns;
                var column = index % columns;
                var left = PaddingSize + column * (ItemWidth + Gap);
                var top = PaddingSize + row * (ItemHeight + Gap);
                var bounds = new Rectangle(left, top, ItemWidth, ItemHeight);
                _items[index].Bounds = bounds;
                _items[index].PreviewBounds = new Rectangle(
                    bounds.Left + 8,
                    bounds.Top + 8,
                    bounds.Width - 16,
                    PreviewHeight);
            }
        }

        private void RegisterThumbnails()
        {
            if (_thumbnailDestination == IntPtr.Zero)
            {
                return;
            }

            foreach (var item in _items)
            {
                if (NativeMethods.DwmRegisterThumbnail(_thumbnailDestination, item.Item.Hwnd, out var thumbnail) == 0)
                {
                    item.Thumbnail = thumbnail;
                    if (NativeMethods.DwmQueryThumbnailSourceSize(thumbnail, out var size) == 0)
                    {
                        item.SourceSize = size;
                    }
                }

                item.IconHandle = WindowIconProvider.GetIconHandleCopy(
                    item.Item.Hwnd,
                    item.Item.ProcessPath,
                    item.Item.IconPath,
                    item.Item.IconIndex);
            }
        }

        private void UpdateThumbnails()
        {
            if (_thumbnailDestination == IntPtr.Zero || !Visible)
            {
                return;
            }

            foreach (var item in _items)
            {
                if (item.Thumbnail == IntPtr.Zero)
                {
                    continue;
                }

                var destination = Fit(item.SourceSize, item.PreviewBounds);
                var properties = new NativeMethods.DwmThumbnailProperties
                {
                    dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION |
                              NativeMethods.DWM_TNP_VISIBLE |
                              NativeMethods.DWM_TNP_OPACITY |
                              NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY,
                    rcDestination = new NativeMethods.Rect
                    {
                        Left = destination.Left,
                        Top = destination.Top,
                        Right = destination.Right,
                        Bottom = destination.Bottom
                    },
                    opacity = 255,
                    fVisible = true,
                    fSourceClientAreaOnly = false
                };
                NativeMethods.DwmUpdateThumbnailProperties(item.Thumbnail, ref properties);
            }
        }

        private static Rectangle Fit(NativeMethods.Size sourceSize, Rectangle target)
        {
            if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
            {
                return target;
            }

            var scale = Math.Min(
                target.Width / (double)sourceSize.Width,
                target.Height / (double)sourceSize.Height);
            var width = Math.Max(1, (int)Math.Round(sourceSize.Width * scale));
            var height = Math.Max(1, (int)Math.Round(sourceSize.Height * scale));
            return new Rectangle(
                target.Left + (target.Width - width) / 2,
                target.Top + (target.Height - height) / 2,
                width,
                height);
        }

        private static void DrawItem(Graphics graphics, PreviewItem item, bool hovered)
        {
            using var background = new SolidBrush(hovered
                ? Color.FromArgb(58, 58, 58)
                : Color.FromArgb(42, 42, 42));
            using var border = new Pen(Color.FromArgb(72, 72, 72));
            graphics.FillRectangle(background, item.Bounds);
            graphics.DrawRectangle(border, item.Bounds);

            if (item.Thumbnail == IntPtr.Zero && item.IconHandle != IntPtr.Zero)
            {
                using var icon = Icon.FromHandle(item.IconHandle);
                var iconSize = Math.Min(48, Math.Min(item.PreviewBounds.Width, item.PreviewBounds.Height));
                var iconBounds = new Rectangle(
                    item.PreviewBounds.Left + (item.PreviewBounds.Width - iconSize) / 2,
                    item.PreviewBounds.Top + (item.PreviewBounds.Height - iconSize) / 2,
                    iconSize,
                    iconSize);
                graphics.DrawIcon(icon, iconBounds);
            }

            var titleBounds = new Rectangle(
                item.Bounds.Left + 8,
                item.Bounds.Top + PreviewHeight + 14,
                item.Bounds.Width - 16,
                item.Bounds.Height - PreviewHeight - 18);
            TextRenderer.DrawText(
                graphics,
                item.Item.Title,
                SystemFonts.MessageBoxFont,
                titleBounds,
                Color.WhiteSmoke,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }

        private sealed class PreviewItem(TaskbarItem item)
        {
            public TaskbarItem Item { get; } = item;

            public Rectangle Bounds { get; set; }

            public Rectangle PreviewBounds { get; set; }

            public IntPtr Thumbnail { get; set; }

            public NativeMethods.Size SourceSize { get; set; }

            public IntPtr IconHandle { get; set; }
        }
    }
}
