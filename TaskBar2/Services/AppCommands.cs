namespace TaskBar2.Services;

internal static class AppCommands
{
    public static event EventHandler? ShowSettingsRequested;

    public static event EventHandler? RefreshRequested;

    public static event EventHandler? OpenLogRequested;

    public static event EventHandler? RestartHooksRequested;

    public static event EventHandler? ExitRequested;

    public static void ShowSettings() => ShowSettingsRequested?.Invoke(null, EventArgs.Empty);

    public static void Refresh() => RefreshRequested?.Invoke(null, EventArgs.Empty);

    public static void OpenLog() => OpenLogRequested?.Invoke(null, EventArgs.Empty);

    public static void RestartHooks() => RestartHooksRequested?.Invoke(null, EventArgs.Empty);

    public static void Exit() => ExitRequested?.Invoke(null, EventArgs.Empty);
}
