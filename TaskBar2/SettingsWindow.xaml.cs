using System.Windows;
using System.Windows.Controls;
using TaskBar2.Services;

namespace TaskBar2;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        ShowOnlyThisMonitorCheckBox.IsChecked = AppSettingsService.Current.ShowOnlyAppsOnThisMonitor;
        EnableInvasiveTrayIconHookCheckBox.IsChecked = AppSettingsService.Current.EnableInvasiveTrayIconHook;
        EnableElevatedTrayIconHookAgentCheckBox.IsChecked = AppSettingsService.Current.EnableElevatedTrayIconHookAgent;
        ShowAllTrayIconsCheckBox.IsChecked = AppSettingsService.Current.ShowAllTrayIcons;
        UseNativeTaskbarRendererCheckBox.IsChecked = AppSettingsService.Current.UseNativeTaskbarRenderer;
        PauseUpdatesWhileFullscreenCheckBox.IsChecked = AppSettingsService.Current.PauseNonClockUpdatesWhileFullscreen;
        EnableExperimentalExplorerTaskbarHookCheckBox.IsChecked = AppSettingsService.Current.EnableExperimentalExplorerTaskbarHook;
        EnableExperimentalExplorerTaskbarMenuProxyCheckBox.IsChecked = AppSettingsService.Current.EnableExperimentalExplorerTaskbarMenuProxy;
        SelectAlignment(AppSettingsService.Current.TaskbarButtonAlignment);
        TaskbarScaleSlider.Value = AppSettingsService.Current.TaskbarScale;
        UpdateScaleText();
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
            settings.ShowOnlyAppsOnThisMonitor = ShowOnlyThisMonitorCheckBox.IsChecked == true;
            settings.EnableInvasiveTrayIconHook = EnableInvasiveTrayIconHookCheckBox.IsChecked == true;
            settings.EnableElevatedTrayIconHookAgent = EnableElevatedTrayIconHookAgentCheckBox.IsChecked == true;
            settings.ShowAllTrayIcons = ShowAllTrayIconsCheckBox.IsChecked == true;
            settings.UseNativeTaskbarRenderer = UseNativeTaskbarRendererCheckBox.IsChecked == true;
            settings.PauseNonClockUpdatesWhileFullscreen = PauseUpdatesWhileFullscreenCheckBox.IsChecked == true;
            settings.EnableExperimentalExplorerTaskbarHook = EnableExperimentalExplorerTaskbarHookCheckBox.IsChecked == true;
            settings.EnableExperimentalExplorerTaskbarMenuProxy = EnableExperimentalExplorerTaskbarMenuProxyCheckBox.IsChecked == true;
            settings.TaskbarButtonAlignment = GetSelectedAlignment();
            settings.TaskbarScale = TaskbarScaleSlider.Value;
            settings.TaskbarPollingIntervalMs = ParseInterval(
                TaskbarPollingIntervalTextBox.Text,
                settings.TaskbarPollingIntervalMs);
            settings.TrayRefreshIntervalMs = ParseInterval(
                TrayRefreshIntervalTextBox.Text,
                settings.TrayRefreshIntervalMs);
        });

        TaskbarPollingIntervalTextBox.Text = AppSettingsService.Current.TaskbarPollingIntervalMs.ToString();
        TrayRefreshIntervalTextBox.Text = AppSettingsService.Current.TrayRefreshIntervalMs.ToString();
        TaskbarScaleSlider.Value = AppSettingsService.Current.TaskbarScale;
        UpdateScaleText();
    }

    private void TaskbarScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateScaleText();
    }

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
}
