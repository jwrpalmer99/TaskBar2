using System.IO;
using System.Text.Json;

namespace TaskBar2.Services;

internal sealed class AppSettings
{
    public bool ShowOnlyAppsOnThisMonitor { get; set; }

    public bool MirrorPrimaryNotificationArea { get; set; } = true;

    public string TaskbarButtonAlignment { get; set; } = "Left";

    public double TaskbarScale { get; set; } = 1.0;

    public Dictionary<string, MonitorTaskbarSettings> MonitorTaskbarSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int TaskbarPollingIntervalMs { get; set; } = AppSettingsService.DefaultTaskbarPollingIntervalMs;

    public int TrayRefreshIntervalMs { get; set; } = AppSettingsService.DefaultTrayRefreshIntervalMs;

    public bool EnableInvasiveTrayIconHook { get; set; }

    public bool EnableElevatedTrayIconHookAgent { get; set; }

    public bool ShowAllTrayIcons { get; set; }

    public bool PauseNonClockUpdatesWhileFullscreen { get; set; }

    public bool ShowTaskbarThumbnailsOnHover { get; set; } = true;

    public int TaskbarThumbnailHoverDelayMs { get; set; } = AppSettingsService.DefaultTaskbarThumbnailHoverDelayMs;

    public bool EnableExperimentalExplorerTaskbarHook { get; set; } = true;

    public bool EnableExperimentalExplorerTaskbarButtonImageCapture { get; set; }
}

internal sealed class MonitorTaskbarSettings
{
    public bool ShowOnlyAppsOnThisMonitor { get; set; }

    public bool MirrorPrimaryNotificationArea { get; set; } = true;

    public bool ShowClock { get; set; } = true;

    public bool AutomaticallyHideTaskbar { get; set; }

    public string TaskbarButtonAlignment { get; set; } = "Left";

    public double TaskbarScale { get; set; } = 1.0;

    public double TaskbarOpacity { get; set; } = 1.0;
}

internal static class AppSettingsService
{
    public const int DefaultTaskbarPollingIntervalMs = 750;
    public const int DefaultTrayRefreshIntervalMs = 2000;
    public const int DefaultTaskbarThumbnailHoverDelayMs = 450;
    public const int MinPollingIntervalMs = 100;
    public const int MaxPollingIntervalMs = 30000;
    public const int MinTaskbarThumbnailHoverDelayMs = 0;
    public const int MaxTaskbarThumbnailHoverDelayMs = 5000;
    public const double MinTaskbarScale = 0.75;
    public const double MaxTaskbarScale = 1.75;
    public const double MinTaskbarOpacity = 0.35;
    public const double MaxTaskbarOpacity = 1.0;

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskBar2");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static event EventHandler? SettingsChanged;

    public static AppSettings Current { get; private set; } = Load();

    public static MonitorTaskbarSettings GetMonitorTaskbarSettings(string monitorDeviceName)
    {
        if (!string.IsNullOrWhiteSpace(monitorDeviceName) &&
            Current.MonitorTaskbarSettings.TryGetValue(monitorDeviceName, out var monitorSettings))
        {
            return CloneMonitorTaskbarSettings(monitorSettings);
        }

        return new MonitorTaskbarSettings
        {
            ShowOnlyAppsOnThisMonitor = Current.ShowOnlyAppsOnThisMonitor,
            MirrorPrimaryNotificationArea = Current.MirrorPrimaryNotificationArea,
            ShowClock = true,
            AutomaticallyHideTaskbar = false,
            TaskbarButtonAlignment = Current.TaskbarButtonAlignment,
            TaskbarScale = Current.TaskbarScale,
            TaskbarOpacity = 1.0
        };
    }

    public static bool ShouldMirrorNotificationAreaOnAnyMonitor(IEnumerable<string> monitorDeviceNames)
    {
        foreach (var monitorDeviceName in monitorDeviceNames)
        {
            if (GetMonitorTaskbarSettings(monitorDeviceName).MirrorPrimaryNotificationArea)
            {
                return true;
            }
        }

        return false;
    }

    public static void SetMonitorTaskbarSettings(
        AppSettings settings,
        string monitorDeviceName,
        MonitorTaskbarSettings monitorSettings)
    {
        if (string.IsNullOrWhiteSpace(monitorDeviceName))
        {
            return;
        }

        Normalize(monitorSettings);
        settings.MonitorTaskbarSettings[monitorDeviceName] = CloneMonitorTaskbarSettings(monitorSettings);
    }

    public static void Update(Action<AppSettings> update)
    {
        update(Current);
        Normalize(Current);
        Save();
        SettingsChanged?.Invoke(null, EventArgs.Empty);
    }

    private static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            Normalize(settings);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    private static void Normalize(AppSettings settings)
    {
        settings.TaskbarButtonAlignment = NormalizeAlignment(settings.TaskbarButtonAlignment);
        settings.TaskbarScale = NormalizeScale(settings.TaskbarScale);
        settings.MonitorTaskbarSettings ??= new Dictionary<string, MonitorTaskbarSettings>(StringComparer.OrdinalIgnoreCase);
        settings.MonitorTaskbarSettings = new Dictionary<string, MonitorTaskbarSettings>(
            settings.MonitorTaskbarSettings
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .Select(pair =>
                {
                    Normalize(pair.Value);
                    return pair;
                }),
            StringComparer.OrdinalIgnoreCase);

        settings.TaskbarPollingIntervalMs = NormalizeInterval(
            settings.TaskbarPollingIntervalMs,
            DefaultTaskbarPollingIntervalMs);
        settings.TrayRefreshIntervalMs = NormalizeInterval(
            settings.TrayRefreshIntervalMs,
            DefaultTrayRefreshIntervalMs);
        settings.TaskbarThumbnailHoverDelayMs = NormalizeThumbnailHoverDelay(settings.TaskbarThumbnailHoverDelayMs);
    }

    private static int NormalizeInterval(int value, int fallback)
    {
        if (value <= 0)
        {
            value = fallback;
        }

        return Math.Clamp(value, MinPollingIntervalMs, MaxPollingIntervalMs);
    }

    private static int NormalizeThumbnailHoverDelay(int value) =>
        Math.Clamp(value, MinTaskbarThumbnailHoverDelayMs, MaxTaskbarThumbnailHoverDelayMs);

    private static void Normalize(MonitorTaskbarSettings settings)
    {
        settings.TaskbarButtonAlignment = NormalizeAlignment(settings.TaskbarButtonAlignment);
        settings.TaskbarScale = NormalizeScale(settings.TaskbarScale);
        settings.TaskbarOpacity = NormalizeOpacity(settings.TaskbarOpacity);
    }

    private static string NormalizeAlignment(string value) =>
        string.Equals(value, "Center", StringComparison.OrdinalIgnoreCase)
            ? "Center"
            : "Left";

    private static double NormalizeScale(double value) =>
        double.IsNaN(value) || value <= 0
            ? 1.0
            : Math.Clamp(value, MinTaskbarScale, MaxTaskbarScale);

    private static double NormalizeOpacity(double value) =>
        double.IsNaN(value) || value <= 0
            ? 1.0
            : Math.Clamp(value, MinTaskbarOpacity, MaxTaskbarOpacity);

    private static MonitorTaskbarSettings CloneMonitorTaskbarSettings(MonitorTaskbarSettings settings) =>
        new()
        {
            ShowOnlyAppsOnThisMonitor = settings.ShowOnlyAppsOnThisMonitor,
            MirrorPrimaryNotificationArea = settings.MirrorPrimaryNotificationArea,
            ShowClock = settings.ShowClock,
            AutomaticallyHideTaskbar = settings.AutomaticallyHideTaskbar,
            TaskbarButtonAlignment = NormalizeAlignment(settings.TaskbarButtonAlignment),
            TaskbarScale = NormalizeScale(settings.TaskbarScale),
            TaskbarOpacity = NormalizeOpacity(settings.TaskbarOpacity)
        };

    private static void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
    }
}
