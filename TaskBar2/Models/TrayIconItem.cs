using System.Windows.Media;

namespace TaskBar2.Models;

public sealed record TrayIconItem(
    IntPtr ToolbarHwnd,
    TrayIconBounds ScreenRect,
    ImageSource? Icon,
    string ToolTip,
    string Glyph = "*",
    TrayIconClickTarget? ClickTarget = null,
    string Identity = "",
    string SourceProcessName = "",
    string SourceProcessPath = "")
{
    public bool HasIcon => Icon is not null;
}

public readonly record struct TrayIconBounds(int Left, int Top, int Right, int Bottom);

public sealed record TrayIconClickTarget(
    IntPtr OwnerHwnd,
    int IconId,
    int CallbackMessage,
    int NotificationVersion,
    string Identity);
