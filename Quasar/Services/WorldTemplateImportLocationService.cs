using Magnetar.Protocol.Runtime;
using Microsoft.JSInterop;
using System.Xml.Linq;

namespace Quasar.Services;

public sealed record InstalledWorldTemplateSource(
    string Category,
    string DisplayName,
    string SourcePath,
    string SourceDisplayPath,
    string Description);

public sealed class WorldTemplateImportLocationService
{
    private const string StorageKey = "quasar.worldTemplates.lastSourceFolder";

    private static readonly (string Label, string RelativePath)[] DedicatedServerContentShortcuts =
    [
        ("DS Custom Worlds", Path.Combine("Content", "CustomWorlds")),
        ("DS Quick Starts", Path.Combine("Content", "QuickStarts")),
        ("DS Scenarios", Path.Combine("Content", "Scenarios")),
    ];

    private readonly ManagedRuntimeOptions _options;
    private readonly ILocalStorageService _localStorage;

    public WorldTemplateImportLocationService(ManagedRuntimeOptions options, ILocalStorageService localStorage)
    {
        _options = options;
        _localStorage = localStorage;
    }

    public IReadOnlyList<FileBrowserShortcut> GetContentShortcuts()
    {
        var shortcuts = new List<FileBrowserShortcut>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in GetDedicatedServerInstallRoots())
        {
            foreach (var (label, relativePath) in DedicatedServerContentShortcuts)
            {
                var path = Path.Combine(root, relativePath);
                if (!Directory.Exists(path))
                    continue;

                var fullPath = Path.GetFullPath(path);
                if (seenPaths.Add(fullPath))
                    shortcuts.Add(new FileBrowserShortcut(label, fullPath));
            }
        }

        return shortcuts;
    }

    public IReadOnlyList<InstalledWorldTemplateSource> GetInstalledWorldTemplates()
    {
        var templates = new List<InstalledWorldTemplateSource>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in GetDedicatedServerInstallRoots())
        {
            foreach (var (label, relativePath) in DedicatedServerContentShortcuts)
            {
                var categoryRoot = Path.Combine(root, relativePath);
                if (!Directory.Exists(categoryRoot))
                    continue;

                foreach (var sandboxPath in EnumerateSandboxFiles(categoryRoot))
                {
                    var sourcePath = Path.GetDirectoryName(sandboxPath) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(sourcePath))
                        continue;

                    var fullPath = Path.GetFullPath(sourcePath);
                    if (!seenPaths.Add(fullPath))
                        continue;

                    var relative = Path.GetRelativePath(categoryRoot, fullPath);
                    if (ShouldSkipInstalledTemplate(relative))
                        continue;

                    var displayName = BuildInstalledTemplateName(label, relative, sandboxPath);
                    var sourceDisplayPath = BuildInstalledTemplateSourceDisplayPath(relative);
                    templates.Add(new InstalledWorldTemplateSource(
                        Category: label,
                        DisplayName: displayName,
                        SourcePath: fullPath,
                        SourceDisplayPath: sourceDisplayPath,
                        Description: $"Installed Space Engineers {label} template from {sourceDisplayPath}."));
                }
            }
        }

        return templates
            .OrderBy(template => template.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(template => template.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(template => template.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string> GetInitialPathAsync(string? currentPath)
    {
        if (!string.IsNullOrWhiteSpace(currentPath))
            return currentPath.Trim();

        var stored = await TryGetStoredPathAsync();
        if (!string.IsNullOrWhiteSpace(stored) && Directory.Exists(stored))
            return stored;

        return GetContentShortcuts().FirstOrDefault()?.FullPath ?? string.Empty;
    }

    public async Task RememberAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var resolved = FileBrowserService.ResolvePath(path);
            if (Directory.Exists(resolved))
                await _localStorage.SetItemAsync(StorageKey, resolved);
        }
        catch (InvalidOperationException)
        {
        }
        catch (JSDisconnectedException)
        {
        }
    }

    private async Task<string> TryGetStoredPathAsync()
    {
        try
        {
            return await _localStorage.GetItemAsync<string>(StorageKey) ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
        catch (JSDisconnectedException)
        {
            return string.Empty;
        }
    }

    private IEnumerable<string> GetDedicatedServerInstallRoots()
    {
        if (!string.IsNullOrWhiteSpace(_options.DedicatedServerInstallDirectory))
            yield return _options.DedicatedServerInstallDirectory;

        if (!string.IsNullOrWhiteSpace(_options.DedicatedServer64OverridePath))
        {
            var resolved = FileBrowserService.ResolvePath(_options.DedicatedServer64OverridePath);
            var directory = new DirectoryInfo(resolved);
            if (string.Equals(directory.Name, "DedicatedServer64", StringComparison.OrdinalIgnoreCase) &&
                directory.Parent is not null)
            {
                yield return directory.Parent.FullName;
            }
            else
            {
                yield return resolved;
            }
        }

        yield return MagnetarPaths.GetQuasarManagedDedicatedServerInstallDirectory();
    }

    private static IEnumerable<string> EnumerateSandboxFiles(string categoryRoot)
    {
        try
        {
            return Directory.EnumerateFiles(categoryRoot, "Sandbox.sbc", SearchOption.AllDirectories).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool ShouldSkipInstalledTemplate(string relativePath)
    {
        var segments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return segments.Any(segment => string.Equals(segment, "XBox", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildInstalledTemplateName(string category, string relativePath, string sandboxPath)
    {
        var segments = SplitRelativePath(relativePath);
        var folderName = GetInstalledTemplateFolderName(segments, sandboxPath);
        var sessionName = ReadSessionName(sandboxPath);
        if (IsGenericInstalledTemplateSegment(sessionName))
            sessionName = string.Empty;

        if (string.Equals(category, "DS Scenarios", StringComparison.OrdinalIgnoreCase) && segments.Length > 0)
        {
            var scenarioName = segments[0];
            if (!string.IsNullOrWhiteSpace(sessionName) &&
                !string.Equals(sessionName, scenarioName, StringComparison.OrdinalIgnoreCase))
            {
                return $"{scenarioName} - {sessionName}";
            }

            return scenarioName;
        }

        return string.IsNullOrWhiteSpace(sessionName) ? folderName : sessionName;
    }

    private static string BuildInstalledTemplateSourceDisplayPath(string relativePath)
    {
        var segments = SplitRelativePath(relativePath)
            .Where(segment => !IsGenericInstalledTemplateSegment(segment) &&
                              !string.Equals(segment, "Worlds", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return segments.Length == 0
            ? relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/')
            : string.Join("/", segments);
    }

    private static string GetInstalledTemplateFolderName(IReadOnlyList<string> segments, string sandboxPath)
    {
        var meaningfulFolderName = segments
            .Where(segment => !IsGenericInstalledTemplateSegment(segment) &&
                              !string.Equals(segment, "Worlds", StringComparison.OrdinalIgnoreCase))
            .LastOrDefault();
        if (!string.IsNullOrWhiteSpace(meaningfulFolderName))
            return meaningfulFolderName;

        return segments.LastOrDefault() ?? Path.GetFileName(Path.GetDirectoryName(sandboxPath)) ?? "World";
    }

    private static string[] SplitRelativePath(string relativePath) =>
        relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

    private static bool IsGenericInstalledTemplateSegment(string value) =>
        string.Equals(value, "PC", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "XBox", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Xbox", StringComparison.OrdinalIgnoreCase);

    private static string ReadSessionName(string sandboxPath)
    {
        try
        {
            var document = XDocument.Load(sandboxPath, LoadOptions.None);
            var sessionName = document
                .Descendants("SessionName")
                .FirstOrDefault()
                ?.Value
                ?.Trim() ?? string.Empty;

            return sessionName.StartsWith("{LOCG:", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : sessionName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return string.Empty;
        }
    }
}
