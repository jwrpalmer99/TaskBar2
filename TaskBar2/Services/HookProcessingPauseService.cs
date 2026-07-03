using System.Diagnostics;
using System.Threading;

namespace TaskBar2.Services;

internal static class HookProcessingPauseService
{
    public static readonly string PauseEventName =
        $"Local\\TaskBar2.HookProcessingPaused.{Process.GetCurrentProcess().SessionId}";

    private static readonly Lazy<EventWaitHandle> PauseEvent = new(CreatePauseEvent);
    private static int _paused;

    public static bool IsPaused => Volatile.Read(ref _paused) != 0;

    public static void ApplyFullscreenPauseState(bool fullscreenPaused)
    {
        SetPaused(fullscreenPaused && AppSettingsService.Current.SuspendHookProcessingWhileFullscreen);
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _paused, 0);
        try
        {
            PauseEvent.Value.Reset();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void SetPaused(bool paused)
    {
        var pausedValue = paused ? 1 : 0;
        if (Interlocked.Exchange(ref _paused, pausedValue) == pausedValue)
        {
            return;
        }

        try
        {
            if (paused)
            {
                PauseEvent.Value.Set();
            }
            else
            {
                PauseEvent.Value.Reset();
            }
        }
        catch (ObjectDisposedException)
        {
        }

        DebugLogger.WriteIfChanged(
            "hook-processing-pause",
            paused
                ? $"Hook processing suspended for fullscreen pause. Event={PauseEventName}"
                : $"Hook processing resumed after fullscreen pause. Event={PauseEventName}");
    }

    private static EventWaitHandle CreatePauseEvent()
    {
        var pauseEvent = new EventWaitHandle(false, EventResetMode.ManualReset, PauseEventName, out _);
        pauseEvent.Reset();
        return pauseEvent;
    }
}
