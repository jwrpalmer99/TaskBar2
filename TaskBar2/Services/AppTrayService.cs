using System.Drawing;
using Forms = System.Windows.Forms;

namespace TaskBar2.Services;

internal sealed class AppTrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;

    public AppTrayService(Action refreshDisplays, Action exitApplication)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => AppCommands.ShowSettings());
        menu.Items.Add("Refresh displays", null, (_, _) => refreshDisplays());
        menu.Items.Add("Open log", null, (_, _) => AppCommands.OpenLog());
        menu.Items.Add("Exit", null, (_, _) => exitApplication());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "TaskBar2",
            ContextMenuStrip = menu,
            Visible = true
        };
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }
}
