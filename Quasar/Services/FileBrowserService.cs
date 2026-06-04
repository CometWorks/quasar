namespace Quasar.Services;

public sealed class FileBrowserService
{
    public IReadOnlyList<FileBrowserEntry> ListDirectories(string path, bool showHidden = false)
    {
        var resolved = ResolvePath(path);
        if (!Directory.Exists(resolved))
            throw new DirectoryNotFoundException($"Directory not found: {resolved}");

        var entries = new List<FileBrowserEntry>();
        foreach (var directory in Directory.EnumerateDirectories(resolved))
        {
            try
            {
                var info = new DirectoryInfo(directory);
                if (!showHidden && (info.Attributes & FileAttributes.Hidden) != 0)
                    continue;

                entries.Add(new FileBrowserEntry(
                    info.Name,
                    info.FullName,
                    IsWorldFolder(info.FullName)));
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return entries
            .OrderByDescending(entry => entry.IsWorldFolder)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<FileBrowserShortcut> GetShortcuts()
    {
        var shortcuts = new List<FileBrowserShortcut>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(home))
            shortcuts.Add(new FileBrowserShortcut("Home", home));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (Directory.Exists(appData))
        {
            var seDir = Path.Combine(appData, "SpaceEngineersDedicated", "Saves");
            if (Directory.Exists(seDir))
                shortcuts.Add(new FileBrowserShortcut("SE Dedicated Saves", seDir));

            var sePlayer = Path.Combine(appData, "SpaceEngineers", "Saves");
            if (Directory.Exists(sePlayer))
                shortcuts.Add(new FileBrowserShortcut("SE Player Saves", sePlayer));
        }

        return shortcuts;
    }

    public static bool IsWorldFolder(string path)
    {
        return File.Exists(Path.Combine(path, "Sandbox.sbc"));
    }

    public static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var trimmed = path.Trim();
        if (trimmed.StartsWith('~'))
            trimmed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), trimmed[1..].TrimStart('/', '\\'));

        return Path.GetFullPath(trimmed);
    }

    public static IReadOnlyList<FileBrowserBreadcrumb> GetBreadcrumbs(string path)
    {
        var resolved = ResolvePath(path);
        var crumbs = new List<FileBrowserBreadcrumb>();
        var current = new DirectoryInfo(resolved);
        while (current is not null)
        {
            crumbs.Insert(0, new FileBrowserBreadcrumb(
                string.IsNullOrEmpty(current.Name) ? current.FullName : current.Name,
                current.FullName));
            current = current.Parent;
        }

        return crumbs;
    }
}

public sealed record FileBrowserEntry(string Name, string FullPath, bool IsWorldFolder);

public sealed record FileBrowserShortcut(string Label, string FullPath);

public sealed record FileBrowserBreadcrumb(string Label, string FullPath);
