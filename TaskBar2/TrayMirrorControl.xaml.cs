using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TaskBar2.Models;
using TaskBar2.Services;

namespace TaskBar2;

public partial class TrayMirrorControl : System.Windows.Controls.UserControl, INotifyPropertyChanged
{
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _snapshotRefreshTimer;
    private readonly NativeTrayIconProvider _trayIconProvider = new();
    private string _clockText = "";
    private double _trayButtonWidth = 28;
    private double _trayButtonHeight = 36;
    private double _trayIconSize = 16;
    private double _clockFontSize = 12;
    private string _suppressNextTrayClickKey = "";

    public TrayMirrorControl()
    {
        TrayItems = [];
        InitializeComponent();
        DataContext = this;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(AppSettingsService.Current.TrayRefreshIntervalMs)
        };
        _timer.Tick += (_, _) => RefreshTray();

        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _clockTimer.Tick += (_, _) => UpdateClock();

        _snapshotRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _snapshotRefreshTimer.Tick += (_, _) =>
        {
            _snapshotRefreshTimer.Stop();
            RefreshTray();
        };

        AppSettingsService.SettingsChanged += OnSettingsChanged;
        TrayIconSnapshotStore.SnapshotsChanged += OnTrayIconSnapshotsChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateScale();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TrayIconItem> TrayItems { get; }

    public string ClockText
    {
        get => _clockText;
        private set
        {
            if (_clockText == value)
            {
                return;
            }

            _clockText = value;
            OnPropertyChanged();
        }
    }

    public double TrayButtonWidth
    {
        get => _trayButtonWidth;
        private set => SetProperty(ref _trayButtonWidth, value);
    }

    public double TrayButtonHeight
    {
        get => _trayButtonHeight;
        private set => SetProperty(ref _trayButtonHeight, value);
    }

    public double TrayIconSize
    {
        get => _trayIconSize;
        private set => SetProperty(ref _trayIconSize, value);
    }

    public double ClockFontSize
    {
        get => _clockFontSize;
        private set => SetProperty(ref _clockFontSize, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshTray();
        UpdateClock();
        _timer.Start();
        _clockTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _clockTimer.Stop();
        _snapshotRefreshTimer.Stop();
        AppSettingsService.SettingsChanged -= OnSettingsChanged;
        TrayIconSnapshotStore.SnapshotsChanged -= OnTrayIconSnapshotsChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        FullscreenApplicationDetector.Invalidate();
        _timer.Interval = TimeSpan.FromMilliseconds(AppSettingsService.Current.TrayRefreshIntervalMs);
        UpdateScale();
        RefreshTray();
    }

    private void RefreshTray()
    {
        if (FullscreenApplicationDetector.IsFullscreenApplicationActive(out _))
        {
            return;
        }

        var latestItems = TrayIconSnapshotStore.GetItems();
        if (latestItems.Count == 0 && !AppSettingsService.Current.EnableInvasiveTrayIconHook)
        {
            latestItems = _trayIconProvider.GetIcons();
        }

        if (latestItems.Count == 0 &&
            TrayItems.Count > 0 &&
            !AppSettingsService.Current.EnableInvasiveTrayIconHook)
        {
            DebugLogger.WriteIfChanged("tray-retained", $"Tray refresh returned 0 items; retaining last known tray snapshot with {TrayItems.Count} items.");
            return;
        }

        ApplyTrayItems(latestItems);
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText = now.ToString("HH:mm");
        ScheduleNextClockTick(now);
    }

    private void ScheduleNextClockTick(DateTime now)
    {
        var nextMinute = new DateTime(
            now.Year,
            now.Month,
            now.Day,
            now.Hour,
            now.Minute,
            0,
            now.Kind).AddMinutes(1);
        var delay = nextMinute - now + TimeSpan.FromMilliseconds(50);
        _clockTimer.Interval = delay < TimeSpan.FromMilliseconds(250)
            ? TimeSpan.FromMilliseconds(250)
            : delay;
    }

    private void OnTrayIconSnapshotsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(ScheduleTrayRefresh), DispatcherPriority.Background);
            return;
        }

        ScheduleTrayRefresh();
    }

    private void ScheduleTrayRefresh()
    {
        if (!_snapshotRefreshTimer.IsEnabled)
        {
            _snapshotRefreshTimer.Start();
        }
    }

    private void ApplyTrayItems(IReadOnlyList<TrayIconItem> latestItems)
    {
        if (latestItems.Any(item => string.IsNullOrWhiteSpace(item.Identity)))
        {
            ReplaceAllTrayItems(latestItems);
            return;
        }

        for (var index = TrayItems.Count - 1; index >= 0; index--)
        {
            var existing = TrayItems[index];
            if (string.IsNullOrWhiteSpace(existing.Identity) ||
                !latestItems.Any(item => item.Identity == existing.Identity))
            {
                TrayItems.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < latestItems.Count; targetIndex++)
        {
            var latest = latestItems[targetIndex];
            var existingIndex = IndexOfTrayIdentity(latest.Identity);
            if (existingIndex < 0)
            {
                TrayItems.Insert(targetIndex, latest);
                continue;
            }

            if (existingIndex != targetIndex)
            {
                TrayItems.Move(existingIndex, targetIndex);
            }

            if (!TrayItems[targetIndex].Equals(latest))
            {
                TrayItems[targetIndex] = latest;
            }
        }
    }

    private void ReplaceAllTrayItems(IReadOnlyList<TrayIconItem> latestItems)
    {
        if (TrayItems.SequenceEqual(latestItems))
        {
            return;
        }

        TrayItems.Clear();
        foreach (var item in latestItems)
        {
            TrayItems.Add(item);
        }
    }

    private int IndexOfTrayIdentity(string identity)
    {
        for (var index = 0; index < TrayItems.Count; index++)
        {
            if (TrayItems[index].Identity == identity)
            {
                return index;
            }
        }

        return -1;
    }

    private void UpdateScale()
    {
        var scale = AppSettingsService.Current.TaskbarScale;
        TrayButtonWidth = Math.Round(28 * scale);
        TrayButtonHeight = Math.Round(36 * scale);
        TrayIconSize = Math.Round(16 * scale);
        ClockFontSize = Math.Round(12 * scale);
    }

    private void TrayIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TrayIconItem item })
        {
            var key = GetTrayClickKey(item);
            if (string.Equals(_suppressNextTrayClickKey, key, StringComparison.Ordinal))
            {
                _suppressNextTrayClickKey = "";
                return;
            }

            if (TrayIconSnapshotStore.TryForwardClick(item, rightClick: false))
            {
                return;
            }

            _trayIconProvider.ForwardClick(item, rightClick: false);
        }
    }

    private void TrayIcon_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TrayIconItem item })
        {
            if (TrayIconSnapshotStore.TryForwardClick(item, rightClick: true))
            {
                e.Handled = true;
                return;
            }

            _trayIconProvider.ForwardClick(item, rightClick: true);
            e.Handled = true;
        }
    }

    private void TrayIcon_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TrayIconItem item })
        {
            _suppressNextTrayClickKey = GetTrayClickKey(item);

            if (TrayIconSnapshotStore.TryForwardClick(item, rightClick: false, doubleClick: true))
            {
                e.Handled = true;
                return;
            }

            _trayIconProvider.ForwardClick(item, rightClick: false, doubleClick: true);
            e.Handled = true;
        }
    }

    private static string GetTrayClickKey(TrayIconItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Identity))
        {
            return item.Identity;
        }

        return $"{item.ToolbarHwnd.ToInt64():X}:{item.ScreenRect.Left}:{item.ScreenRect.Top}:{item.ToolTip}";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }
}
