using System.IO;
using System.Text.Json;

namespace TaskBar2.Services;

internal sealed class AppSettings
{
    public bool ShowOnlyAppsOnThisMonitor { get; set; }

    public string TaskbarButtonAlignment { get; set; } = "Left";

    public double TaskbarScale { get; set; } = 1.0;

    public int TaskbarPollingIntervalMs { get; set; } = AppSettingsService.DefaultTaskbarPollingIntervalMs;

    public int TrayRefreshIntervalMs { get; set; } = AppSettingsService.DefaultTrayRefreshIntervalMs;

    public bool EnableInvasiveTrayIconHook { get; set; }

    public bool EnableElevatedTrayIconHookAgent { get; set; }

    public bool ShowAllTrayIcons { get; set; }

    public bool UseNativeTaskbarRenderer { get; set; }

    public bool PauseNonClockUpdatesWhileFullscreen { get; set; }

    public bool EnableExperimentalExplorerTaskbarHook { get; set; }

    public bool EnableExperimentalExplorerTaskbarMenuProxy { get; set; }
}

internal static class AppSettingsService
{
    public const int DefaultTaskbarPollingIntervalMs = 750;
    public const int DefaultTrayRefreshIntervalMs = 2000;
    public const int MinPollingIntervalMs = 100;
    public const int MaxPollingIntervalMs = 30000;
    public const double MinTaskbarScale = 0.75;
    public const double MaxTaskbarScale = 1.75;

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskBar2");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static event EventHandler? SettingsChanged;

    public static AppSettings Current { get; private set; } = Load();

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
        if (!string.Equals(settings.TaskbarButtonAlignment, "Center", StringComparison.OrdinalIgnoreCase))
        {
            settings.TaskbarButtonAlignment = "Left";
        }
        else
        {
            settings.TaskbarButtonAlignment = "Center";
        }

        if (double.IsNaN(settings.TaskbarScale) || settings.TaskbarScale <= 0)
        {
            settings.TaskbarScale = 1.0;
        }

        settings.TaskbarScale = Math.Clamp(settings.TaskbarScale, MinTaskbarScale, MaxTaskbarScale);

        settings.TaskbarPollingIntervalMs = NormalizeInterval(
            settings.TaskbarPollingIntervalMs,
            DefaultTaskbarPollingIntervalMs);
        settings.TrayRefreshIntervalMs = NormalizeInterval(
            settings.TrayRefreshIntervalMs,
            DefaultTrayRefreshIntervalMs);
    }

    private static int NormalizeInterval(int value, int fallback)
    {
        if (value <= 0)
        {
            value = fallback;
        }

        return Math.Clamp(value, MinPollingIntervalMs, MaxPollingIntervalMs);
    }

    private static void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
    }
}
