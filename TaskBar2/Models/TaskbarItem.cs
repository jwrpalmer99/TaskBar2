using System.Windows.Media;

namespace TaskBar2.Models;

public sealed record TaskbarItem(
    IntPtr Hwnd,
    string Title,
    ImageSource? Icon,
    string IconFingerprint,
    bool IsActive,
    bool IsMinimized,
    string MonitorDeviceName,
    uint ProcessId,
    string ProcessName,
    string ProcessPath,
    string AppUserModelId,
    string GroupKey);
