using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskBar2.Models;
using TaskBar2.Native;

namespace TaskBar2.Services;

internal static class ExplorerTaskbarSnapshotStore
{
    private const int MinimumUsableButtonImageBytes = 512;
    private static readonly object Sync = new();
    private static IReadOnlyList<ExplorerTaskbarButtonItem> Buttons = Array.Empty<ExplorerTaskbarButtonItem>();
    private static readonly Dictionary<string, string> InvalidatedImageFingerprintsByGroupKey = [];
    private static string _lastSignature = "";
    private static DateTimeOffset _lastProxyRefreshRequested = DateTimeOffset.MinValue;

    public static event EventHandler? SnapshotChanged;

    public static void Apply(ExplorerTaskbarSnapshotMessage message)
    {
        var buttons = message.Buttons
            .Where(button => button.Right > button.Left && button.Bottom > button.Top)
            .Where(IsUsableTaskbarButton)
            .Select(button => new ExplorerTaskbarButtonItem(
                button.RuntimeId ?? "",
                button.Name ?? "",
                button.ClassName ?? "",
                button.AutomationId ?? "",
                button.ControlType ?? "",
                button.NativeWindowHandle,
                new IntPtr(button.RootHwnd),
                button.RootClassName ?? "",
                button.Left,
                button.Top,
                button.Right,
                button.Bottom,
                DecodeBytes(button.ButtonIconPngBase64),
                button.ButtonIconFingerprint ?? ""))
            .ToArray();

        var signature = BuildSignature(message, buttons);
        int retainedCount;
        lock (Sync)
        {
            retainedCount = Buttons.Count;
            if (buttons.Length == 0 && retainedCount > 0)
            {
                if (signature != _lastSignature)
                {
                    _lastSignature = signature;
                    DebugLogger.WriteIfChanged(
                        "explorer-taskbar-snapshot-retained",
                        "Explorer taskbar snapshot ignored because it had no usable app buttons: " +
                        $"Roots={message.Roots.Count} RawButtons={message.Buttons.Count} RetainedButtons={retainedCount} Error={message.Error ?? ""} " +
                        $"RawItems={string.Join(" || ", message.Buttons.Take(12).Select(FormatRawButtonForLog))}");
                }

                return;
            }

            Buttons = buttons;
        }

        if (signature == _lastSignature)
        {
            return;
        }

        _lastSignature = signature;
        SnapshotChanged?.Invoke(null, EventArgs.Empty);
        DebugLogger.WriteIfChanged(
            "explorer-taskbar-snapshot",
            "Explorer taskbar snapshot: " +
            $"Roots={message.Roots.Count} Buttons={buttons.Length} Error={message.Error ?? ""} " +
            $"Items={string.Join(" || ", buttons.Take(24).Select(FormatButtonForLog))}");
    }

    public static bool TryForwardClick(IReadOnlyList<TaskbarItem> groupItems, bool rightClick)
    {
        var cursor = System.Windows.Forms.Control.MousePosition;
        return TryForwardClick(groupItems, rightClick, cursor.X, cursor.Y);
    }

    public static IReadOnlyList<TaskbarItem> MergePinnedItems(IReadOnlyList<TaskbarItem> windowItems)
    {
        if (TryMergeExplorerButtonOrder(windowItems, out var explorerOrderedItems))
        {
            return explorerOrderedItems;
        }

        var pinnedItems = PinnedTaskbarShortcutProvider.GetPinnedItems();
        if (pinnedItems.Count == 0)
        {
            return windowItems;
        }

        var merged = MergePinnedOrder(windowItems, pinnedItems);
        DebugLogger.WriteIfChanged(
            "taskbar-pinned-merge",
            "Taskbar pinned merge: " +
            $"Source=Shortcuts Windows={windowItems.Count} PinnedSlots={pinnedItems.Count} Output={merged.Count} " +
            $"Pinned={string.Join(" | ", pinnedItems.Take(24).Select(item => $"{item.Title}:{item.GroupKey}"))}");
        return merged;
    }

    private static bool TryMergeExplorerButtonOrder(
        IReadOnlyList<TaskbarItem> windowItems,
        out IReadOnlyList<TaskbarItem> orderedItems)
    {
        orderedItems = Array.Empty<TaskbarItem>();
        if (!AppSettingsService.Current.EnableExperimentalExplorerTaskbarHook)
        {
            return false;
        }

        ExplorerTaskbarButtonItem[] buttons;
        lock (Sync)
        {
            buttons = Buttons.ToArray();
        }

        if (buttons.Length == 0)
        {
            return false;
        }

        var windowGroups = windowItems
            .GroupBy(item => item.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.ToArray())
            .ToArray();
        var usedWindowGroupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emittedGroupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<TaskbarItem>(windowItems.Count + buttons.Length);
        var matchedButtons = 0;
        var pinnedButtons = 0;

        foreach (var button in GetVisualButtonOrder(buttons))
        {
            var matchingGroup = FindMatchingWindowGroup(windowGroups, button, usedWindowGroupKeys);
            if (matchingGroup is not null)
            {
                usedWindowGroupKeys.Add(matchingGroup[0].GroupKey);
                if (emittedGroupKeys.Add(matchingGroup[0].GroupKey))
                {
                    merged.AddRange(matchingGroup);
                    matchedButtons++;
                }

                continue;
            }

            if (IsPinnedButton(button) &&
                TryCreatePinnedItem(button, out var pinnedItem) &&
                emittedGroupKeys.Add(pinnedItem.GroupKey))
            {
                merged.Add(pinnedItem);
                pinnedButtons++;
            }
        }

        foreach (var group in windowGroups)
        {
            if (usedWindowGroupKeys.Contains(group[0].GroupKey))
            {
                continue;
            }

            if (emittedGroupKeys.Add(group[0].GroupKey))
            {
                merged.AddRange(group);
            }
        }

        if (matchedButtons == 0 && pinnedButtons == 0)
        {
            return false;
        }

        orderedItems = merged;
        DebugLogger.WriteIfChanged(
            "taskbar-explorer-order-merge",
            "Taskbar Explorer order merge: " +
            $"Windows={windowItems.Count} Buttons={buttons.Length} MatchedButtons={matchedButtons} PinnedButtons={pinnedButtons} Output={merged.Count} " +
            $"Order={string.Join(" | ", merged.Take(32).Select(item => $"{item.Title}:{item.GroupKey}"))}");
        return true;
    }

    private static IEnumerable<ExplorerTaskbarButtonItem> GetVisualButtonOrder(IEnumerable<ExplorerTaskbarButtonItem> buttons)
    {
        return buttons
            .OrderBy(button => string.Equals(button.RootClassName, "Shell_TrayWnd", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(button => button.Left)
            .ThenBy(button => button.Top)
            .ThenBy(button => button.Right);
    }

    private static IReadOnlyList<TaskbarItem> MergePinnedOrder(
        IReadOnlyList<TaskbarItem> windowItems,
        IReadOnlyList<TaskbarItem> pinnedItems)
    {
        var windowGroups = windowItems
            .GroupBy(item => item.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.ToArray())
            .ToArray();
        var usedWindowGroupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emittedGroupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<TaskbarItem>(windowItems.Count + pinnedItems.Count);

        foreach (var pinnedItem in pinnedItems)
        {
            var matchingGroup = FindMatchingWindowGroup(windowGroups, pinnedItem, usedWindowGroupKeys);
            if (matchingGroup is not null)
            {
                usedWindowGroupKeys.Add(matchingGroup[0].GroupKey);
                if (emittedGroupKeys.Add(matchingGroup[0].GroupKey))
                {
                    merged.AddRange(matchingGroup);
                }

                continue;
            }

            if (emittedGroupKeys.Add(pinnedItem.GroupKey))
            {
                merged.Add(pinnedItem);
            }
        }

        foreach (var group in windowGroups)
        {
            if (usedWindowGroupKeys.Contains(group[0].GroupKey))
            {
                continue;
            }

            if (emittedGroupKeys.Add(group[0].GroupKey))
            {
                merged.AddRange(group);
            }
        }

        return merged;
    }

    private static TaskbarItem[]? FindMatchingWindowGroup(
        IReadOnlyList<TaskbarItem[]> windowGroups,
        TaskbarItem pinnedItem,
        HashSet<string> usedWindowGroupKeys)
    {
        foreach (var group in windowGroups)
        {
            if (!usedWindowGroupKeys.Contains(group[0].GroupKey) &&
                string.Equals(group[0].GroupKey, pinnedItem.GroupKey, StringComparison.OrdinalIgnoreCase))
            {
                return group;
            }
        }

        var scored = windowGroups
            .Where(group => !usedWindowGroupKeys.Contains(group[0].GroupKey))
            .Select(group => new { Group = group, Score = ScorePinnedToWindowGroup(pinnedItem, group) })
            .Where(candidate => candidate.Score >= 60)
            .OrderByDescending(candidate => candidate.Score)
            .ToArray();

        if (scored.Length == 0)
        {
            return null;
        }

        if (scored.Length > 1 && scored[0].Score == scored[1].Score)
        {
            return null;
        }

        return scored[0].Group;
    }

    private static TaskbarItem[]? FindMatchingWindowGroup(
        IReadOnlyList<TaskbarItem[]> windowGroups,
        ExplorerTaskbarButtonItem button,
        HashSet<string> usedWindowGroupKeys)
    {
        var scored = windowGroups
            .Where(group => !usedWindowGroupKeys.Contains(group[0].GroupKey))
            .Select(group => new { Group = group, Score = ScoreButton(group, button) })
            .Where(candidate => candidate.Score >= 60)
            .OrderByDescending(candidate => candidate.Score)
            .ToArray();

        if (scored.Length == 0)
        {
            return null;
        }

        if (scored.Length > 1 && scored[0].Score == scored[1].Score)
        {
            DebugLogger.WriteIfChanged(
                $"taskbar-explorer-order-ambiguous-{button.RuntimeId}",
                "Explorer taskbar order match skipped because multiple groups scored equally: " +
                $"Button={button.Name} Score={scored[0].Score} Groups={string.Join(" | ", scored.Take(4).Select(candidate => candidate.Group[0].GroupKey))}");
            return null;
        }

        return scored[0].Group;
    }

    private static int ScorePinnedToWindowGroup(TaskbarItem pinnedItem, IReadOnlyList<TaskbarItem> groupItems)
    {
        var pinnedTitle = Normalize(pinnedItem.Title);
        var pinnedProcessName = Normalize(Path.GetFileNameWithoutExtension(pinnedItem.ProcessName));
        var pinnedPath = NormalizePathForCompare(pinnedItem.ProcessPath);
        var best = 0;

        foreach (var item in groupItems)
        {
            var itemPath = NormalizePathForCompare(item.ProcessPath);
            if (pinnedPath.Length > 0 && string.Equals(pinnedPath, itemPath, StringComparison.OrdinalIgnoreCase))
            {
                best = Math.Max(best, 130);
            }

            var itemProcessName = Normalize(Path.GetFileNameWithoutExtension(item.ProcessName));
            if (pinnedProcessName.Length >= 3 &&
                itemProcessName.Length >= 3 &&
                string.Equals(pinnedProcessName, itemProcessName, StringComparison.OrdinalIgnoreCase))
            {
                best = Math.Max(best, 110);
            }

            var itemTitle = Normalize(item.Title);
            if (pinnedTitle.Length >= 4 && itemTitle.Length >= 4)
            {
                if (itemTitle.Equals(pinnedTitle, StringComparison.Ordinal))
                {
                    best = Math.Max(best, 105);
                }
                else if (itemTitle.Contains(pinnedTitle) || pinnedTitle.Contains(itemTitle))
                {
                    best = Math.Max(best, 85);
                }
            }

            if (pinnedTitle.Length >= 4 && itemProcessName.Length >= 4 && pinnedTitle.Contains(itemProcessName))
            {
                best = Math.Max(best, 75);
            }

            if (itemTitle.Length >= 4 && pinnedProcessName.Length >= 4 && itemTitle.Contains(pinnedProcessName))
            {
                best = Math.Max(best, 75);
            }

            foreach (var alias in GetProcessAliases(itemProcessName))
            {
                if (pinnedTitle.Contains(alias))
                {
                    best = Math.Max(best, 90);
                }
            }

            foreach (var alias in GetProcessAliases(pinnedProcessName))
            {
                if (itemTitle.Contains(alias))
                {
                    best = Math.Max(best, 90);
                }
            }
        }

        return best;
    }

    public static bool TryForwardClick(IReadOnlyList<TaskbarItem> groupItems, bool rightClick, int anchorX, int anchorY)
    {
        if ((!AppSettingsService.Current.EnableExperimentalExplorerTaskbarHook &&
             !AppSettingsService.Current.EnableExperimentalExplorerTaskbarMenuProxy) ||
            groupItems.Count == 0)
        {
            return false;
        }

        ExplorerTaskbarButtonItem[] buttons;
        lock (Sync)
        {
            buttons = Buttons.ToArray();
        }

        var match = FindUniqueMatch(groupItems, buttons);
        if (match is null)
        {
            DebugLogger.WriteIfChanged(
                $"explorer-taskbar-proxy-no-match-{BuildGroupKey(groupItems)}",
                "Explorer taskbar proxy skipped: " +
                $"Group={BuildGroupKey(groupItems)} Titles={string.Join(" | ", groupItems.Select(item => item.Title))} " +
                $"Processes={string.Join(" | ", groupItems.Select(item => item.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase))} " +
                $"Buttons={string.Join(" | ", buttons.Take(16).Select(button => button.Name))}");
            return false;
        }

        var groupKey = BuildGroupKey(groupItems);
        if (!rightClick)
        {
            if (!AppSettingsService.Current.EnableExperimentalExplorerTaskbarHook)
            {
                return false;
            }

            if (TrySendTaskbarButtonCommand(match, "ActivateTaskbarButton", anchorX, anchorY, out var activateDetail, responseTimeoutMs: 1200))
            {
                DebugLogger.WriteIfChanged(
                    $"explorer-taskbar-activate-sent-{groupKey}",
                    "Explorer taskbar activate proxy handled: " +
                    $"Group={groupKey} Button={match.Name} RuntimeId={match.RuntimeId} Root=0x{match.RootHwnd.ToInt64():X} Detail={activateDetail}");
                return true;
            }

            DebugLogger.WriteIfChanged(
                $"explorer-taskbar-activate-failed-{groupKey}",
                "Explorer taskbar activate proxy failed; falling back to direct window activation: " +
                $"Group={groupKey} Button={match.Name} Error={activateDetail}");
            RequestProxyRefresh($"activate failed for {groupKey}: {activateDetail}");
            return false;
        }

        if (!AppSettingsService.Current.EnableExperimentalExplorerTaskbarMenuProxy)
        {
            DebugLogger.WriteIfChanged(
                $"explorer-taskbar-context-menu-disabled-{groupKey}",
                "Explorer taskbar context-menu proxy disabled by setting: " +
                $"Group={BuildGroupKey(groupItems)} Button={match.Name}");
            return false;
        }

        if (!TrySendTaskbarButtonCommand(match, "ShowTaskbarContextMenu", anchorX, anchorY, out var contextError))
        {
            DebugLogger.WriteIfChanged(
                $"explorer-taskbar-context-menu-send-failed-{groupKey}",
                "Explorer taskbar context-menu proxy send failed: " +
                $"Group={groupKey} Button={match.Name} Anchor={anchorX},{anchorY} Error={contextError}");
            RequestProxyRefresh($"context menu failed for {groupKey}: {contextError}");
            return false;
        }

        DebugLogger.WriteIfChanged(
            $"explorer-taskbar-context-menu-sent-{groupKey}",
            "Explorer taskbar context-menu proxy sent: " +
            $"Group={groupKey} Button={match.Name} RuntimeId={match.RuntimeId} Root=0x{match.RootHwnd.ToInt64():X} Anchor={anchorX},{anchorY} Detail={contextError}");
        return true;
    }

    public static void InvalidateButtonImage(IReadOnlyList<TaskbarItem> groupItems, string reason)
    {
        if (groupItems.Count == 0)
        {
            return;
        }

        var groupKey = BuildGroupKey(groupItems);
        ExplorerTaskbarButtonItem[] buttons;
        lock (Sync)
        {
            buttons = Buttons.ToArray();
        }

        var match = FindUniqueMatch(groupItems, buttons);
        if (match?.ButtonIconPngBytes is null || match.ButtonIconPngBytes.Length == 0)
        {
            return;
        }

        var fingerprint = GetButtonImageFingerprint(match);
        lock (Sync)
        {
            InvalidatedImageFingerprintsByGroupKey[groupKey] = fingerprint;
        }

        DebugLogger.WriteIfChanged(
            $"explorer-button-image-invalidated-{groupKey}",
            "Explorer taskbar button image invalidated: " +
            $"Group={groupKey} Button={match.Name} Fingerprint={fingerprint} Reason={reason}");
    }

    public static bool TryGetButtonImage(IReadOnlyList<TaskbarItem> groupItems, out ExplorerTaskbarButtonImage image)
    {
        image = default;
        if (!AppSettingsService.Current.EnableExperimentalExplorerTaskbarHook || groupItems.Count == 0)
        {
            return false;
        }

        ExplorerTaskbarButtonItem[] buttons;
        lock (Sync)
        {
            buttons = Buttons.ToArray();
        }

        var match = FindUniqueMatch(groupItems, buttons);
        if (match?.ButtonIconPngBytes is null || match.ButtonIconPngBytes.Length == 0)
        {
            return false;
        }

        var fingerprint = GetButtonImageFingerprint(match);
        var groupKey = BuildGroupKey(groupItems);
        lock (Sync)
        {
            if (InvalidatedImageFingerprintsByGroupKey.TryGetValue(groupKey, out var invalidatedFingerprint))
            {
                if (string.Equals(invalidatedFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    DebugLogger.WriteIfChanged(
                        $"explorer-button-image-skipped-invalidated-{groupKey}",
                        "Explorer taskbar button image skipped because it was invalidated by app taskbar state: " +
                        $"Group={groupKey} Button={match.Name} Fingerprint={fingerprint}");
                    return false;
                }

                InvalidatedImageFingerprintsByGroupKey.Remove(groupKey);
            }
        }

        if (match.ButtonIconPngBytes.Length < MinimumUsableButtonImageBytes)
        {
            DebugLogger.WriteIfChanged(
                $"explorer-button-image-rejected-{groupKey}",
                "Explorer taskbar button image rejected because it is too small to be a real icon capture: " +
                $"Group={groupKey} Button={match.Name} Bytes={match.ButtonIconPngBytes.Length} Fingerprint={match.ButtonIconFingerprint}");
            return false;
        }

        image = new ExplorerTaskbarButtonImage(
            match.ButtonIconPngBytes,
            fingerprint,
            match.Name);
        return true;
    }

    private static string GetButtonImageFingerprint(ExplorerTaskbarButtonItem button) =>
        string.IsNullOrWhiteSpace(button.ButtonIconFingerprint)
            ? button.RuntimeId
            : button.ButtonIconFingerprint;

    private static ExplorerTaskbarButtonItem? FindUniqueMatch(
        IReadOnlyList<TaskbarItem> groupItems,
        IReadOnlyList<ExplorerTaskbarButtonItem> buttons)
    {
        var scored = buttons
            .Select(button => new ScoredExplorerButton(button, ScoreButton(groupItems, button)))
            .Where(scoredButton => scoredButton.Score >= 60)
            .OrderByDescending(scoredButton => scoredButton.Score)
            .ToArray();

        if (scored.Length == 0)
        {
            return null;
        }

        if (scored.Length > 1 && scored[0].Score == scored[1].Score)
        {
            return null;
        }

        return scored[0].Button;
    }

    private static int ScoreButton(IReadOnlyList<TaskbarItem> groupItems, ExplorerTaskbarButtonItem button)
    {
        var buttonName = Normalize(CleanTaskbarButtonTitle(button.Name));
        var rawButtonName = Normalize(button.Name);
        if (buttonName.Length == 0 && rawButtonName.Length == 0)
        {
            return 0;
        }

        var buttonAppId = GetAutomationAppId(button.AutomationId);
        var normalizedButtonAppId = Normalize(buttonAppId);
        var buttonProcessPath = ResolveExecutablePathFromAppId(buttonAppId);
        var normalizedButtonProcessPath = NormalizePathForCompare(buttonProcessPath);
        var best = 0;
        foreach (var item in groupItems)
        {
            var itemPath = NormalizePathForCompare(item.ProcessPath);
            if (normalizedButtonProcessPath.Length > 0 &&
                itemPath.Length > 0 &&
                string.Equals(normalizedButtonProcessPath, itemPath, StringComparison.OrdinalIgnoreCase))
            {
                best = Math.Max(best, 140);
            }

            var itemAppId = Normalize(item.AppUserModelId);
            if (normalizedButtonAppId.Length > 0 &&
                itemAppId.Length > 0 &&
                string.Equals(normalizedButtonAppId, itemAppId, StringComparison.Ordinal))
            {
                best = Math.Max(best, 135);
            }

            var title = Normalize(item.Title);
            if (title.Length >= 6)
            {
                if (buttonName.Equals(title, StringComparison.Ordinal) ||
                    rawButtonName.Equals(title, StringComparison.Ordinal))
                {
                    best = Math.Max(best, 120);
                }
                else if (buttonName.Contains(title) || rawButtonName.Contains(title))
                {
                    best = Math.Max(best, 100);
                }
                else if (title.Contains(buttonName) && buttonName.Length >= 6)
                {
                    best = Math.Max(best, 80);
                }
            }

            var processName = Normalize(Path.GetFileNameWithoutExtension(item.ProcessName));
            if (processName.Length >= 4 &&
                (buttonName.Contains(processName) || rawButtonName.Contains(processName)))
            {
                best = Math.Max(best, 75);
            }

            foreach (var alias in GetProcessAliases(processName))
            {
                if (buttonName.Contains(alias) || rawButtonName.Contains(alias))
                {
                    best = Math.Max(best, 85);
                }
            }

            foreach (var token in SplitTokens(item.AppUserModelId))
            {
                if (buttonName.Contains(token) ||
                    rawButtonName.Contains(token) ||
                    normalizedButtonAppId.Contains(token))
                {
                    best = Math.Max(best, 65);
                }
            }

            foreach (var token in SplitTokens(buttonAppId))
            {
                if (title.Contains(token) || processName.Contains(token) || itemAppId.Contains(token))
                {
                    best = Math.Max(best, 65);
                }
            }
        }

        return best;
    }

    private static IEnumerable<string> GetProcessAliases(string processName)
    {
        switch (processName)
        {
            case "devenv":
                yield return "visual studio";
                break;
            case "code":
                yield return "visual studio code";
                break;
            case "explorer":
                yield return "file explorer";
                break;
            case "msedge":
                yield return "edge";
                yield return "microsoft edge";
                break;
            case "chrome":
                yield return "chrome";
                yield return "google chrome";
                break;
            case "discord":
                yield return "discord";
                break;
            case "mailbird":
                yield return "mailbird";
                break;
            case "processlasso":
                yield return "process lasso";
                break;
        }
    }

    private static IEnumerable<string> SplitTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var token in value.Split(['.', '!', '_', '-', ' ', '\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = Normalize(token);
            if (normalized.Length >= 4)
            {
                yield return normalized;
            }
        }
    }

    private static string Normalize(string value) =>
        string.Join(
            " ",
            value.Trim()
                .ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizePathForCompare(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).TrimEnd('\\');
        }
        catch
        {
            return path.Trim();
        }
    }

    private static bool IsPinnedButton(ExplorerTaskbarButtonItem button)
    {
        var name = Normalize(button.Name);
        return name.EndsWith(" pinned", StringComparison.Ordinal);
    }

    private static string CleanTaskbarButtonTitle(string name)
    {
        var title = name.Trim();
        const string pinnedSuffix = " pinned";
        title = title.EndsWith(pinnedSuffix, StringComparison.OrdinalIgnoreCase)
            ? title[..^pinnedSuffix.Length].Trim()
            : title;

        var runningIndex = title.LastIndexOf(" running window", StringComparison.OrdinalIgnoreCase);
        if (runningIndex > 0)
        {
            var separatorIndex = title.LastIndexOf(" - ", runningIndex, StringComparison.OrdinalIgnoreCase);
            if (separatorIndex > 0)
            {
                title = title[..separatorIndex].Trim();
            }
        }

        return title;
    }

    private static bool TryCreatePinnedItem(ExplorerTaskbarButtonItem button, out TaskbarItem item)
    {
        item = default!;
        var appId = GetAutomationAppId(button.AutomationId);
        var title = CleanPinnedTitle(button.Name);
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var shortcutItem = FindMatchingPinnedShortcut(title);
        var processPath = ResolveExecutablePathFromAppId(appId);
        if (string.IsNullOrWhiteSpace(processPath))
        {
            processPath = shortcutItem?.ProcessPath ?? "";
        }

        var processName = GetProcessName(processPath, appId, title);
        if (string.Equals(processName, title, StringComparison.OrdinalIgnoreCase) &&
            shortcutItem is not null &&
            !string.IsNullOrWhiteSpace(shortcutItem.ProcessName))
        {
            processName = shortcutItem.ProcessName;
        }

        var groupKey = GetPinnedGroupKey(appId, processPath, processName);
        var icon = CreatePinnedImageSource(button.ButtonIconPngBytes) ?? shortcutItem?.Icon;
        var fingerprint = string.IsNullOrWhiteSpace(button.ButtonIconFingerprint)
            ? button.RuntimeId
            : button.ButtonIconFingerprint;

        item = new TaskbarItem(
            IntPtr.Zero,
            title,
            icon,
            fingerprint,
            IsActive: false,
            IsMinimized: false,
            MonitorDeviceName: "",
            ProcessId: 0,
            processName,
            processPath,
            appId,
            groupKey,
            shortcutItem?.IconPath ?? processPath,
            shortcutItem?.LaunchPath ?? processPath,
            shortcutItem?.LaunchArguments ?? "",
            shortcutItem?.LaunchWorkingDirectory ?? "");
        return true;
    }

    private static TaskbarItem? FindMatchingPinnedShortcut(string title)
    {
        var normalizedTitle = Normalize(title);
        return PinnedTaskbarShortcutProvider.GetPinnedItems()
            .FirstOrDefault(item => string.Equals(Normalize(item.Title), normalizedTitle, StringComparison.Ordinal));
    }

    private static string GetAutomationAppId(string automationId)
    {
        const string prefix = "Appid:";
        return automationId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? automationId[prefix.Length..].Trim()
            : "";
    }

    private static string CleanPinnedTitle(string name)
    {
        return CleanTaskbarButtonTitle(name);
    }

    private static string GetPinnedGroupKey(string appId, string processPath, string processName)
    {
        if (!string.IsNullOrWhiteSpace(processPath) && IsPathLikeAppId(appId))
        {
            return "path:" + processPath.ToUpperInvariant();
        }

        if (!string.IsNullOrWhiteSpace(appId))
        {
            return "appid:" + appId.ToUpperInvariant();
        }

        return "process:" + processName.ToUpperInvariant();
    }

    private static string GetProcessName(string processPath, string appId, string title)
    {
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return Path.GetFileNameWithoutExtension(processPath);
        }

        return Normalize(appId) switch
        {
            "microsoft windows explorer" => "explorer",
            "msedge" => "msedge",
            "chrome" => "chrome",
            "valve steam client" => "steam",
            _ => title
        };
    }

    private static string ResolveExecutablePathFromAppId(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return "";
        }

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.GetFolderPath(Environment.SpecialFolder.System)
        };

        foreach (var replacement in replacements)
        {
            if (appId.StartsWith(replacement.Key, StringComparison.OrdinalIgnoreCase))
            {
                return replacement.Value + appId[replacement.Key.Length..];
            }
        }

        if (Path.IsPathRooted(appId))
        {
            return appId;
        }

        return Normalize(appId) switch
        {
            "microsoft windows explorer" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            "msedge" => FirstExistingPath(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")),
            "chrome" => FirstExistingPath(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe")),
            "valve steam client" => FirstExistingPath(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steam.exe")),
            _ => ""
        };
    }

    private static bool IsPathLikeAppId(string appId) =>
        !string.IsNullOrWhiteSpace(appId) &&
        (Path.IsPathRooted(appId) || appId.StartsWith("{", StringComparison.Ordinal));

    private static string FirstExistingPath(params string[] paths) =>
        paths.FirstOrDefault(File.Exists) ?? paths.FirstOrDefault() ?? "";

    private static ImageSource? CreatePinnedImageSource(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildSignature(
        ExplorerTaskbarSnapshotMessage message,
        IReadOnlyList<ExplorerTaskbarButtonItem> buttons) =>
        $"{message.Error}|{string.Join("|", buttons.Select(button => $"{button.RuntimeId}:{button.Name}:{button.Left},{button.Top},{button.Right},{button.Bottom}:{button.ButtonIconFingerprint}"))}";

    private static string FormatButtonForLog(ExplorerTaskbarButtonItem button) =>
        $"{button.Name} [{button.ControlType}/{button.ClassName}/{button.AutomationId}] Rect={button.Left},{button.Top},{button.Right},{button.Bottom} ExplorerIcon={button.ButtonIconPngBytes is { Length: > 0 }}";

    private static string FormatRawButtonForLog(ExplorerTaskbarButtonSnapshotMessage button) =>
        $"{button.Name} [{button.ControlType}/{button.ClassName}/{button.AutomationId}] Rect={button.Left},{button.Top},{button.Right},{button.Bottom} ExplorerIcon={!string.IsNullOrWhiteSpace(button.ButtonIconPngBase64)}";

    private static bool IsUsableTaskbarButton(ExplorerTaskbarButtonSnapshotMessage button)
    {
        var controlType = button.ControlType ?? "";
        var className = button.ClassName ?? "";
        var name = button.Name ?? "";

        if (!string.Equals(controlType, "ControlType.Button", StringComparison.Ordinal) &&
            !string.Equals(controlType, "ControlType.ListItem", StringComparison.Ordinal) &&
            !string.Equals(controlType, "ControlType.MenuItem", StringComparison.Ordinal))
        {
            return false;
        }

        if (className.StartsWith("SystemTray.", StringComparison.Ordinal) ||
            className.IndexOf("Tray", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        return !name.StartsWith("Running applications", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildGroupKey(IReadOnlyList<TaskbarItem> groupItems) =>
        groupItems[0].GroupKey;

    private static bool TryPostTaskbarLeftClick(ExplorerTaskbarButtonItem button, out string detail)
    {
        var screenX = (button.Left + button.Right) / 2;
        var screenY = (button.Top + button.Bottom) / 2;
        var point = new NativeMethods.Point
        {
            X = screenX,
            Y = screenY
        };

        if (!NativeMethods.ScreenToClient(button.RootHwnd, ref point))
        {
            detail = $"ScreenPoint={screenX},{screenY} ScreenToClientLastError={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
            return false;
        }

        var lParam = MakeLParam(point.X, point.Y);
        var down = NativeMethods.PostMessage(
            button.RootHwnd,
            NativeMethods.WM_LBUTTONDOWN,
            new IntPtr(NativeMethods.MK_LBUTTON),
            lParam);
        var up = NativeMethods.PostMessage(
            button.RootHwnd,
            NativeMethods.WM_LBUTTONUP,
            IntPtr.Zero,
            lParam);
        detail = $"ScreenPoint={screenX},{screenY} ClientPoint={point.X},{point.Y} Down={down} Up={up} LastError={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
        return down && up;
    }

    private static IntPtr MakeLParam(int x, int y) =>
        new(unchecked((int)(((ushort)y << 16) | (ushort)x)));

    private static bool TrySendTaskbarButtonCommand(
        ExplorerTaskbarButtonItem button,
        string operation,
        int anchorX,
        int anchorY,
        out string error,
        int responseTimeoutMs = 1800)
    {
        error = "";
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                GetCommandPipeName(),
                PipeDirection.InOut,
                PipeOptions.None);
            pipe.Connect(250);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
            {
                AutoFlush = true
            };
            writer.WriteLine(JsonSerializer.Serialize(new ExplorerTaskbarMenuCommand
            {
                ProtocolVersion = 1,
                Operation = operation,
                RuntimeId = button.RuntimeId,
                Name = button.Name,
                RootHwnd = button.RootHwnd.ToInt64(),
                NativeWindowHandle = button.NativeWindowHandle,
                Left = button.Left,
                Top = button.Top,
                Right = button.Right,
                Bottom = button.Bottom,
                AnchorX = anchorX,
                AnchorY = anchorY
            }));

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var responseTask = reader.ReadLineAsync();
            if (!responseTask.Wait(responseTimeoutMs))
            {
                error = "Timed out waiting for Explorer taskbar command response.";
                return false;
            }

            var responseLine = responseTask.Result;
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                error = "Explorer taskbar command response was empty.";
                return false;
            }

            var response = JsonSerializer.Deserialize<ExplorerTaskbarCommandResponse>(responseLine);
            if (response is null)
            {
                error = $"Explorer taskbar command response was invalid: {responseLine}";
                return false;
            }

            error = response.Detail ?? "";
            return response.Handled;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException or JsonException or AggregateException)
        {
            error = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static void RequestProxyRefresh(string reason)
    {
        var shouldRefresh = false;
        lock (Sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastProxyRefreshRequested >= TimeSpan.FromSeconds(10))
            {
                _lastProxyRefreshRequested = now;
                Buttons = Array.Empty<ExplorerTaskbarButtonItem>();
                _lastSignature = "";
                shouldRefresh = true;
            }
        }

        if (!shouldRefresh)
        {
            return;
        }

        DebugLogger.Write($"Explorer taskbar proxy refresh requested: {reason}");
        AppCommands.RestartHooks();
        SnapshotChanged?.Invoke(null, EventArgs.Empty);
    }

    private static string GetCommandPipeName() =>
        $"TaskBar2.ExplorerTaskbarCommand.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    private static byte[]? DecodeBytes(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed record ScoredExplorerButton(ExplorerTaskbarButtonItem Button, int Score);
}

internal sealed record ExplorerTaskbarButtonItem(
    string RuntimeId,
    string Name,
    string ClassName,
    string AutomationId,
    string ControlType,
    int NativeWindowHandle,
    IntPtr RootHwnd,
    string RootClassName,
    int Left,
    int Top,
    int Right,
    int Bottom,
    byte[]? ButtonIconPngBytes,
    string ButtonIconFingerprint);

internal sealed class ExplorerTaskbarMenuCommand
{
    public int ProtocolVersion { get; set; }

    public string Operation { get; set; } = "";

    public string RuntimeId { get; set; } = "";

    public string Name { get; set; } = "";

    public long RootHwnd { get; set; }

    public int NativeWindowHandle { get; set; }

    public int Left { get; set; }

    public int Top { get; set; }

    public int Right { get; set; }

    public int Bottom { get; set; }

    public int AnchorX { get; set; }

    public int AnchorY { get; set; }
}

internal sealed class ExplorerTaskbarCommandResponse
{
    public bool Handled { get; set; }

    public string? Detail { get; set; }
}

internal readonly record struct ExplorerTaskbarButtonImage(
    byte[] PngBytes,
    string Fingerprint,
    string ButtonName);

internal sealed class ExplorerTaskbarSnapshotMessage
{
    public int ProtocolVersion { get; set; }

    public string? MessageType { get; set; }

    public int SourceProcessId { get; set; }

    public string? SourceProcessName { get; set; }

    public string? SourceProcessPath { get; set; }

    public string? CapturedAtUtc { get; set; }

    public string? Error { get; set; }

    public List<ExplorerTaskbarRootSnapshotMessage> Roots { get; set; } = [];

    public List<ExplorerTaskbarButtonSnapshotMessage> Buttons { get; set; } = [];
}

internal sealed class ExplorerTaskbarRootSnapshotMessage
{
    public long Hwnd { get; set; }

    public string? ClassName { get; set; }

    public int Left { get; set; }

    public int Top { get; set; }

    public int Right { get; set; }

    public int Bottom { get; set; }
}

internal sealed class ExplorerTaskbarButtonSnapshotMessage
{
    public string? RuntimeId { get; set; }

    public string? Name { get; set; }

    public string? ClassName { get; set; }

    public string? AutomationId { get; set; }

    public string? ControlType { get; set; }

    public int NativeWindowHandle { get; set; }

    public long RootHwnd { get; set; }

    public string? RootClassName { get; set; }

    public int Left { get; set; }

    public int Top { get; set; }

    public int Right { get; set; }

    public int Bottom { get; set; }

    public string? ButtonIconPngBase64 { get; set; }

    public string? ButtonIconFingerprint { get; set; }
}
