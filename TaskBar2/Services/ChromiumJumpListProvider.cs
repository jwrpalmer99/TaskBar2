using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using TaskBar2.Models;

namespace TaskBar2.Services;

internal static class ChromiumJumpListProvider
{
    private const int MaxEntriesPerSection = 8;

    public static bool TryGetSections(
        IReadOnlyList<TaskbarItem> items,
        out IReadOnlyList<JumpListMenuSection> sections)
    {
        sections = Array.Empty<JumpListMenuSection>();
        if (items.Count == 0 || !TryGetBrowser(items[0], out var browser))
        {
            return false;
        }

        var profiles = GetProfileDirectories(browser.ProfileRoot).ToArray();
        var recentlyClosed = GetRecentlyClosed(browser, profiles);
        var mostVisited = GetMostVisited(browser, profiles);

        var result = new List<JumpListMenuSection>();
        if (mostVisited.Count > 0)
        {
            result.Add(new JumpListMenuSection("Most visited", mostVisited));
        }

        if (recentlyClosed.Count > 0)
        {
            result.Add(new JumpListMenuSection("Recently closed", recentlyClosed));
        }

        sections = result;
        DebugLogger.WriteIfChanged(
            $"chromium-jump-list-{browser.Kind}-{items[0].GroupKey}",
            $"Chromium Jump List loaded: Browser={browser.Kind} Group={items[0].GroupKey} Profiles={profiles.Length} RecentlyClosed={recentlyClosed.Count} MostVisited={mostVisited.Count}");
        return true;
    }

    private static IReadOnlyList<JumpListMenuEntry> GetRecentlyClosed(
        BrowserInfo browser,
        IReadOnlyList<string> profiles)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<JumpListMenuEntry>();
        foreach (var profile in profiles)
        {
            foreach (var row in ReadHistoryRows(profile, HistoryQuery.Recent))
            {
                AddUrlEntry(browser, row, entries, seen);
                if (entries.Count >= MaxEntriesPerSection)
                {
                    return entries;
                }
            }
        }

        return entries;
    }

    private static IReadOnlyList<JumpListMenuEntry> GetMostVisited(
        BrowserInfo browser,
        IReadOnlyList<string> profiles)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<JumpListMenuEntry>();
        foreach (var profile in profiles)
        {
            foreach (var row in ReadTopSitesRows(profile).Concat(ReadHistoryRows(profile, HistoryQuery.MostVisited)))
            {
                AddUrlEntry(browser, row, entries, seen);
                if (entries.Count >= MaxEntriesPerSection)
                {
                    return entries;
                }
            }
        }

        return entries;
    }

    private static void AddUrlEntry(
        BrowserInfo browser,
        UrlRow row,
        List<JumpListMenuEntry> entries,
        HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(row.Url) ||
            !Uri.TryCreate(row.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !seen.Add(uri.AbsoluteUri))
        {
            return;
        }

        var title = GetDisplayTitle(row.Title, uri);
        entries.Add(new JumpListMenuEntry(
            title,
            $"chromium:{browser.Kind}:{uri.AbsoluteUri}",
            () => LaunchUrl(browser.ExecutablePath, uri.AbsoluteUri)));
    }

    private static IEnumerable<UrlRow> ReadTopSitesRows(string profilePath)
    {
        var sourcePath = Path.Combine(profilePath, "Top Sites");
        using var database = TempDatabaseCopy.TryCreate(sourcePath);
        if (database is null)
        {
            yield break;
        }

        using var connection = OpenReadOnly(database.Path);
        if (connection is null)
        {
            yield break;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT url, title
            FROM top_sites
            WHERE url LIKE 'http%'
            ORDER BY url_rank ASC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", MaxEntriesPerSection);

        using var reader = ExecuteReader(command, sourcePath);
        if (reader is null)
        {
            yield break;
        }

        while (reader.Read())
        {
            yield return new UrlRow(reader.GetString(0), reader.GetString(1));
        }
    }

    private static IEnumerable<UrlRow> ReadHistoryRows(string profilePath, HistoryQuery query)
    {
        var sourcePath = Path.Combine(profilePath, "History");
        using var database = TempDatabaseCopy.TryCreate(sourcePath);
        if (database is null)
        {
            yield break;
        }

        using var connection = OpenReadOnly(database.Path);
        if (connection is null)
        {
            yield break;
        }

        using var command = connection.CreateCommand();
        command.CommandText = query == HistoryQuery.Recent
            ? """
              SELECT u.url, u.title
              FROM urls u
              WHERE u.url LIKE 'http%' AND u.hidden = 0
              ORDER BY u.last_visit_time DESC
              LIMIT $limit
              """
            : """
              SELECT u.url, u.title
              FROM urls u
              WHERE u.url LIKE 'http%' AND u.hidden = 0
              ORDER BY u.visit_count DESC, u.last_visit_time DESC
              LIMIT $limit
              """;
        command.Parameters.AddWithValue("$limit", MaxEntriesPerSection * 3);

        using var reader = ExecuteReader(command, sourcePath);
        if (reader is null)
        {
            yield break;
        }

        while (reader.Read())
        {
            yield return new UrlRow(reader.GetString(0), reader.GetString(1));
        }
    }

    private static SqliteConnection? OpenReadOnly(string path)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly
            };
            var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            return connection;
        }
        catch (Exception exception)
        {
            DebugLogger.WriteIfChanged(
                $"chromium-db-open-{path}",
                $"Chromium Jump List database open failed: Path={path} {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    private static SqliteDataReader? ExecuteReader(SqliteCommand command, string sourcePath)
    {
        try
        {
            return command.ExecuteReader();
        }
        catch (Exception exception)
        {
            DebugLogger.WriteIfChanged(
                $"chromium-db-query-{sourcePath}-{command.CommandText.GetHashCode()}",
                $"Chromium Jump List database query failed: Source={sourcePath} {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    private static IEnumerable<string> GetProfileDirectories(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            yield break;
        }

        IEnumerable<DirectoryInfo> directories;
        try
        {
            directories = new DirectoryInfo(rootPath)
                .EnumerateDirectories()
                .Where(directory =>
                    File.Exists(Path.Combine(directory.FullName, "History")) ||
                    File.Exists(Path.Combine(directory.FullName, "Top Sites")))
                .OrderBy(directory => directory.Name.Equals("Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(directory => directory.LastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception exception)
        {
            DebugLogger.WriteIfChanged(
                $"chromium-profile-enumerate-{rootPath}",
                $"Chromium profile enumeration failed: Root={rootPath} {exception.GetType().Name}: {exception.Message}");
            yield break;
        }

        foreach (var directory in directories)
        {
            yield return directory.FullName;
        }
    }

    private static bool TryGetBrowser(TaskbarItem item, out BrowserInfo browser)
    {
        var processName = Path.GetFileNameWithoutExtension(item.ProcessName);
        if (string.IsNullOrWhiteSpace(processName))
        {
            processName = Path.GetFileNameWithoutExtension(item.ProcessPath);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (processName.Equals("chrome", StringComparison.OrdinalIgnoreCase))
        {
            browser = new BrowserInfo(
                BrowserKind.Chrome,
                item.ProcessPath,
                Path.Combine(localAppData, "Google", "Chrome", "User Data"));
            return true;
        }

        if (processName.Equals("msedge", StringComparison.OrdinalIgnoreCase))
        {
            browser = new BrowserInfo(
                BrowserKind.Edge,
                item.ProcessPath,
                Path.Combine(localAppData, "Microsoft", "Edge", "User Data"));
            return true;
        }

        browser = default;
        return false;
    }

    private static string GetReadableUrl(Uri uri)
    {
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
        return string.IsNullOrWhiteSpace(host) ? uri.AbsoluteUri : host;
    }

    private static string GetDisplayTitle(string title, Uri uri)
    {
        var trimmed = title.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ||
               Uri.TryCreate(trimmed, UriKind.Absolute, out _)
            ? GetReadableUrl(uri)
            : trimmed;
    }

    private static void LaunchUrl(string executablePath, string url)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            {
                Process.Start(new ProcessStartInfo(executablePath)
                {
                    UseShellExecute = true,
                    Arguments = QuoteArgument(url)
                });
                return;
            }

            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            DebugLogger.WriteIfChanged(
                $"chromium-url-launch-{url}",
                $"Chromium Jump List launch failed: Exe={executablePath} Url={url} {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string QuoteArgument(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private enum BrowserKind
    {
        Chrome,
        Edge
    }

    private enum HistoryQuery
    {
        Recent,
        MostVisited
    }

    private readonly record struct BrowserInfo(BrowserKind Kind, string ExecutablePath, string ProfileRoot);

    private readonly record struct UrlRow(string Url, string Title);

    private sealed class TempDatabaseCopy : IDisposable
    {
        private readonly string _directoryPath;

        private TempDatabaseCopy(string path, string directoryPath)
        {
            Path = path;
            _directoryPath = directoryPath;
        }

        public string Path { get; }

        public static TempDatabaseCopy? TryCreate(string sourcePath)
        {
            if (!File.Exists(sourcePath))
            {
                return null;
            }

            try
            {
                var directoryPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "TaskBar2-Chromium-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directoryPath);

                var destinationPath = System.IO.Path.Combine(directoryPath, System.IO.Path.GetFileName(sourcePath));
                File.Copy(sourcePath, destinationPath);
                TryCopySidecar(sourcePath + "-wal", destinationPath + "-wal");
                TryCopySidecar(sourcePath + "-shm", destinationPath + "-shm");
                return new TempDatabaseCopy(destinationPath, directoryPath);
            }
            catch (Exception exception)
            {
                DebugLogger.WriteIfChanged(
                    $"chromium-db-copy-{sourcePath}",
                    $"Chromium Jump List database copy failed: Source={sourcePath} {exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directoryPath, recursive: true);
            }
            catch
            {
            }
        }

        private static void TryCopySidecar(string sourcePath, string destinationPath)
        {
            try
            {
                if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, destinationPath);
                }
            }
            catch
            {
            }
        }
    }
}
