using Microsoft.Win32;
using System.IO;

namespace TaskBar2.Services;

internal static class TrayIconVisibilityService
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, bool> PromotedByPathAndToolTip = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PromotionAggregate> PromotedByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PromotionAggregate> PromotedByProcessName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PromotionAggregate> PromotedByToolTip = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    public static bool IsShownByWindowsSettings(string? executablePath, string? processName, string? toolTip) =>
        GetVisibilityDecision(executablePath, processName, toolTip).IsShown;

    public static TrayIconVisibilityDecision GetVisibilityDecision(string? executablePath, string? processName, string? toolTip)
    {
        RefreshIfNeeded();

        lock (Sync)
        {
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                var normalizedPath = NormalizePath(executablePath);
                var normalizedToolTip = NormalizeToolTip(toolTip ?? "");
                if (!string.IsNullOrWhiteSpace(normalizedToolTip) &&
                    PromotedByPathAndToolTip.TryGetValue(BuildPathToolTipKey(normalizedPath, normalizedToolTip), out var promotedByToolTip))
                {
                    return new TrayIconVisibilityDecision(
                        promotedByToolTip,
                        "ExactPathAndTooltip",
                        promotedByToolTip,
                        normalizedPath,
                        normalizedToolTip);
                }

                if (PromotedByPath.TryGetValue(normalizedPath, out var promotedByPath))
                {
                    return new TrayIconVisibilityDecision(
                        promotedByPath.AnyPromoted,
                        "PathAnyPromoted",
                        promotedByPath.AnyPromoted,
                        normalizedPath,
                        "");
                }
            }

            var normalizedProcessName = Path.GetFileNameWithoutExtension(processName ?? "");
            if (!string.IsNullOrWhiteSpace(normalizedProcessName) &&
                PromotedByProcessName.TryGetValue(normalizedProcessName, out var promotedByName))
            {
                return new TrayIconVisibilityDecision(
                    promotedByName.AnyPromoted,
                    "ProcessAnyPromoted",
                    promotedByName.AnyPromoted,
                    normalizedProcessName,
                    "");
            }

            var toolTipOnly = NormalizeToolTip(toolTip ?? "");
            if (!string.IsNullOrWhiteSpace(toolTipOnly) &&
                PromotedByToolTip.TryGetValue(toolTipOnly, out var promotedByToolTipOnly))
            {
                return new TrayIconVisibilityDecision(
                    promotedByToolTipOnly.AnyPromoted,
                    "ToolTipAnyPromoted",
                    promotedByToolTipOnly.AnyPromoted,
                    "",
                    toolTipOnly);
            }
        }

        return new TrayIconVisibilityDecision(
            false,
            "NoRegistryMatch_DefaultHidden",
            null,
            "",
            "");
    }

    public static void RefreshNow()
    {
        lock (Sync)
        {
            RefreshLocked();
        }
    }

    private static void RefreshIfNeeded()
    {
        lock (Sync)
        {
            if (DateTimeOffset.UtcNow - _lastRefresh < TimeSpan.FromSeconds(10))
            {
                return;
            }

            RefreshLocked();
        }
    }

    private static void RefreshLocked()
    {
        PromotedByPath.Clear();
        PromotedByPathAndToolTip.Clear();
        PromotedByProcessName.Clear();
        PromotedByToolTip.Clear();
        _lastRefresh = DateTimeOffset.UtcNow;

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root is null)
            {
                return;
            }

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var subKey = root.OpenSubKey(subKeyName);
                var rawPath = subKey?.GetValue("ExecutablePath") as string;
                var initialToolTip = subKey?.GetValue("InitialToolTip") as string;
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    continue;
                }

                var isPromoted = ReadIsPromoted(subKey);
                var path = NormalizePath(ResolveKnownFolderPath(rawPath));
                AddPromotion(PromotedByPath, path, isPromoted);
                if (!string.IsNullOrWhiteSpace(initialToolTip))
                {
                    PromotedByPathAndToolTip[BuildPathToolTipKey(path, initialToolTip)] = isPromoted;
                    AddPromotion(PromotedByToolTip, NormalizeToolTip(initialToolTip), isPromoted);
                }

                var processName = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(processName))
                {
                    AddPromotion(PromotedByProcessName, processName, isPromoted);
                }

                DebugLogger.WriteIfChanged(
                    $"tray-visibility-registry-{subKeyName}",
                    "Tray visibility registry entry: " +
                    $"SubKey={subKeyName} IsPromoted={isPromoted} " +
                    $"RawPath={rawPath} ResolvedPath={path} Process={processName} " +
                    $"InitialToolTip={NormalizeForLog(initialToolTip ?? "")}");
            }

            DebugLogger.WriteIfChanged(
                "tray-visibility-settings",
                $"Tray visibility settings loaded: Icons={PromotedByPathAndToolTip.Count} Paths={PromotedByPath.Count} ProcessNames={PromotedByProcessName.Count} ToolTips={PromotedByToolTip.Count}");
        }
        catch (Exception exception)
        {
            DebugLogger.WriteIfChanged(
                "tray-visibility-settings-error",
                $"Tray visibility settings read failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static bool ReadIsPromoted(RegistryKey? subKey)
    {
        var value = subKey?.GetValue("IsPromoted");
        return value switch
        {
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed != 0,
            _ => false
        };
    }

    private static void AddPromotion(Dictionary<string, PromotionAggregate> dictionary, string key, bool isPromoted)
    {
        if (!dictionary.TryGetValue(key, out var aggregate))
        {
            aggregate = new PromotionAggregate();
            dictionary[key] = aggregate;
        }

        aggregate.Add(isPromoted);
    }

    private static string BuildPathToolTipKey(string path, string toolTip) =>
        NormalizePath(path) + "\n" + NormalizeToolTip(toolTip);

    private static string NormalizeToolTip(string toolTip) =>
        string.Join(
            "\n",
            toolTip
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));

    private static string NormalizeForLog(string value) =>
        NormalizeToolTip(value).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string ResolveKnownFolderPath(string path)
    {
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.GetFolderPath(Environment.SpecialFolder.System)
        };

        foreach (var replacement in replacements)
        {
            if (path.StartsWith(replacement.Key, StringComparison.OrdinalIgnoreCase))
            {
                return replacement.Value + path[replacement.Key.Length..];
            }
        }

        return path;
    }

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

    private sealed class PromotionAggregate
    {
        public bool AnyPromoted { get; private set; }

        public void Add(bool isPromoted)
        {
            AnyPromoted |= isPromoted;
        }
    }
}

internal sealed record TrayIconVisibilityDecision(
    bool IsShown,
    string MatchType,
    bool? IsPromoted,
    string MatchedKey,
    string MatchedToolTip);
