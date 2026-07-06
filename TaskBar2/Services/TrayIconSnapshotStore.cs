using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskBar2.Models;
using TaskBar2.Native;

namespace TaskBar2.Services;

internal static class TrayIconSnapshotStore
{
    private const int NimDelete = 0x00000002;
    private const int NimSetVersion = 0x00000004;
    private static readonly TimeSpan DeletedIconRetention = TimeSpan.FromSeconds(2);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, SnapshotState> Snapshots = [];
    private static readonly Dictionary<string, long> SequenceByOrderKey = new(StringComparer.OrdinalIgnoreCase);
    private static long _nextSequence;

    public static event EventHandler? SnapshotsChanged;

    public static int Count
    {
        get
        {
            lock (Sync)
            {
                return Snapshots.Count;
            }
        }
    }

    public static (int Count, int VisibleCount, int IconsWithImages) GetSummary()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = GetSnapshotStates(now);
        var visibleSnapshots = GetVisibleSnapshots(snapshots, now);
        return (
            snapshots.Length,
            visibleSnapshots.Length,
            visibleSnapshots.Count(snapshot => snapshot.Icon is not null));
    }

    public static IReadOnlyList<TrayIconItem> GetItems()
    {
        var now = DateTimeOffset.UtcNow;
        return GetVisibleSnapshots(GetSnapshotStates(now), now)
            .Select(snapshot => new TrayIconItem(
                    IntPtr.Zero,
                    default,
                    snapshot.Icon,
                    snapshot.ToolTip,
                     ClickTarget: new TrayIconClickTarget(
                         snapshot.OwnerHwnd,
                         snapshot.IconId,
                         snapshot.CallbackMessage,
                         snapshot.NotificationVersion,
                         snapshot.Identity),
                     Identity: snapshot.Identity,
                     SourceProcessName: snapshot.SourceProcessName,
                     SourceProcessPath: snapshot.SourceProcessPath,
                     IconFingerprint: snapshot.IconFingerprint,
                     IsOverflow: ShouldPlaceInOverflow(snapshot)))
            .ToArray();
    }

    public static bool Apply(TrayIconHookMessage message)
    {
        var identity = BuildIdentity(message);
        if (string.IsNullOrWhiteSpace(identity))
        {
            DebugLogger.WriteIfChanged("tray-hook-empty-identity", "Tray hook message ignored: no GUID and no hwnd/id identity.");
            return false;
        }

        var changed = false;
        lock (Sync)
        {
            if (IsDelete(message))
            {
                changed = MarkDeletedLocked(identity);
            }
            else if (message.ShellMessage == NimSetVersion)
            {
                changed = UpdateVersionLocked(identity, message);
            }
            else
            {
                changed = AddOrUpdateLocked(identity, message);
            }
        }

        if (changed)
        {
            SnapshotsChanged?.Invoke(null, EventArgs.Empty);
        }

        return changed;
    }

    public static bool TryForwardClick(TrayIconItem item, bool rightClick, bool doubleClick = false)
    {
        if (item.ClickTarget is null)
        {
            return false;
        }

        var target = item.ClickTarget;
        if (target.OwnerHwnd == IntPtr.Zero || target.CallbackMessage == 0)
        {
            DebugLogger.WriteIfChanged(
                $"tray-hook-click-no-target-{target.Identity}",
                $"Tray hook click ignored: Identity={target.Identity} Owner=0x{target.OwnerHwnd.ToInt64():X} Callback=0x{target.CallbackMessage:X}");
            return true;
        }

        if (doubleClick)
        {
            ForwardDoubleClick(target);
        }
        else if (rightClick)
        {
            ForwardRightClick(target);
        }
        else
        {
            ForwardLeftClick(target);
        }

        DebugLogger.WriteIfChanged(
            $"tray-hook-click-{target.Identity}-{rightClick}-{doubleClick}",
            $"Tray hook click forwarded: Identity={target.Identity} Owner=0x{target.OwnerHwnd.ToInt64():X} Callback=0x{target.CallbackMessage:X} Version={target.NotificationVersion} RightClick={rightClick} DoubleClick={doubleClick}");
        return true;
    }

    public static void ReapplyVisibilityFilter()
    {
        var changed = false;
        lock (Sync)
        {
            if (AppSettingsService.Current.ShowAllTrayIcons)
            {
                return;
            }

            foreach (var snapshot in Snapshots.Values.ToArray())
            {
                if (IsShownBySettings(snapshot, out _, out _))
                {
                    continue;
                }

                Snapshots.Remove(snapshot.Identity);
                changed = true;
            }
        }

        if (changed)
        {
            SnapshotsChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    private static bool AddOrUpdateLocked(string identity, TrayIconHookMessage message)
    {
        Snapshots.TryGetValue(identity, out var existing);

        var toolTip = message.ToolTip ?? existing?.ToolTip ?? "";
        var ownerHwnd = message.OwnerHwnd != 0 ? new IntPtr(message.OwnerHwnd) : existing?.OwnerHwnd ?? IntPtr.Zero;
        var iconId = message.IconId;
        var callbackMessage = message.CallbackMessage != 0 ? message.CallbackMessage : existing?.CallbackMessage ?? 0;
        var notificationVersion = message.NotificationVersion != 0 ? message.NotificationVersion : existing?.NotificationVersion ?? 0;
        var hidden = message.HiddenStateKnown ? message.Hidden : existing?.Hidden ?? false;
        var hiddenStateKnown = message.HiddenStateKnown || existing?.HiddenStateKnown == true;
        var sourceProcess = ResolveSourceProcess(message, ownerHwnd, existing);
        var sourceProcessId = sourceProcess.Id;
        var sourceProcessName = sourceProcess.Name;
        var sourceProcessPath = sourceProcess.Path;
        var orderKey = BuildOrderKey(identity, iconId, sourceProcessName, sourceProcessPath);
        var replacementKey = BuildReplacementKey(identity, toolTip, sourceProcessName, sourceProcessPath);
        var appKey = BuildAppKey(sourceProcessName, sourceProcessPath);
        var updatedAt = DateTimeOffset.UtcNow;

        var visibilityCandidate = new SnapshotState(
            identity,
            orderKey,
            replacementKey,
            appKey,
            existing?.IconFingerprint ?? "",
            existing?.Sequence ?? 0,
            updatedAt,
            DeletedAt: null,
            ownerHwnd,
            iconId,
            callbackMessage,
            notificationVersion,
            toolTip,
            hidden,
            hiddenStateKnown,
            sourceProcessId,
            sourceProcessName,
            sourceProcessPath,
            existing?.Icon);

        if (!IsShownBySettings(visibilityCandidate, out var reason, out var windowsDecision))
        {
            if (existing is null)
            {
                return false;
            }

            Snapshots.Remove(identity);
            LogVisibilityDecision(visibilityCandidate, visible: false, $"FilteredBeforeStore:{reason}", windowsDecision);
            return true;
        }

        var iconFingerprint = ComputeIconFingerprint(message.IconPngBase64) ?? existing?.IconFingerprint ?? "";
        var icon = string.Equals(existing?.IconFingerprint, iconFingerprint, StringComparison.Ordinal)
            ? existing?.Icon
            : DecodeIcon(message.IconPngBase64) ?? existing?.Icon;

        if (icon is null)
        {
            DebugLogger.WriteIfChanged($"tray-hook-no-icon-{identity}", $"Tray hook snapshot has no icon image yet: Identity={identity}");
        }

        var updated = visibilityCandidate with
        {
            Icon = icon,
            IconFingerprint = iconFingerprint,
            Sequence = existing?.Sequence ?? GetOrCreateSequenceLocked(orderKey)
        };

        if (existing is not null && existing with { UpdatedAt = updated.UpdatedAt } == updated)
        {
            return false;
        }

        Snapshots[identity] = updated;
        return true;
    }

    private static bool MarkDeletedLocked(string identity)
    {
        if (!Snapshots.TryGetValue(identity, out var existing))
        {
            return false;
        }

        if (existing.DeletedAt is not null)
        {
            return false;
        }

        Snapshots[identity] = existing with
        {
            DeletedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        // Keep the icon visible for a short grace window. Most apparent tray
        // flicker is a delete/add pair from an app refreshing its shell icon.
        return true;
    }

    private static bool UpdateVersionLocked(string identity, TrayIconHookMessage message)
    {
        if (!Snapshots.TryGetValue(identity, out var existing))
        {
            return false;
        }

        Snapshots[identity] = existing with
        {
            NotificationVersion = message.NotificationVersion,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return true;
    }

    private static SnapshotState[] GetSnapshotStates(DateTimeOffset now)
    {
        lock (Sync)
        {
            PruneExpiredDeletedLocked(now);
            return Snapshots.Values.ToArray();
        }
    }

    private static SnapshotState[] GetVisibleSnapshots(SnapshotState[] snapshots, DateTimeOffset now)
    {
        var visibleSnapshots = snapshots
            .Where(snapshot => IsVisible(snapshot, now))
            .ToArray();

        LogDuplicateGroups(visibleSnapshots);

        return visibleSnapshots
            .GroupBy(BuildDeduplicationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(snapshot => snapshot.DeletedAt is null ? 0 : 1)
                .ThenByDescending(snapshot => snapshot.UpdatedAt)
                .First())
            .OrderBy(snapshot => snapshot.Sequence)
            .ThenBy(snapshot => snapshot.Identity, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsVisible(SnapshotState snapshot, DateTimeOffset now)
    {
        var reason = "";
        TrayIconVisibilityDecision? windowsDecision = null;
        var visible = true;

        if (snapshot.DeletedAt is DateTimeOffset deletedAt &&
            (snapshot.Icon is null || now - deletedAt > DeletedIconRetention))
        {
            visible = false;
            reason = "ExpiredDeletedCache";
        }
        else
        {
            visible = IsShownBySettings(snapshot, out reason, out windowsDecision);
        }

        LogVisibilityDecision(snapshot, visible, reason, windowsDecision);
        return visible;
    }

    private static bool ShouldPlaceInOverflow(SnapshotState snapshot)
    {
        if (!AppSettingsService.Current.ShowAllTrayIcons)
        {
            return false;
        }

        if (snapshot.Hidden)
        {
            return true;
        }

        var windowsDecision = TrayIconVisibilityService.GetVisibilityDecision(
            snapshot.SourceProcessPath,
            snapshot.SourceProcessName,
            snapshot.ToolTip);
        return !windowsDecision.IsShown;
    }

    private static bool IsShownBySettings(
        SnapshotState snapshot,
        out string reason,
        out TrayIconVisibilityDecision? windowsDecision)
    {
        windowsDecision = null;

        if (IsExplorerOwnedTrayIcon(snapshot))
        {
            reason = "ExplorerOwnedSystemIcon";
            return false;
        }

        if (AppSettingsService.Current.ShowAllTrayIcons)
        {
            reason = "ShowAllSetting";
            return true;
        }

        if (snapshot.Hidden)
        {
            reason = "HookHiddenState";
            windowsDecision = TrayIconVisibilityService.GetVisibilityDecision(
                snapshot.SourceProcessPath,
                snapshot.SourceProcessName,
                snapshot.ToolTip);
            return false;
        }

        windowsDecision = TrayIconVisibilityService.GetVisibilityDecision(
            snapshot.SourceProcessPath,
            snapshot.SourceProcessName,
            snapshot.ToolTip);
        reason = windowsDecision.IsShown ? "WindowsVisibilityShown" : "WindowsVisibilityHidden";
        return windowsDecision.IsShown;
    }

    private static void LogVisibilityDecision(
        SnapshotState snapshot,
        bool visible,
        string reason,
        TrayIconVisibilityDecision? windowsDecision)
    {
        DebugLogger.WriteIfChanged(
            $"tray-visibility-{snapshot.Identity}",
            "Tray icon visibility: " +
            $"Identity={snapshot.Identity} Visible={visible} Reason={reason} " +
            $"ShowAll={AppSettingsService.Current.ShowAllTrayIcons} " +
            $"HookHidden={snapshot.Hidden} HiddenKnown={snapshot.HiddenStateKnown} " +
            $"Deleted={snapshot.DeletedAt is not null} " +
            $"SourcePid={snapshot.SourceProcessId} Process={snapshot.SourceProcessName} " +
            $"Path={snapshot.SourceProcessPath} Tooltip={NormalizeForLog(snapshot.ToolTip)} " +
            $"WindowsMatch={windowsDecision?.MatchType ?? ""} " +
            $"WindowsPromoted={FormatNullableBool(windowsDecision?.IsPromoted)} " +
            $"WindowsKey={windowsDecision?.MatchedKey ?? ""} " +
            $"WindowsTooltip={NormalizeForLog(windowsDecision?.MatchedToolTip ?? "")} " +
            $"DedupKey={BuildDeduplicationKey(snapshot)} " +
            $"OrderKey={snapshot.OrderKey} ReplacementKey={snapshot.ReplacementKey} " +
            $"AppKey={snapshot.AppKey} IconHash={ShortHash(snapshot.IconFingerprint)}");
    }

    private static void LogDuplicateGroups(IReadOnlyList<SnapshotState> visibleSnapshots)
    {
        foreach (var group in visibleSnapshots
                     .GroupBy(BuildDeduplicationKey, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            DebugLogger.WriteIfChanged(
                $"tray-dedupe-{ComputeStringFingerprint(group.Key)}",
                "Tray icon dedupe group: " +
                $"Key={group.Key} Count={group.Count()} Items=" +
                string.Join(" || ", group.Select(snapshot =>
                    $"Identity={snapshot.Identity};Pid={snapshot.SourceProcessId};Process={snapshot.SourceProcessName};" +
                    $"Tooltip={NormalizeForLog(snapshot.ToolTip)};IconHash={ShortHash(snapshot.IconFingerprint)};" +
                    $"Deleted={snapshot.DeletedAt is not null};Updated={snapshot.UpdatedAt:HH:mm:ss.fff}")));
        }
    }

    private static void PruneExpiredDeletedLocked(DateTimeOffset now)
    {
        foreach (var identity in Snapshots
                     .Where(pair => pair.Value.DeletedAt is DateTimeOffset deletedAt &&
                                    now - deletedAt > DeletedIconRetention)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            Snapshots.Remove(identity);
        }

        if (SequenceByOrderKey.Count <= Snapshots.Count)
        {
            return;
        }

        var activeOrderKeys = Snapshots.Values
            .Select(snapshot => snapshot.OrderKey)
            .Where(orderKey => !string.IsNullOrWhiteSpace(orderKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var orderKey in SequenceByOrderKey.Keys
                     .Where(orderKey => !activeOrderKeys.Contains(orderKey))
                     .ToArray())
        {
            SequenceByOrderKey.Remove(orderKey);
        }
    }

    private static ImageSource? DecodeIcon(string? iconPngBase64)
    {
        if (string.IsNullOrWhiteSpace(iconPngBase64))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(iconPngBase64);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException or IOException or COMException)
        {
            DebugLogger.WriteIfChanged(
                "tray-hook-decode-error",
                $"Tray hook icon decode failed: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    private static string? ComputeIconFingerprint(string? iconPngBase64)
    {
        if (string.IsNullOrWhiteSpace(iconPngBase64))
        {
            return null;
        }

        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var character in iconPngBase64)
        {
            hash ^= character;
            hash *= prime;
        }

        return $"b64:{iconPngBase64.Length}:{hash:X16}";
    }

    private static void ForwardLeftClick(TrayIconClickTarget target)
    {
        if (target.NotificationVersion >= 4)
        {
            PrepareForegroundMenuOwner(target.OwnerHwnd);
            PostNotification(target, NativeMethods.NIN_SELECT);
            return;
        }

        PostNotification(target, NativeMethods.WM_LBUTTONDOWN);
        PostNotification(target, NativeMethods.WM_LBUTTONUP);
    }

    private static void ForwardDoubleClick(TrayIconClickTarget target)
    {
        PostNotification(target, NativeMethods.WM_LBUTTONDBLCLK);
        PostNotification(target, NativeMethods.WM_LBUTTONUP);
    }

    private static void ForwardRightClick(TrayIconClickTarget target)
    {
        PrepareForegroundMenuOwner(target.OwnerHwnd);

        if (target.NotificationVersion >= 4)
        {
            PostNotification(target, NativeMethods.WM_CONTEXTMENU);
            PostOwnerNullMessageLater(target.OwnerHwnd);
            return;
        }

        PostNotification(target, NativeMethods.WM_RBUTTONDOWN);
        PostNotification(target, NativeMethods.WM_RBUTTONUP);
        PostOwnerNullMessageLater(target.OwnerHwnd);
    }

    private static void PrepareForegroundMenuOwner(IntPtr ownerHwnd)
    {
        if (ownerHwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.GetWindowThreadProcessId(ownerHwnd, out var processId);
            if (processId != 0)
            {
                NativeMethods.AllowSetForegroundWindow(processId);
            }

            NativeMethods.SetForegroundWindow(ownerHwnd);
        }
        catch
        {
        }
    }

    private static void PostOwnerNullMessageLater(IntPtr ownerHwnd)
    {
        if (ownerHwnd == IntPtr.Zero)
        {
            return;
        }

        _ = Task.Delay(150).ContinueWith(
            _ => NativeMethods.PostMessage(ownerHwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero),
            TaskScheduler.Default);
    }

    private static void PostNotification(TrayIconClickTarget target, int notificationMessage)
    {
        var wParam = target.NotificationVersion >= 4
            ? BuildCoordinateParam()
            : new IntPtr(target.IconId);
        var lParam = target.NotificationVersion >= 4
            ? new IntPtr(((target.IconId & 0xFFFF) << 16) | (notificationMessage & 0xFFFF))
            : new IntPtr(notificationMessage);

        if (!NativeMethods.PostMessage(target.OwnerHwnd, (uint)target.CallbackMessage, wParam, lParam))
        {
            DebugLogger.WriteIfChanged(
                $"tray-hook-post-failed-{target.Identity}-{notificationMessage}",
                $"Tray hook callback post failed: Identity={target.Identity} Owner=0x{target.OwnerHwnd.ToInt64():X} Callback=0x{target.CallbackMessage:X} Event=0x{notificationMessage:X} LastError={Marshal.GetLastWin32Error()}");
        }
    }

    private static IntPtr BuildCoordinateParam()
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            return IntPtr.Zero;
        }

        var x = unchecked((ushort)(short)point.X);
        var y = unchecked((ushort)(short)point.Y);
        return new IntPtr(unchecked((int)(x | (y << 16))));
    }

    private static bool IsDelete(TrayIconHookMessage message) =>
        message.ShellMessage == NimDelete ||
        string.Equals(message.Operation, "Delete", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(message.Operation, "NIM_DELETE", StringComparison.OrdinalIgnoreCase);

    private static string BuildIdentity(TrayIconHookMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Guid) &&
            System.Guid.TryParse(message.Guid, out var parsedGuid) &&
            parsedGuid != System.Guid.Empty)
        {
            return $"guid:{parsedGuid:D}";
        }

        return message.OwnerHwnd == 0 && message.IconId == 0
            ? ""
            : $"hwnd:{message.OwnerHwnd:X}:{message.IconId}";
    }

    private static long GetOrCreateSequenceLocked(string orderKey)
    {
        if (SequenceByOrderKey.TryGetValue(orderKey, out var sequence))
        {
            return sequence;
        }

        sequence = ++_nextSequence;
        SequenceByOrderKey[orderKey] = sequence;
        return sequence;
    }

    private static SourceProcess ResolveSourceProcess(
        TrayIconHookMessage message,
        IntPtr ownerHwnd,
        SnapshotState? existing)
    {
        var sourceProcessId = message.SourceProcessId != 0
            ? message.SourceProcessId
            : existing?.SourceProcessId ?? 0;
        var sourceProcessName = !string.IsNullOrWhiteSpace(message.SourceProcessName)
            ? message.SourceProcessName
            : existing?.SourceProcessName ?? "";
        var sourceProcessPath = !string.IsNullOrWhiteSpace(message.SourceProcessPath)
            ? message.SourceProcessPath
            : existing?.SourceProcessPath ?? "";

        if (!string.IsNullOrWhiteSpace(sourceProcessName) &&
            !string.IsNullOrWhiteSpace(sourceProcessPath))
        {
            return new SourceProcess(sourceProcessId, sourceProcessName, sourceProcessPath);
        }

        if (ownerHwnd != IntPtr.Zero &&
            NativeMethods.GetWindowThreadProcessId(ownerHwnd, out var ownerProcessId) != 0 &&
            ownerProcessId != 0)
        {
            sourceProcessId = sourceProcessId != 0 ? sourceProcessId : unchecked((int)ownerProcessId);
            if (string.IsNullOrWhiteSpace(sourceProcessName) ||
                string.IsNullOrWhiteSpace(sourceProcessPath))
            {
                var ownerProcess = ResolveProcessById(ownerProcessId);
                if (string.IsNullOrWhiteSpace(sourceProcessName))
                {
                    sourceProcessName = ownerProcess.Name;
                }

                if (string.IsNullOrWhiteSpace(sourceProcessPath))
                {
                    sourceProcessPath = ownerProcess.Path;
                }
            }
        }

        return new SourceProcess(sourceProcessId, sourceProcessName, sourceProcessPath);
    }

    private static SourceProcess ResolveProcessById(uint processId)
    {
        var processName = "";
        var processPath = "";

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(unchecked((int)processId));
            processName = process.ProcessName;
            try
            {
                processPath = process.MainModule?.FileName ?? "";
            }
            catch
            {
            }
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(processPath))
        {
            processPath = QueryProcessImagePath(processId);
        }

        if (string.IsNullOrWhiteSpace(processName) &&
            !string.IsNullOrWhiteSpace(processPath))
        {
            processName = Path.GetFileNameWithoutExtension(processPath);
        }

        return new SourceProcess(unchecked((int)processId), processName, processPath);
    }

    private static string QueryProcessImagePath(uint processId)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            processId);
        if (handle == IntPtr.Zero)
        {
            return "";
        }

        try
        {
            var builder = new StringBuilder(4096);
            var length = builder.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, builder, ref length)
                ? builder.ToString()
                : "";
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static string BuildOrderKey(
        string identity,
        int iconId,
        string sourceProcessName,
        string sourceProcessPath)
    {
        if (identity.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
        {
            return identity;
        }

        if (!string.IsNullOrWhiteSpace(sourceProcessPath))
        {
            return $"path:{NormalizePath(sourceProcessPath)}:id:{iconId}";
        }

        if (!string.IsNullOrWhiteSpace(sourceProcessName))
        {
            return $"process:{Path.GetFileNameWithoutExtension(sourceProcessName)}:id:{iconId}";
        }

        return identity;
    }

    private static string BuildReplacementKey(
        string identity,
        string toolTip,
        string sourceProcessName,
        string sourceProcessPath)
    {
        if (identity.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
        {
            return identity;
        }

        var normalizedToolTip = NormalizeToolTip(toolTip);
        if (string.IsNullOrWhiteSpace(normalizedToolTip))
        {
            return "";
        }

        if (!string.IsNullOrWhiteSpace(sourceProcessPath))
        {
            return $"path:{NormalizePath(sourceProcessPath)}:tip:{normalizedToolTip}";
        }

        if (!string.IsNullOrWhiteSpace(sourceProcessName))
        {
            return $"process:{Path.GetFileNameWithoutExtension(sourceProcessName)}:tip:{normalizedToolTip}";
        }

        return "";
    }

    private static string BuildAppKey(string sourceProcessName, string sourceProcessPath)
    {
        if (!string.IsNullOrWhiteSpace(sourceProcessPath))
        {
            return $"path:{NormalizePath(sourceProcessPath)}";
        }

        if (!string.IsNullOrWhiteSpace(sourceProcessName))
        {
            return $"process:{Path.GetFileNameWithoutExtension(sourceProcessName)}";
        }

        return "";
    }

    private static string BuildAppIconKey(SnapshotState snapshot) =>
        snapshot.AppKey + "\n" + snapshot.IconFingerprint;

    private static string BuildDeduplicationKey(SnapshotState snapshot)
    {
        if (IsWallpaperEngine(snapshot) && !string.IsNullOrWhiteSpace(snapshot.AppKey))
        {
            return "wallpaper-engine:" + snapshot.AppKey;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.ReplacementKey))
        {
            return "replacement:" + snapshot.ReplacementKey;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.AppKey) &&
            !string.IsNullOrWhiteSpace(snapshot.IconFingerprint))
        {
            return "app-icon:" + BuildAppIconKey(snapshot);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.OrderKey))
        {
            return "order:" + snapshot.OrderKey;
        }

        return "identity:" + snapshot.Identity;
    }

    private static bool IsWallpaperEngine(SnapshotState snapshot)
    {
        var processName = Path.GetFileNameWithoutExtension(snapshot.SourceProcessName);
        return string.Equals(processName, "wallpaper32", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(processName, "wallpaper64", StringComparison.OrdinalIgnoreCase) ||
               snapshot.SourceProcessPath.Contains("wallpaper_engine", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExplorerOwnedTrayIcon(SnapshotState snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.SourceProcessPath) &&
            string.Equals(Path.GetFileName(snapshot.SourceProcessPath), "explorer.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            Path.GetFileNameWithoutExtension(snapshot.SourceProcessName),
            "explorer",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatNullableBool(bool? value) =>
        value is null ? "" : value.Value ? "true" : "false";

    private static string NormalizeForLog(string value) =>
        NormalizeToolTip(value).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string ShortHash(string value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value[..Math.Min(12, value.Length)];

    private static string ComputeStringFingerprint(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash.ToString("X16")[..12];
    }

    private static string NormalizeToolTip(string toolTip) =>
        string.Join(
            "\n",
            toolTip
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).TrimEnd('\\');
        }
        catch
        {
            return path.Trim();
        }
    }

    private sealed record SnapshotState(
        string Identity,
        string OrderKey,
        string ReplacementKey,
        string AppKey,
        string IconFingerprint,
        long Sequence,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? DeletedAt,
        IntPtr OwnerHwnd,
        int IconId,
        int CallbackMessage,
        int NotificationVersion,
        string ToolTip,
        bool Hidden,
        bool HiddenStateKnown,
        int SourceProcessId,
        string SourceProcessName,
        string SourceProcessPath,
        ImageSource? Icon);

    private sealed record SourceProcess(
        int Id,
        string Name,
        string Path);
}

internal sealed class TrayIconHookMessage
{
    public int ProtocolVersion { get; set; } = 1;

    public string? Operation { get; set; }

    public int? ShellMessage { get; set; }

    public long OwnerHwnd { get; set; }

    public int IconId { get; set; }

    public string? Guid { get; set; }

    public int CallbackMessage { get; set; }

    public int NotificationVersion { get; set; }

    public string? ToolTip { get; set; }

    public string? IconPngBase64 { get; set; }

    public bool Hidden { get; set; }

    public bool HiddenStateKnown { get; set; }

    public int SourceProcessId { get; set; }

    public string? SourceProcessName { get; set; }

    public string? SourceProcessPath { get; set; }
}
