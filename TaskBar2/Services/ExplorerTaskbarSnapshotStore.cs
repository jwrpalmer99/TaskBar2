using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskBar2.Models;
using TaskBar2.Native;

namespace TaskBar2.Services;

internal static class ExplorerTaskbarSnapshotStore
{
    private const int MinimumUsableButtonImageBytes = 512;
    private const int AutomationMaxDepth = 14;
    private const int AutomationMaxNodes = 600;
    private const int AutomationMaxButtons = 96;
    private static readonly TimeSpan AutomationRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HookSnapshotFreshDuration = TimeSpan.FromSeconds(5);
    private static readonly string[] TaskbarRootClasses = ["Shell_TrayWnd", "Shell_SecondaryTrayWnd"];
    private static readonly string[] ExcludedNamePrefixes =
    [
        "Start",
        "Search",
        "Task view",
        "Widgets",
        "Copilot",
        "Show hidden icons",
        "Show desktop",
        "Notification center",
        "Quick settings",
        "Running applications",
        "Clock "
    ];

    private static readonly object Sync = new();
    private static IReadOnlyList<ExplorerTaskbarButtonItem> Buttons = Array.Empty<ExplorerTaskbarButtonItem>();
    private static readonly Dictionary<string, string> InvalidatedImageFingerprintsByGroupKey = [];
    private static readonly Dictionary<string, string> ResolvedExecutablePathByAppId = new(StringComparer.OrdinalIgnoreCase);
    private static string _lastSignature = "";
    private static string _lastAutomationSignature = "";
    private static DateTimeOffset _lastHookSnapshot = DateTimeOffset.MinValue;
    private static DateTimeOffset _lastAutomationRefreshAttempt = DateTimeOffset.MinValue;
    private static DateTimeOffset _lastProxyRefreshRequested = DateTimeOffset.MinValue;

    public static event EventHandler? SnapshotChanged;

    public static void Apply(ExplorerTaskbarSnapshotMessage message)
    {
        var buttons = SelectPrimaryTaskbarButtonSequence(message.Buttons
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
            .ToArray()).ToArray();

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
            if (buttons.Length > 0)
            {
                _lastHookSnapshot = DateTimeOffset.UtcNow;
            }
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

    public static IReadOnlyList<TaskbarItem> MergePinnedItems(
        IReadOnlyList<TaskbarItem> windowItems,
        bool includePinnedOnly = true)
    {
        if (TryMergeExplorerButtonOrder(windowItems, includePinnedOnly, out var explorerOrderedItems))
        {
            return explorerOrderedItems;
        }

        var pinnedItems = PinnedTaskbarShortcutProvider.GetPinnedItems();
        if (pinnedItems.Count == 0)
        {
            return windowItems;
        }

        var merged = MergePinnedOrder(windowItems, pinnedItems, includePinnedOnly);
        DebugLogger.WriteIfChanged(
            "taskbar-pinned-merge",
            "Taskbar pinned merge: " +
            $"Source=Shortcuts Windows={windowItems.Count} PinnedSlots={pinnedItems.Count} IncludePinnedOnly={includePinnedOnly} Output={merged.Count} " +
            $"Pinned={string.Join(" | ", pinnedItems.Take(24).Select(item => $"{item.Title}:{item.GroupKey}"))}");
        return merged;
    }

    private static bool TryMergeExplorerButtonOrder(
        IReadOnlyList<TaskbarItem> windowItems,
        bool includePinnedOnly,
        out IReadOnlyList<TaskbarItem> orderedItems)
    {
        orderedItems = Array.Empty<TaskbarItem>();
        if (!AppSettingsService.Current.EnableExperimentalExplorerTaskbarHook)
        {
            return false;
        }

        RefreshAutomationSnapshotIfHookIsStale();

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

        IReadOnlyDictionary<string, TaskbarItem>? pinnedShortcutsByTitle = null;
        foreach (var button in buttons)
        {
            var matchingGroup = IsPinnedOnlyButton(button)
                ? FindExactMatchingWindowGroup(windowGroups, button, usedWindowGroupKeys)
                : FindMatchingWindowGroup(windowGroups, button, usedWindowGroupKeys);
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

            if (includePinnedOnly &&
                IsPinnedButton(button) &&
                TryCreatePinnedItem(button, pinnedShortcutsByTitle ??= GetPinnedShortcutsByTitle(), out var pinnedItem) &&
                emittedGroupKeys.Add(pinnedItem.GroupKey))
            {
                merged.Add(pinnedItem);
                pinnedButtons++;
                continue;
            }

            if (IsPinnedButton(button))
            {
                DebugLogger.WriteIfChanged(
                    $"taskbar-explorer-pinned-skip-{button.RuntimeId}",
                    "Explorer pinned button was not emitted: " +
                    $"Button={button.Name} AppId={GetAutomationAppId(button.AutomationId)} " +
                    $"AlreadyEmitted={string.Join(" | ", emittedGroupKeys.Take(16))}");
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
            $"Windows={windowItems.Count} Buttons={buttons.Length} MatchedButtons={matchedButtons} PinnedButtons={pinnedButtons} IncludePinnedOnly={includePinnedOnly} Output={merged.Count} " +
            $"Order={string.Join(" | ", merged.Take(32).Select(item => $"{item.Title}:{item.GroupKey}"))}");
        return true;
    }

    private static IReadOnlyList<ExplorerTaskbarButtonItem> SelectPrimaryTaskbarButtonSequence(
        IReadOnlyList<ExplorerTaskbarButtonItem> buttons)
    {
        if (buttons.Count == 0)
        {
            return buttons;
        }

        var primaryGroup = buttons
            .GroupBy(button => button.RootHwnd)
            .Where(group => group.Any(button => string.Equals(button.RootClassName, "Shell_TrayWnd", StringComparison.Ordinal)))
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();
        var selectedGroup = primaryGroup ?? buttons
            .GroupBy(button => button.RootHwnd)
            .OrderByDescending(group => group.Count())
            .First();

        return selectedGroup
            .OrderBy(button => button.Left)
            .ThenBy(button => button.Top)
            .ThenBy(button => button.Right)
            .ToArray();
    }

    private static IReadOnlyList<TaskbarItem> MergePinnedOrder(
        IReadOnlyList<TaskbarItem> windowItems,
        IReadOnlyList<TaskbarItem> pinnedItems,
        bool includePinnedOnly)
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

            if (includePinnedOnly && emittedGroupKeys.Add(pinnedItem.GroupKey))
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

        TaskbarItem[]? bestGroup = null;
        var bestScore = 0;
        var tied = false;
        foreach (var group in windowGroups)
        {
            if (usedWindowGroupKeys.Contains(group[0].GroupKey))
            {
                continue;
            }

            var score = ScorePinnedToWindowGroup(pinnedItem, group);
            if (score < 60)
            {
                continue;
            }

            if (score > bestScore)
            {
                bestGroup = group;
                bestScore = score;
                tied = false;
            }
            else if (score == bestScore)
            {
                tied = true;
            }
        }

        return tied ? null : bestGroup;
    }

    private static TaskbarItem[]? FindMatchingWindowGroup(
        IReadOnlyList<TaskbarItem[]> windowGroups,
        ExplorerTaskbarButtonItem button,
        HashSet<string> usedWindowGroupKeys)
    {
        var context = CreateButtonScoreContext(button);
        TaskbarItem[]? bestGroup = null;
        var bestScore = 0;
        var tied = false;
        foreach (var group in windowGroups)
        {
            if (usedWindowGroupKeys.Contains(group[0].GroupKey))
            {
                continue;
            }

            var score = ScoreButton(group, context);
            if (score < 60)
            {
                continue;
            }

            if (score > bestScore)
            {
                bestGroup = group;
                bestScore = score;
                tied = false;
            }
            else if (score == bestScore)
            {
                tied = true;
            }
        }

        if (tied)
        {
            DebugLogger.WriteIfChanged(
                $"taskbar-explorer-order-ambiguous-{button.RuntimeId}",
                "Explorer taskbar order match skipped because multiple groups scored equally: " +
                $"Button={button.Name} Score={bestScore}");
            return null;
        }

        return bestGroup;
    }

    private static TaskbarItem[]? FindExactMatchingWindowGroup(
        IReadOnlyList<TaskbarItem[]> windowGroups,
        ExplorerTaskbarButtonItem button,
        HashSet<string> usedWindowGroupKeys)
    {
        var appId = GetAutomationAppId(button.AutomationId);
        var normalizedAppId = Normalize(appId);
        var normalizedPath = NormalizePathForCompare(ResolveExecutablePathFromAppId(appId));
        foreach (var group in windowGroups)
        {
            if (usedWindowGroupKeys.Contains(group[0].GroupKey))
            {
                continue;
            }

            foreach (var item in group)
            {
                var itemAppId = Normalize(item.AppUserModelId);
                if (normalizedAppId.Length > 0 &&
                    itemAppId.Length > 0 &&
                    string.Equals(normalizedAppId, itemAppId, StringComparison.Ordinal))
                {
                    return group;
                }

                var itemPath = NormalizePathForCompare(item.ProcessPath);
                if (normalizedPath.Length > 0 &&
                    itemPath.Length > 0 &&
                    string.Equals(normalizedPath, itemPath, StringComparison.OrdinalIgnoreCase))
                {
                    return group;
                }
            }
        }

        return null;
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
        if (rightClick ||
            !AppSettingsService.Current.EnableExperimentalExplorerTaskbarHook ||
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
        if (!AppSettingsService.Current.EnableExperimentalExplorerTaskbarHook ||
            !AppSettingsService.Current.EnableExperimentalExplorerTaskbarButtonImageCapture ||
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
        ExplorerTaskbarButtonItem? bestButton = null;
        var bestScore = 0;
        var tied = false;
        foreach (var button in buttons)
        {
            var score = ScoreButton(groupItems, CreateButtonScoreContext(button));
            if (score < 60)
            {
                continue;
            }

            if (score > bestScore)
            {
                bestButton = button;
                bestScore = score;
                tied = false;
            }
            else if (score == bestScore)
            {
                tied = true;
            }
        }

        return tied ? null : bestButton;
    }

    private static ButtonScoreContext CreateButtonScoreContext(ExplorerTaskbarButtonItem button)
    {
        var buttonName = Normalize(CleanTaskbarButtonTitle(button.Name));
        var rawButtonName = Normalize(button.Name);
        var buttonAppId = GetAutomationAppId(button.AutomationId);
        var normalizedButtonAppId = Normalize(buttonAppId);
        return new ButtonScoreContext(
            buttonName,
            rawButtonName,
            buttonAppId,
            normalizedButtonAppId,
            NormalizePathForCompare(ResolveExecutablePathFromAppId(buttonAppId)),
            SplitTokens(buttonAppId).ToArray());
    }

    private static int ScoreButton(IReadOnlyList<TaskbarItem> groupItems, ButtonScoreContext context)
    {
        if (context.ButtonName.Length == 0 && context.RawButtonName.Length == 0)
        {
            return 0;
        }

        var best = 0;
        foreach (var item in groupItems)
        {
            var itemPath = NormalizePathForCompare(item.ProcessPath);
            if (context.NormalizedButtonProcessPath.Length > 0 &&
                itemPath.Length > 0 &&
                string.Equals(context.NormalizedButtonProcessPath, itemPath, StringComparison.OrdinalIgnoreCase))
            {
                best = Math.Max(best, 140);
            }

            var itemAppId = Normalize(item.AppUserModelId);
            if (context.NormalizedButtonAppId.Length > 0 &&
                itemAppId.Length > 0 &&
                string.Equals(context.NormalizedButtonAppId, itemAppId, StringComparison.Ordinal))
            {
                best = Math.Max(best, 135);
            }

            var title = Normalize(item.Title);
            if (title.Length >= 6)
            {
                if (context.ButtonName.Equals(title, StringComparison.Ordinal) ||
                    context.RawButtonName.Equals(title, StringComparison.Ordinal))
                {
                    best = Math.Max(best, 120);
                }
                else if (context.ButtonName.Contains(title) || context.RawButtonName.Contains(title))
                {
                    best = Math.Max(best, 100);
                }
                else if (title.Contains(context.ButtonName) && context.ButtonName.Length >= 6)
                {
                    best = Math.Max(best, 80);
                }
            }

            var processName = Normalize(Path.GetFileNameWithoutExtension(item.ProcessName));
            if (processName.Length >= 4 &&
                (context.ButtonName.Contains(processName) || context.RawButtonName.Contains(processName)))
            {
                best = Math.Max(best, 75);
            }

            foreach (var alias in GetProcessAliases(processName))
            {
                if (context.ButtonName.Contains(alias) || context.RawButtonName.Contains(alias))
                {
                    best = Math.Max(best, 85);
                }
            }

            foreach (var token in SplitTokens(item.AppUserModelId))
            {
                if (context.ButtonName.Contains(token) ||
                    context.RawButtonName.Contains(token) ||
                    context.NormalizedButtonAppId.Contains(token))
                {
                    best = Math.Max(best, 65);
                }
            }

            foreach (var token in context.ButtonAppIdTokens)
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

    private static bool IsPinnedOnlyButton(ExplorerTaskbarButtonItem button)
    {
        var name = Normalize(button.Name);
        return name.EndsWith(" pinned", StringComparison.Ordinal) &&
               !name.Contains(" running window", StringComparison.Ordinal);
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

    private static bool TryCreatePinnedItem(
        ExplorerTaskbarButtonItem button,
        IReadOnlyDictionary<string, TaskbarItem> pinnedShortcutsByTitle,
        out TaskbarItem item)
    {
        item = default!;
        var appId = GetAutomationAppId(button.AutomationId);
        var title = CleanPinnedTitle(button.Name);
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        pinnedShortcutsByTitle.TryGetValue(Normalize(title), out var shortcutItem);
        var packageInfo = PackageAppResolver.Resolve(appId);
        var processPath = ResolveExecutablePathFromAppId(appId);
        if (string.IsNullOrWhiteSpace(processPath))
        {
            processPath = packageInfo?.ExecutablePath ?? shortcutItem?.ProcessPath ?? "";
        }

        var processName = GetProcessName(processPath, appId, title);
        if (string.Equals(processName, title, StringComparison.OrdinalIgnoreCase) &&
            shortcutItem is not null &&
            !string.IsNullOrWhiteSpace(shortcutItem.ProcessName))
        {
            processName = shortcutItem.ProcessName;
        }

        var groupKey = GetPinnedGroupKey(appId, processPath, processName);
        var iconPath = FirstNonEmpty(shortcutItem?.IconPath, packageInfo?.IconPath, processPath);
        var icon = AppSettingsService.Current.EnableExperimentalExplorerTaskbarButtonImageCapture
            ? CreatePinnedImageSource(button.ButtonIconPngBytes) ?? shortcutItem?.Icon ?? PackageAppResolver.CreateImageSource(packageInfo?.IconPath)
            : shortcutItem?.Icon ?? PackageAppResolver.CreateImageSource(packageInfo?.IconPath);
        var fingerprint = GetPinnedIconFingerprint(button, iconPath, appId);

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
            iconPath,
            shortcutItem?.LaunchPath ?? processPath,
            shortcutItem?.LaunchArguments ?? "",
            shortcutItem?.LaunchWorkingDirectory ?? "");
        return true;
    }

    private static string GetPinnedIconFingerprint(ExplorerTaskbarButtonItem button, string iconPath, string appId)
    {
        if (!string.IsNullOrWhiteSpace(button.ButtonIconFingerprint))
        {
            return button.ButtonIconFingerprint;
        }

        var fileFingerprint = PackageAppResolver.GetFileFingerprint(iconPath);
        if (!string.IsNullOrWhiteSpace(fileFingerprint))
        {
            return $"pin:{appId}:{fileFingerprint}";
        }

        return string.IsNullOrWhiteSpace(button.RuntimeId)
            ? $"pin:{appId}"
            : button.RuntimeId;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static IReadOnlyDictionary<string, TaskbarItem> GetPinnedShortcutsByTitle()
    {
        var result = new Dictionary<string, TaskbarItem>(StringComparer.Ordinal);
        foreach (var item in PinnedTaskbarShortcutProvider.GetPinnedItems())
        {
            result.TryAdd(Normalize(item.Title), item);
        }

        return result;
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

        lock (Sync)
        {
            if (ResolvedExecutablePathByAppId.TryGetValue(appId, out var cachedPath))
            {
                return cachedPath;
            }
        }

        var resolvedPath = ResolveExecutablePathFromAppIdUncached(appId);
        lock (Sync)
        {
            ResolvedExecutablePathByAppId[appId] = resolvedPath;
        }

        return resolvedPath;
    }

    private static string ResolveExecutablePathFromAppIdUncached(string appId)
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

        var packageInfo = PackageAppResolver.Resolve(appId);
        if (!string.IsNullOrWhiteSpace(packageInfo?.ExecutablePath))
        {
            return packageInfo.ExecutablePath;
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

    private static void RefreshAutomationSnapshotIfHookIsStale()
    {
        var now = DateTimeOffset.UtcNow;
        lock (Sync)
        {
            if (now - _lastHookSnapshot < HookSnapshotFreshDuration ||
                now - _lastAutomationRefreshAttempt < AutomationRefreshInterval)
            {
                return;
            }

            _lastAutomationRefreshAttempt = now;
        }

        var buttons = ReadAutomationTaskbarButtons();
        if (buttons.Count == 0)
        {
            DebugLogger.WriteIfChanged(
                "explorer-taskbar-automation-empty",
                "Explorer taskbar automation fallback found no usable app buttons.");
            return;
        }

        var signature = string.Join(
            "|",
            buttons.Select(button => $"{button.RootHwnd.ToInt64():X}:{button.RuntimeId}:{button.Name}:{button.Left},{button.Top},{button.Right},{button.Bottom}"));

        lock (Sync)
        {
            if (DateTimeOffset.UtcNow - _lastHookSnapshot < HookSnapshotFreshDuration ||
                string.Equals(signature, _lastAutomationSignature, StringComparison.Ordinal))
            {
                return;
            }

            Buttons = buttons;
            _lastAutomationSignature = signature;
        }

        DebugLogger.WriteIfChanged(
            "explorer-taskbar-automation-order",
            "Explorer taskbar automation fallback order: " +
            $"Buttons={buttons.Count} Items={string.Join(" || ", buttons.Take(24).Select(FormatButtonForLog))}");
    }

    private static IReadOnlyList<ExplorerTaskbarButtonItem> ReadAutomationTaskbarButtons()
    {
        try
        {
            var buttons = new List<ExplorerTaskbarButtonItem>();
            foreach (var root in FindTaskbarRootWindows())
            {
                AutomationElement? rootElement;
                try
                {
                    rootElement = AutomationElement.FromHandle(root.Hwnd);
                }
                catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
                {
                    continue;
                }

                if (rootElement is null)
                {
                    continue;
                }

                var visited = 0;
                CollectAutomationButtons(rootElement, root, buttons, depth: 0, ref visited);
                if (buttons.Count >= AutomationMaxButtons)
                {
                    break;
                }
            }

            return SelectPrimaryTaskbarButtonSequence(buttons);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            DebugLogger.WriteIfChanged(
                "explorer-taskbar-automation-error",
                $"Explorer taskbar automation fallback failed: {exception.GetType().Name}: {exception.Message}");
            return Array.Empty<ExplorerTaskbarButtonItem>();
        }
    }

    private static void CollectAutomationButtons(
        AutomationElement element,
        TaskbarRootWindow root,
        List<ExplorerTaskbarButtonItem> buttons,
        int depth,
        ref int visited)
    {
        if (depth > AutomationMaxDepth || visited >= AutomationMaxNodes || buttons.Count >= AutomationMaxButtons)
        {
            return;
        }

        visited++;
        TryAddAutomationButton(element, root, buttons);

        AutomationElement? child;
        try
        {
            child = TreeWalker.RawViewWalker.GetFirstChild(element);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return;
        }

        while (child is not null && visited < AutomationMaxNodes && buttons.Count < AutomationMaxButtons)
        {
            CollectAutomationButtons(child, root, buttons, depth + 1, ref visited);
            try
            {
                child = TreeWalker.RawViewWalker.GetNextSibling(child);
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                return;
            }
        }
    }

    private static void TryAddAutomationButton(
        AutomationElement element,
        TaskbarRootWindow root,
        List<ExplorerTaskbarButtonItem> buttons)
    {
        AutomationElement.AutomationElementInformation current;
        try
        {
            current = element.Current;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return;
        }

        if (!IsAutomationTaskbarButton(current))
        {
            return;
        }

        var rect = current.BoundingRectangle;
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        buttons.Add(new ExplorerTaskbarButtonItem(
            GetAutomationRuntimeId(element),
            current.Name ?? "",
            current.ClassName ?? "",
            current.AutomationId ?? "",
            current.ControlType?.ProgrammaticName ?? "",
            current.NativeWindowHandle,
            root.Hwnd,
            root.ClassName,
            (int)Math.Round(rect.Left),
            (int)Math.Round(rect.Top),
            (int)Math.Round(rect.Right),
            (int)Math.Round(rect.Bottom),
            null,
            ""));
    }

    private static bool IsAutomationTaskbarButton(AutomationElement.AutomationElementInformation current)
    {
        var controlType = current.ControlType;
        var className = current.ClassName ?? "";
        var automationId = current.AutomationId ?? "";
        var name = current.Name ?? "";

        if (className.StartsWith("SystemTray.", StringComparison.Ordinal) ||
            className.IndexOf("Tray", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        if (ExcludedNamePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (controlType != ControlType.Button &&
            controlType != ControlType.ListItem &&
            controlType != ControlType.MenuItem)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(name) ||
               className.IndexOf("Task", StringComparison.OrdinalIgnoreCase) >= 0 ||
               automationId.IndexOf("Task", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static IReadOnlyList<TaskbarRootWindow> FindTaskbarRootWindows()
    {
        var roots = new List<TaskbarRootWindow>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var className = GetWindowClassName(hwnd);
            if (!TaskbarRootClasses.Contains(className, StringComparer.Ordinal))
            {
                return true;
            }

            NativeMethods.GetWindowRect(hwnd, out var rect);
            roots.Add(new TaskbarRootWindow(hwnd, className, rect.Left, rect.Top, rect.Right, rect.Bottom));
            return true;
        }, IntPtr.Zero);

        return roots;
    }

    private static string GetAutomationRuntimeId(AutomationElement element)
    {
        try
        {
            return string.Join(".", element.GetRuntimeId() ?? Array.Empty<int>());
        }
        catch (ElementNotAvailableException)
        {
            return "";
        }
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var builder = new StringBuilder(256);
        return NativeMethods.GetClassName(hwnd, builder, builder.Capacity) > 0
            ? builder.ToString()
            : "";
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

    private sealed record ButtonScoreContext(
        string ButtonName,
        string RawButtonName,
        string ButtonAppId,
        string NormalizedButtonAppId,
        string NormalizedButtonProcessPath,
        string[] ButtonAppIdTokens);
}

internal sealed record TaskbarRootWindow(
    IntPtr Hwnd,
    string ClassName,
    int Left,
    int Top,
    int Right,
    int Bottom);

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
