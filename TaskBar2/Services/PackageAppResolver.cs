using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Microsoft.Win32;

namespace TaskBar2.Services;

internal static class PackageAppResolver
{
    private const string RepositoryPackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    private static readonly object Sync = new();
    private static readonly Dictionary<string, PackageAppInfo?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsPackageAppId(string appUserModelId) =>
        !string.IsNullOrWhiteSpace(appUserModelId) &&
        appUserModelId.Contains('!', StringComparison.Ordinal);

    public static PackageAppInfo? Resolve(string appUserModelId)
    {
        if (!IsPackageAppId(appUserModelId))
        {
            return null;
        }

        lock (Sync)
        {
            if (Cache.TryGetValue(appUserModelId, out var cached))
            {
                return cached;
            }
        }

        var resolved = ResolveUncached(appUserModelId);
        lock (Sync)
        {
            Cache[appUserModelId] = resolved;
        }

        return resolved;
    }

    public static ImageSource? CreateImageSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public static string GetFileFingerprint(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? $"file:{info.FullName.ToUpperInvariant()}:{info.Length}:{info.LastWriteTimeUtc.Ticks}"
                : $"file:{path.ToUpperInvariant()}";
        }
        catch
        {
            return $"file:{path.ToUpperInvariant()}";
        }
    }

    private static PackageAppInfo? ResolveUncached(string appUserModelId)
    {
        var bangIndex = appUserModelId.IndexOf('!');
        if (bangIndex <= 0 || bangIndex >= appUserModelId.Length - 1)
        {
            return null;
        }

        var packageFamilyName = appUserModelId[..bangIndex];
        var applicationId = appUserModelId[(bangIndex + 1)..];
        var packageRoot = FindPackageRoot(packageFamilyName);
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            return null;
        }

        var manifestPath = Path.Combine(packageRoot, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var document = XDocument.Load(manifestPath);
            var application = document
                .Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName == "Application" &&
                    string.Equals((string?)element.Attribute("Id"), applicationId, StringComparison.OrdinalIgnoreCase)) ??
                document
                    .Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == "Application");

            if (application is null)
            {
                return null;
            }

            var executable = ((string?)application.Attribute("Executable"))?.Trim() ?? "";
            var visualElements = application
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "VisualElements");
            var logo = ((string?)visualElements?.Attribute("Square44x44Logo"))?.Trim() ?? "";
            var displayName = ((string?)visualElements?.Attribute("DisplayName"))?.Trim() ?? "";
            var executablePath = ResolvePackageFile(packageRoot, executable);
            var iconPath = ResolvePackageLogoPath(packageRoot, logo);

            return new PackageAppInfo(packageRoot, executablePath, iconPath, displayName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string FindPackageRoot(string packageFamilyName)
    {
        using var packagesKey = Registry.CurrentUser.OpenSubKey(RepositoryPackagesKey);
        if (packagesKey is null)
        {
            return "";
        }

        foreach (var subKeyName in packagesKey.GetSubKeyNames())
        {
            if (!IsPackageFamilyMatch(subKeyName, packageFamilyName))
            {
                continue;
            }

            using var packageKey = packagesKey.OpenSubKey(subKeyName);
            var packageRoot = packageKey?.GetValue("PackageRootFolder") as string;
            if (!string.IsNullOrWhiteSpace(packageRoot) && Directory.Exists(packageRoot))
            {
                return packageRoot;
            }
        }

        return "";
    }

    private static bool IsPackageFamilyMatch(string packageFullName, string packageFamilyName)
    {
        var splitIndex = packageFamilyName.LastIndexOf('_');
        if (splitIndex <= 0 || splitIndex >= packageFamilyName.Length - 1)
        {
            return false;
        }

        var packageName = packageFamilyName[..splitIndex];
        var publisherId = packageFamilyName[(splitIndex + 1)..];
        return packageFullName.StartsWith(packageName + "_", StringComparison.OrdinalIgnoreCase) &&
               packageFullName.EndsWith("__" + publisherId, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolvePackageFile(string packageRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var path = Path.Combine(packageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : "";
    }

    private static string ResolvePackageLogoPath(string packageRoot, string relativePath)
    {
        var exactPath = ResolvePackageFile(packageRoot, relativePath);
        if (!string.IsNullOrWhiteSpace(exactPath))
        {
            return exactPath;
        }

        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var expectedPath = Path.Combine(packageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(expectedPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return "";
        }

        var extension = Path.GetExtension(expectedPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".png";
        }

        var baseName = Path.GetFileNameWithoutExtension(expectedPath);
        try
        {
            return Directory
                .EnumerateFiles(directory, baseName + "*" + extension, SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .OrderByDescending(ScoreLogoCandidate)
                .ThenByDescending(file => file.Length)
                .Select(file => file.FullName)
                .FirstOrDefault() ?? "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static int ScoreLogoCandidate(FileInfo file)
    {
        var name = file.Name.ToLowerInvariant();
        var score = 0;
        if (name.Contains("altform-unplated", StringComparison.Ordinal))
        {
            score += 1000;
        }

        if (name.Contains("targetsize-256", StringComparison.Ordinal)) return score + 900;
        if (name.Contains("targetsize-128", StringComparison.Ordinal)) return score + 850;
        if (name.Contains("targetsize-96", StringComparison.Ordinal)) return score + 800;
        if (name.Contains("targetsize-64", StringComparison.Ordinal)) return score + 750;
        if (name.Contains("targetsize-48", StringComparison.Ordinal)) return score + 700;
        if (name.Contains("targetsize-44", StringComparison.Ordinal)) return score + 680;
        if (name.Contains("targetsize-32", StringComparison.Ordinal)) return score + 600;
        if (name.Contains("scale-400", StringComparison.Ordinal)) return score + 500;
        if (name.Contains("scale-300", StringComparison.Ordinal)) return score + 450;
        if (name.Contains("scale-200", StringComparison.Ordinal)) return score + 400;
        if (name.Contains("scale-150", StringComparison.Ordinal)) return score + 350;
        if (name.Contains("scale-125", StringComparison.Ordinal)) return score + 300;
        if (name.Contains("scale-100", StringComparison.Ordinal)) return score + 250;
        return score;
    }
}

internal sealed record PackageAppInfo(
    string PackageRoot,
    string ExecutablePath,
    string IconPath,
    string DisplayName);
