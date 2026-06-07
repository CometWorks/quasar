using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

public sealed class QuasarWorldTemplateCatalog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<QuasarWorldTemplateCatalog> _logger;
    private List<QuasarWorldTemplate> _templates;
    private string _snapshot;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;

    public QuasarWorldTemplateCatalog(ILogger<QuasarWorldTemplateCatalog> logger)
    {
        _logger = logger;
        MigrateLegacyStorage();
        _templates = LoadTemplates();
        _snapshot = CreateSnapshot(_templates);
        StartWatching();
    }

    public event Action? Changed;

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
    }

    public IReadOnlyList<QuasarWorldTemplate> GetTemplates()
    {
        lock (_sync)
        {
            return _templates
                .Select(Clone)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.WorldTemplateId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public QuasarWorldTemplate? GetTemplate(string worldTemplateId)
    {
        if (string.IsNullOrWhiteSpace(worldTemplateId))
            return null;

        lock (_sync)
        {
            return _templates
                .Where(p => string.Equals(p.WorldTemplateId, worldTemplateId, StringComparison.OrdinalIgnoreCase))
                .Select(Clone)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Returns the directory where this template's world files are stored.
    /// Does not guarantee the directory exists.
    /// </summary>
    public string GetWorldDirectory(string worldTemplateId) =>
        MagnetarPaths.GetQuasarWorldTemplateWorldDirectory(worldTemplateId);

    /// <summary>
    /// Creates a new world template by copying files from <paramref name="sourcePath"/> into
    /// Quasar-managed storage.
    /// </summary>
    public async Task<QuasarWorldTemplate> ImportAsync(
        string name,
        string description,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        sourcePath = sourcePath.Trim();

        if (!Directory.Exists(sourcePath))
            throw new InvalidOperationException($"Source world directory not found: {sourcePath}");

        var sandboxPath = Path.Combine(sourcePath, "Sandbox.sbc");
        if (!File.Exists(sandboxPath))
            throw new InvalidOperationException($"Source directory '{sourcePath}' does not contain Sandbox.sbc.");

        var template = Normalize(new QuasarWorldTemplate
        {
            WorldTemplateId = GenerateUniqueTemplateId(name),
            Name = name,
            Description = description,
        });

        var destWorldDir = MagnetarPaths.GetQuasarWorldTemplateWorldDirectory(template.WorldTemplateId);
        Directory.CreateDirectory(destWorldDir);

        foreach (var sourceFile in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourcePath, sourceFile);

            // Skip the world's Backup/ subdirectory — it holds only historical backup
            // copies of the save, which are unused as long as the top-level save is good.
            var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (string.Equals(firstSegment, "Backup", StringComparison.OrdinalIgnoreCase))
                continue;

            var destFile = Path.Combine(destWorldDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(sourceFile, destFile, overwrite: true);
        }

        lock (_sync)
        {
            _templates.Add(Clone(template));
            _snapshot = CreateSnapshot(_templates);
        }

        await SaveTemplateAsync(template, cancellationToken);
        Changed?.Invoke();
        return Clone(template);
    }

    public async Task DeleteAsync(string worldTemplateId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(worldTemplateId))
            return;

        QuasarWorldTemplate? removed = null;
        lock (_sync)
        {
            var index = _templates.FindIndex(p =>
                string.Equals(p.WorldTemplateId, worldTemplateId, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                removed = Clone(_templates[index]);
                _templates.RemoveAt(index);
                _snapshot = CreateSnapshot(_templates);
            }
        }

        if (removed is null)
            return;

        await ArchiveAndDeleteTemplateAsync(removed.WorldTemplateId, cancellationToken);
        Changed?.Invoke();
    }

    private List<QuasarWorldTemplate> LoadTemplates()
    {
        try
        {
            var directory = MagnetarPaths.GetQuasarWorldTemplatesDirectory();
            if (!Directory.Exists(directory))
                return [];

            return Directory
                .GetFiles(directory, "template.json", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(directory, "profile.json", SearchOption.AllDirectories))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(LoadTemplate)
                .Where(p => p is not null)
                .Select(p => Normalize(p!))
                .ToList();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load Quasar world templates.");
            return [];
        }
    }

    private QuasarWorldTemplate? LoadTemplate(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var template = JsonSerializer.Deserialize<QuasarWorldTemplate>(json, JsonOptions) ?? new QuasarWorldTemplate();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var pathTemplateId = GetTemplateIdFromDefinitionPath(path);

            if (TryGetString(root, "worldTemplateId", out var storedId))
            {
                template.WorldTemplateId = storedId;
            }
            else if (TryGetString(root, "worldProfileId", out var legacyId))
            {
                template.WorldTemplateId = legacyId;
            }

            if (!string.IsNullOrWhiteSpace(pathTemplateId))
                template.WorldTemplateId = pathTemplateId;

            return template;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load Quasar world template from {Path}", path);
            return null;
        }
    }

    private async Task SaveTemplateAsync(QuasarWorldTemplate template, CancellationToken cancellationToken)
    {
        template.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var path = MagnetarPaths.GetQuasarWorldTemplateDefinitionPath(template.WorldTemplateId);
        var historyDirectory = MagnetarPaths.GetQuasarWorldTemplateHistoryDirectory(template.WorldTemplateId);
        var json = JsonSerializer.Serialize(template, JsonOptions);

        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);

        Directory.CreateDirectory(historyDirectory);
        var historyPath = Path.Combine(historyDirectory, $"{template.UpdatedAtUtc:yyyyMMddHHmmssfff}.json");
        await AtomicFileWriter.WriteTextAsync(historyPath, json, cancellationToken);

        _logger.LogInformation("Saved Quasar world template to {Path}", path);
    }

    private async Task ArchiveAndDeleteTemplateAsync(string worldTemplateId, CancellationToken cancellationToken)
    {
        var currentPath = MagnetarPaths.GetQuasarWorldTemplateDefinitionPath(worldTemplateId);
        if (File.Exists(currentPath))
        {
            var historyDirectory = MagnetarPaths.GetQuasarWorldTemplateHistoryDirectory(worldTemplateId);
            Directory.CreateDirectory(historyDirectory);

            var deletedContents = await File.ReadAllTextAsync(currentPath, cancellationToken);
            var historyPath = Path.Combine(historyDirectory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-deleted.json");
            await AtomicFileWriter.WriteTextAsync(historyPath, deletedContents, cancellationToken);

            File.Delete(currentPath);
        }

        var worldDir = MagnetarPaths.GetQuasarWorldTemplateWorldDirectory(worldTemplateId);
        if (Directory.Exists(worldDir))
            Directory.Delete(worldDir, recursive: true);

        _logger.LogInformation("Deleted Quasar world template {WorldTemplateId}", worldTemplateId);
    }

    /// <summary>
    /// Derives a folder/identifier slug from the template name, appending a
    /// "-N" suffix when needed so it does not collide with an existing template.
    /// </summary>
    private string GenerateUniqueTemplateId(string name) =>
        IdentifierSlug.CreateUnique(name, "world", TemplateIdExists);

    private bool TemplateIdExists(string candidate)
    {
        lock (_sync)
        {
            if (_templates.Any(t =>
                    string.Equals(t.WorldTemplateId, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return Directory.Exists(MagnetarPaths.GetQuasarWorldTemplateDirectory(candidate));
    }

    private static QuasarWorldTemplate Normalize(QuasarWorldTemplate template)
    {
        template.WorldTemplateId = string.IsNullOrWhiteSpace(template.WorldTemplateId)
            ? Guid.NewGuid().ToString("N")
            : template.WorldTemplateId.Trim();
        template.Name = template.Name?.Trim() ?? string.Empty;
        template.Description = template.Description?.Trim() ?? string.Empty;
        if (template.UpdatedAtUtc == default)
            template.UpdatedAtUtc = DateTimeOffset.UtcNow;
        return template;
    }

    private static QuasarWorldTemplate Clone(QuasarWorldTemplate template) =>
        new()
        {
            WorldTemplateId = template.WorldTemplateId,
            Name = template.Name,
            Description = template.Description,
            UpdatedAtUtc = template.UpdatedAtUtc,
        };

    private void StartWatching()
    {
        var directory = MagnetarPaths.GetQuasarWorldTemplatesDirectory();
        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            Filter = "template.json",
        };

        _watcher.Changed += HandleWatchedFileChanged;
        _watcher.Created += HandleWatchedFileChanged;
        _watcher.Deleted += HandleWatchedFileChanged;
        _watcher.Renamed += HandleWatchedFileChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void HandleWatchedFileChanged(object sender, FileSystemEventArgs args) => ScheduleReload();

    private void ScheduleReload()
    {
        CancellationTokenSource debounce;
        lock (_sync)
        {
            _reloadDebounce?.Cancel();
            _reloadDebounce?.Dispose();
            _reloadDebounce = new CancellationTokenSource();
            debounce = _reloadDebounce;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), debounce.Token);
                ReloadFromDisk();
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private void ReloadFromDisk()
    {
        List<QuasarWorldTemplate> reloaded;
        string snapshot;

        try
        {
            reloaded = LoadTemplates();
            snapshot = CreateSnapshot(reloaded);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed reloading Quasar world templates from disk.");
            return;
        }

        var changed = false;
        lock (_sync)
        {
            if (!string.Equals(_snapshot, snapshot, StringComparison.Ordinal))
            {
                _templates = reloaded;
                _snapshot = snapshot;
                changed = true;
            }
        }

        if (!changed)
            return;

        _logger.LogInformation("Reloaded Quasar world templates from disk after external edit.");
        Changed?.Invoke();
    }

    private static string CreateSnapshot(IEnumerable<QuasarWorldTemplate> templates)
    {
        var normalized = templates
            .Select(p => Normalize(Clone(p)))
            .OrderBy(p => p.WorldTemplateId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private void MigrateLegacyStorage()
    {
        var currentDirectory = MagnetarPaths.GetQuasarWorldTemplatesDirectory();
        var legacyDirectory = MagnetarPaths.GetLegacyQuasarWorldProfilesDirectory();

        try
        {
            if (Directory.Exists(legacyDirectory) && !Directory.Exists(currentDirectory))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(currentDirectory)!);
                Directory.Move(legacyDirectory, currentDirectory);
                _logger.LogInformation("Migrated Quasar world templates from {LegacyPath} to {Path}.", legacyDirectory, currentDirectory);
            }
            else if (Directory.Exists(legacyDirectory) && Directory.Exists(currentDirectory))
            {
                foreach (var legacyTemplateDirectory in Directory.GetDirectories(legacyDirectory))
                {
                    var targetDirectory = Path.Combine(currentDirectory, Path.GetFileName(legacyTemplateDirectory));
                    if (!Directory.Exists(targetDirectory))
                        Directory.Move(legacyTemplateDirectory, targetDirectory);
                }
            }

            if (Directory.Exists(currentDirectory))
            {
                RenameLegacyTemplateDefinitions(currentDirectory);
                RewriteLegacyTemplateDefinitions(currentDirectory);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed migrating legacy Quasar world template storage.");
        }
    }

    private static void RenameLegacyTemplateDefinitions(string directory)
    {
        foreach (var legacyDefinitionPath in Directory.GetFiles(directory, "profile.json", SearchOption.AllDirectories))
        {
            var newDefinitionPath = Path.Combine(Path.GetDirectoryName(legacyDefinitionPath)!, "template.json");
            if (File.Exists(newDefinitionPath))
                continue;

            File.Move(legacyDefinitionPath, newDefinitionPath);
        }
    }

    private static void RewriteLegacyTemplateDefinitions(string directory)
    {
        foreach (var definitionPath in Directory.GetFiles(directory, "template.json", SearchOption.AllDirectories))
        {
            var json = File.ReadAllText(definitionPath);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var pathTemplateId = GetTemplateIdFromDefinitionPath(definitionPath);
            var hasWorldTemplateId = TryGetString(root, "worldTemplateId", out var storedId);
            var hasLegacyWorldTemplateId = TryGetString(root, "worldProfileId", out var legacyId);

            if (hasWorldTemplateId &&
                !hasLegacyWorldTemplateId &&
                string.Equals(storedId, pathTemplateId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var template = JsonSerializer.Deserialize<QuasarWorldTemplate>(json, JsonOptions) ?? new QuasarWorldTemplate();
            template.WorldTemplateId = !string.IsNullOrWhiteSpace(pathTemplateId)
                ? pathTemplateId
                : hasLegacyWorldTemplateId
                    ? legacyId
                    : template.WorldTemplateId;

            var rewrittenJson = JsonSerializer.Serialize(Normalize(template), JsonOptions);
            WriteTextReplacing(definitionPath, rewrittenJson);
        }
    }

    private static string GetTemplateIdFromDefinitionPath(string definitionPath) =>
        Path.GetFileName(Path.GetDirectoryName(definitionPath)) ?? string.Empty;

    private static void WriteTextReplacing(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, overwrite: true);
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
}
