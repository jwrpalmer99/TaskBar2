using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TaskBar2.Models;
using TaskBar2.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace TaskBar2;

public partial class SecondaryTaskbarWindow : Window, INotifyPropertyChanged, ISecondaryTaskbarHost
{
    private const double BaseTaskbarHeight = 40;
    private readonly Screen _screen;
    private readonly WindowTracker _windowTracker;
    private IReadOnlyList<TaskbarItem> _currentWindows = Array.Empty<TaskbarItem>();
    private AppBarHost? _appBar;
    private HorizontalAlignment _taskbarButtonPanelAlignment = HorizontalAlignment.Left;
    private double _taskbarHeight = BaseTaskbarHeight;
    private double _taskbarButtonWidth = 36;
    private double _taskbarButtonHeight = 36;
    private Thickness _taskbarButtonMargin = new(0, 2, 0, 2);
    private double _taskbarIconSize = 20;
    private double _taskbarOverlayIconSize = 11;
    private double _taskbarUnderlineWidth = 16;

    public SecondaryTaskbarWindow(Screen screen, WindowTracker windowTracker)
    {
        _screen = screen;
        _windowTracker = windowTracker;
        Items = [];

        InitializeComponent();
        DataContext = this;
        UpdateScale();

        Closed += OnClosed;
        SourceInitialized += OnSourceInitialized;
        _windowTracker.WindowsChanged += OnWindowsChanged;
        TaskbarStateSnapshotStore.StateChanged += OnTaskbarStateChanged;
        AppSettingsService.SettingsChanged += OnSettingsChanged;
        UpdateAlignment();
        UpdateItems(_windowTracker.CurrentWindows);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TaskbarButtonViewModel> Items { get; }

    public double TaskbarHeight
    {
        get => _taskbarHeight;
        private set => SetProperty(ref _taskbarHeight, value);
    }

    public double TaskbarButtonWidth
    {
        get => _taskbarButtonWidth;
        private set => SetProperty(ref _taskbarButtonWidth, value);
    }

    public double TaskbarButtonHeight
    {
        get => _taskbarButtonHeight;
        private set => SetProperty(ref _taskbarButtonHeight, value);
    }

    public Thickness TaskbarButtonMargin
    {
        get => _taskbarButtonMargin;
        private set => SetProperty(ref _taskbarButtonMargin, value);
    }

    public double TaskbarIconSize
    {
        get => _taskbarIconSize;
        private set => SetProperty(ref _taskbarIconSize, value);
    }

    public double TaskbarOverlayIconSize
    {
        get => _taskbarOverlayIconSize;
        private set => SetProperty(ref _taskbarOverlayIconSize, value);
    }

    public double TaskbarUnderlineWidth
    {
        get => _taskbarUnderlineWidth;
        private set => SetProperty(ref _taskbarUnderlineWidth, value);
    }

    public HorizontalAlignment TaskbarButtonPanelAlignment
    {
        get => _taskbarButtonPanelAlignment;
        private set
        {
            if (_taskbarButtonPanelAlignment == value)
            {
                return;
            }

            _taskbarButtonPanelAlignment = value;
            OnPropertyChanged();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _appBar = new AppBarHost(helper, _screen, GetTaskbarHeightPixels());
        _appBar.Register();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _windowTracker.WindowsChanged -= OnWindowsChanged;
        TaskbarStateSnapshotStore.StateChanged -= OnTaskbarStateChanged;
        AppSettingsService.SettingsChanged -= OnSettingsChanged;
        _appBar?.Dispose();
    }

    public void RefreshPlacement()
    {
        _appBar?.RepositionWithoutChangingReservation();
    }

    private void OnWindowsChanged(object? sender, IReadOnlyList<TaskbarItem> windows)
    {
        if (FullscreenApplicationDetector.IsFullscreenApplicationActive(out _))
        {
            _currentWindows = windows;
            return;
        }

        UpdateItems(windows);
    }

    private void OnTaskbarStateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => UpdateItems(_currentWindows)));
            return;
        }

        if (FullscreenApplicationDetector.IsFullscreenApplicationActive(out _))
        {
            return;
        }

        UpdateItems(_currentWindows);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        FullscreenApplicationDetector.Invalidate();
        UpdateScale();
        UpdateAlignment();
        UpdateItems(_currentWindows);
    }

    private void UpdateItems(IReadOnlyList<TaskbarItem> windows)
    {
        _currentWindows = windows;
        Items.Clear();
        var items = AppSettingsService.Current.ShowOnlyAppsOnThisMonitor
            ? windows.Where(item => item.MonitorDeviceName == _screen.DeviceName)
            : windows;

        foreach (var item in items)
        {
            TaskbarStateSnapshotStore.TryGetState(item.Hwnd, out var state);
            Items.Add(new TaskbarButtonViewModel(item, state, TaskbarButtonWidth));
        }
    }

    private void UpdateAlignment()
    {
        TaskbarButtonPanelAlignment = AppSettingsService.Current.TaskbarButtonAlignment == "Center"
            ? HorizontalAlignment.Center
            : HorizontalAlignment.Left;
    }

    private void UpdateScale()
    {
        var scale = AppSettingsService.Current.TaskbarScale;
        TaskbarHeight = Math.Round(BaseTaskbarHeight * scale);
        TaskbarButtonWidth = Math.Round(36 * scale);
        TaskbarButtonHeight = Math.Round(36 * scale);
        TaskbarIconSize = Math.Round(20 * scale);
        TaskbarOverlayIconSize = Math.Round(11 * scale);
        TaskbarUnderlineWidth = Math.Round(16 * scale);

        var verticalMargin = Math.Max(0, Math.Round((TaskbarHeight - TaskbarButtonHeight) / 2));
        TaskbarButtonMargin = new Thickness(0, verticalMargin, 0, verticalMargin);
        _appBar?.SetHeight(GetTaskbarHeightPixels());
    }

    private int GetTaskbarHeightPixels()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return Math.Max(32, (int)Math.Ceiling(TaskbarHeight * dpi.DpiScaleY));
    }

    private void TaskbarButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskbarButtonViewModel viewModel })
        {
            if (ExplorerTaskbarSnapshotStore.TryForwardClick([viewModel.Item], rightClick: false))
            {
                return;
            }

            WindowActions.ActivateOrMinimize(viewModel.Hwnd);
            _windowTracker.Refresh();
        }
    }

    private void TaskbarButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskbarButtonViewModel viewModel })
        {
            var cursor = System.Windows.Forms.Control.MousePosition;
            if (ExplorerTaskbarSnapshotStore.TryForwardClick([viewModel.Item], rightClick: true, cursor.X, cursor.Y))
            {
                e.Handled = true;
                return;
            }

            WindowActions.ShowSystemMenu(viewModel.Hwnd);
            e.Handled = true;
        }
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AppCommands.ShowSettings();
    }

    private void RefreshMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AppCommands.Refresh();
    }

    private void OpenLogMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AppCommands.OpenLog();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AppCommands.Exit();
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

public sealed class TaskbarButtonViewModel
{
    private const int TbpfNoProgress = 0;
    private const int TbpfIndeterminate = 1;
    private const int TbpfNormal = 2;
    private const int TbpfError = 4;
    private const int TbpfPaused = 8;
    private static readonly Brush Transparent = Brushes.Transparent;
    private static readonly Brush ActiveBackground = new SolidColorBrush(Color.FromRgb(48, 57, 67));
    private static readonly Brush ActiveAccent = new SolidColorBrush(Color.FromRgb(118, 185, 237));
    private static readonly Brush InactiveAccent = new SolidColorBrush(Color.FromRgb(120, 120, 120));
    private static readonly Brush NormalProgress = new SolidColorBrush(Color.FromRgb(77, 166, 89));
    private static readonly Brush IndeterminateProgress = new SolidColorBrush(Color.FromRgb(82, 155, 214));
    private static readonly Brush ErrorProgress = new SolidColorBrush(Color.FromRgb(199, 61, 58));
    private static readonly Brush PausedProgress = new SolidColorBrush(Color.FromRgb(211, 158, 52));

    static TaskbarButtonViewModel()
    {
        ActiveBackground.Freeze();
        ActiveAccent.Freeze();
        InactiveAccent.Freeze();
        NormalProgress.Freeze();
        IndeterminateProgress.Freeze();
        ErrorProgress.Freeze();
        PausedProgress.Freeze();
    }

    public TaskbarButtonViewModel(TaskbarItem item, TaskbarButtonState? state, double buttonWidth)
    {
        Item = item;
        Hwnd = item.Hwnd;
        Title = item.Title;
        Icon = item.Icon;
        BackgroundBrush = item.IsActive ? ActiveBackground : Transparent;
        AccentBrush = item.IsActive ? ActiveAccent : InactiveAccent;
        OverlayIcon = null;
        OverlayDescription = "";
        OverlayVisibility = Visibility.Collapsed;
        ProgressVisibility = ShouldShowProgress(state) ? Visibility.Visible : Visibility.Collapsed;
        ProgressBrush = GetProgressBrush(state?.ProgressState ?? TbpfNoProgress);
        ProgressFillWidth = GetProgressFillWidth(state, buttonWidth);
    }

    public IntPtr Hwnd { get; }

    public TaskbarItem Item { get; }

    public string Title { get; }

    public ImageSource? Icon { get; }

    public ImageSource? OverlayIcon { get; }

    public string OverlayDescription { get; }

    public Brush BackgroundBrush { get; }

    public Brush AccentBrush { get; }

    public Brush ProgressBrush { get; }

    public double ProgressFillWidth { get; }

    public Visibility OverlayVisibility { get; }

    public Visibility ProgressVisibility { get; }

    private static bool ShouldShowProgress(TaskbarButtonState? state) =>
        state is not null &&
        state.ProgressState != TbpfNoProgress;

    private static Brush GetProgressBrush(int state) => state switch
    {
        TbpfError => ErrorProgress,
        TbpfPaused => PausedProgress,
        TbpfIndeterminate => IndeterminateProgress,
        _ => NormalProgress
    };

    private static double GetProgressFillWidth(TaskbarButtonState? state, double buttonWidth)
    {
        if (state is null || state.ProgressState == TbpfNoProgress)
        {
            return 0;
        }

        if (state.ProgressState == TbpfIndeterminate || state.ProgressTotal == 0)
        {
            return buttonWidth;
        }

        var fraction = Math.Clamp((double)state.ProgressCompleted / state.ProgressTotal, 0, 1);
        return Math.Max(2, Math.Round(buttonWidth * fraction));
    }
}
