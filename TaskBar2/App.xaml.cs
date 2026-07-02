using TaskBar2.Services;

namespace TaskBar2;

public partial class App : System.Windows.Application
{
    private AppControlService? _controlService;
    private TaskbarApplicationController? _controller;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        if (AppControlService.TryHandleCommandLine(e.Args))
        {
            Shutdown();
            return;
        }

        _controlService = new AppControlService(Dispatcher);
        _controller = new TaskbarApplicationController();
        _controller.Start();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _controller?.Dispose();
        _controlService?.Dispose();
        base.OnExit(e);
    }
}
