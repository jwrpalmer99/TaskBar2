using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace TaskBar2.Services;

internal sealed class AppControlService : IDisposable
{
    private readonly EventWaitHandle _exitEvent;
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public AppControlService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _thread = new Thread(WaitForExitRequest)
        {
            IsBackground = true,
            Name = "TaskBar2 app control"
        };
        _thread.Start();
    }

    public static bool TryHandleCommandLine(string[] args)
    {
        if (!args.Any(argument =>
                string.Equals(argument, "--exit-existing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "/exit-existing", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        try
        {
            using var exitEvent = EventWaitHandle.OpenExisting(ExitEventName);
            exitEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            DebugLogger.Write("Exit-existing requested but no running TaskBar2 control event was found.");
        }
        catch (Exception exception)
        {
            DebugLogger.Write($"Exit-existing request failed: {exception.GetType().Name}: {exception.Message}");
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _exitEvent.Set();
            _thread.Join(1000);
        }
        catch
        {
        }
        finally
        {
            _exitEvent.Dispose();
        }
    }

    private static string ExitEventName => $"Local\\TaskBar2.Exit.{Process.GetCurrentProcess().SessionId}";

    private void WaitForExitRequest()
    {
        while (!_disposed)
        {
            try
            {
                _exitEvent.WaitOne();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (_disposed)
            {
                return;
            }

            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed)
                {
                    DebugLogger.Write("Exit requested through app control event.");
                    System.Windows.Application.Current.Shutdown();
                }
            });
        }
    }
}
