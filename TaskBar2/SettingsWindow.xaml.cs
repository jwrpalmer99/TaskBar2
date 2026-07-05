using System.Windows;
using System.Windows.Controls;
using TaskBar2.Services;
using Screen = System.Windows.Forms.Screen;

namespace TaskBar2;

public partial class SettingsWindow : Window
{
    private bool _loadingMonitorSettings;

    public SettingsWindow()
    {
        InitializeComponent();
        PopulateMonitors();
        LoadSelectedMonitorSettings();
        EnableElevatedTrayIconHookAgentCheckBox.IsChecked = AppSettingsService.Current.EnableElevatedTrayIconHookAgent;
        ShowAllTrayIconsCheckBox.IsChecked = AppSettingsService.Current.ShowAllTrayIcons;
        PauseUpdatesWhileFullscreenCheckBox.IsChecked = AppSettingsService.Current.PauseNonClockUpdatesWhileFullscreen;
        SuspendHookProcessingWhileFullscreenCheckBox.IsChecked = AppSettingsService.Current.SuspendHookProcessingWhileFullscreen;
        ShowTaskbarThumbnailsOnHoverCheckBox.IsChecked = AppSettingsService.Current.ShowTaskbarThumbnailsOnHover;
        TaskbarThumbnailHoverDelayTextBox.Text = AppSettingsService.Current.TaskbarThumbnailHoverDelayMs.ToString();
        EnableExperimentalExplorerTaskbarButtonImageCaptureCheckBox.IsChecked = AppSettingsService.Current.EnableExperimentalExplorerTaskbarButtonImageCapture;
        TaskbarPollingIntervalTextBox.Text = AppSettingsService.Current.TaskbarPollingIntervalMs.ToString();
        TrayRefreshIntervalTextBox.Text = AppSettingsService.Current.TrayRefreshIntervalMs.ToString();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        AppCommands.Refresh();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        Close();
    }

    private void SaveSettings()
    {
        AppSettingsService.Update(settings =>
        {
            settings.EnableElevatedTrayIconHookAgent = EnableElevatedTrayIconHookAgentCheckBox.IsChecked == true;
            settings.ShowAllTrayIcons = ShowAllTrayIconsCheckBox.IsChecked == true;
            settings.PauseNonClockUpdatesWhileFullscreen = PauseUpdatesWhileFullscreenCheckBox.IsChecked == true;
            settings.SuspendHookProcessingWhileFullscreen = SuspendHookProcessingWhileFullscreenCheckBox.IsChecked == true;
            settings.ShowTaskbarThumbnailsOnHover = ShowTaskbarThumbnailsOnHoverCheckBox.IsChecked == true;
            settings.TaskbarThumbnailHoverDelayMs = ParseInterval(
                TaskbarThumbnailHoverDelayTextBox.Text,
                settings.TaskbarThumbnailHoverDelayMs);
            settings.EnableExperimentalExplorerTaskbarButtonImageCapture = EnableExperimentalExplorerTaskbarButtonImageCaptureCheckBox.IsChecked == true;
            AppSettingsService.SetMonitorTaskbarSettings(
                settings,
                GetSelectedMonitorDeviceName(),
                new MonitorTaskbarSettings
                {
                    ShowOnlyAppsOnThisMonitor = ShowOnlyThisMonitorCheckBox.IsChecked == true,
                    MirrorPrimaryNotificationArea = MirrorPrimaryNotificationAreaCheckBox.IsChecked == true,
                    ShowClock = ShowClockCheckBox.IsChecked == true,
                    AutomaticallyHideTaskbar = AutomaticallyHideTaskbarCheckBox.IsChecked == true,
                    TaskbarButtonAlignment = GetSelectedAlignment(),
                    TaskbarScale = TaskbarScaleSlider.Value,
                    TaskbarOpacity = TaskbarOpacitySlider.Value
                });
            settings.TaskbarPollingIntervalMs = ParseInterval(
                TaskbarPollingIntervalTextBox.Text,
                settings.TaskbarPollingIntervalMs);
            settings.TrayRefreshIntervalMs = ParseInterval(
                TrayRefreshIntervalTextBox.Text,
                settings.TrayRefreshIntervalMs);
        });

        TaskbarPollingIntervalTextBox.Text = AppSettingsService.Current.TaskbarPollingIntervalMs.ToString();
        TrayRefreshIntervalTextBox.Text = AppSettingsService.Current.TrayRefreshIntervalMs.ToString();
        TaskbarThumbnailHoverDelayTextBox.Text = AppSettingsService.Current.TaskbarThumbnailHoverDelayMs.ToString();
        LoadSelectedMonitorSettings();
    }

    private void TaskbarScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateScaleText();
    }

    private void TaskbarOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOpacityText();
    }

    private void MonitorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingMonitorSettings)
        {
            LoadSelectedMonitorSettings();
        }
    }

    private void PopulateMonitors()
    {
        var screens = Screen.AllScreens
            .Where(screen => !screen.Primary)
            .ToArray();
        if (screens.Length == 0)
        {
            screens = Screen.AllScreens;
        }

        var monitorOptions = screens
            .Select(screen => new MonitorOption(
                screen.DeviceName,
                $"{screen.DeviceName} ({screen.Bounds.Width}x{screen.Bounds.Height} at {screen.Bounds.Left},{screen.Bounds.Top})"))
            .ToArray();
        MonitorComboBox.ItemsSource = monitorOptions;

        var cursorScreen = Screen.FromPoint(System.Windows.Forms.Control.MousePosition);
        var selectedIndex = Array.FindIndex(
            monitorOptions,
            option => string.Equals(option.DeviceName, cursorScreen.DeviceName, StringComparison.OrdinalIgnoreCase));
        MonitorComboBox.SelectedIndex = selectedIndex >= 0
            ? selectedIndex
            : MonitorComboBox.Items.Count > 0 ? 0 : -1;
    }

    private void LoadSelectedMonitorSettings()
    {
        var monitorDeviceName = GetSelectedMonitorDeviceName();
        var settings = AppSettingsService.GetMonitorTaskbarSettings(monitorDeviceName);
        _loadingMonitorSettings = true;
        try
        {
            ShowOnlyThisMonitorCheckBox.IsChecked = settings.ShowOnlyAppsOnThisMonitor;
            MirrorPrimaryNotificationAreaCheckBox.IsChecked = settings.MirrorPrimaryNotificationArea;
            ShowClockCheckBox.IsChecked = settings.ShowClock;
            AutomaticallyHideTaskbarCheckBox.IsChecked = settings.AutomaticallyHideTaskbar;
            SelectAlignment(settings.TaskbarButtonAlignment);
            TaskbarScaleSlider.Value = settings.TaskbarScale;
            TaskbarOpacitySlider.Value = settings.TaskbarOpacity;
            UpdateScaleText();
            UpdateOpacityText();
        }
        finally
        {
            _loadingMonitorSettings = false;
        }
    }

    private string GetSelectedMonitorDeviceName() =>
        MonitorComboBox.SelectedItem is MonitorOption option
            ? option.DeviceName
            : "";

    private void SelectAlignment(string alignment)
    {
        foreach (var item in TaskbarButtonAlignmentComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), alignment, StringComparison.OrdinalIgnoreCase))
            {
                TaskbarButtonAlignmentComboBox.SelectedItem = item;
                return;
            }
        }

        TaskbarButtonAlignmentComboBox.SelectedIndex = 0;
    }

    private string GetSelectedAlignment()
    {
        return TaskbarButtonAlignmentComboBox.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? "Left"
            : "Left";
    }

    private static int ParseInterval(string value, int fallback)
    {
        return int.TryParse(value, out var interval) ? interval : fallback;
    }

    private void UpdateScaleText()
    {
        if (TaskbarScaleTextBlock is not null)
        {
            TaskbarScaleTextBlock.Text = $"{TaskbarScaleSlider.Value:P0}";
        }
    }

    private void UpdateOpacityText()
    {
        if (TaskbarOpacityTextBlock is not null)
        {
            TaskbarOpacityTextBlock.Text = $"{TaskbarOpacitySlider.Value:P0}";
        }
    }

    private sealed record MonitorOption(string DeviceName, string Label);
}
