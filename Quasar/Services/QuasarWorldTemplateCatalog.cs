using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

public sealed record WorldTemplateImportProgress(int CompletedFiles, int TotalFiles);

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
    private ChildFileWatcher? _watcher;

    public QuasarWorldTemplateCatalog(ILogger<QuasarWorldTemplateCatalog> logger)
    {
        _logger = logger;
        _templates = LoadTemplates();
        _snapshot = CreateSnapshot(_templates);
        StartWatching();
    }

    public event Action? Changed;

    public void Dispose()
    {
        _watcher?.Dispose();
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
        CancellationToken cancellationToken = default,
        IProgress<WorldTemplateImportProgress>? progress = null)
    {
        sourcePath = sourcePath.Trim();

        if (!Directory.Exists(sourcePath))
            throw new InvalidOperationException($"Source world directory not found: {sourcePath}");

        var sandboxPath = Path.Combine(sourcePath, "Sandbox.sbc");
        if (!File.Exists(sandboxPath))
            throw new InvalidOperationException($"Source directory '{sourcePath}' does not contain Sandbox.sbc.");

        cancellationToken.ThrowIfCancellationRequested();
        QuasarWorldTemplate template;
        string destWorldDir;
        lock (_sync)
        {
            template = Normalize(new QuasarWorldTemplate
            {
                WorldTemplateId = GenerateUniqueTemplateId(name),
                Name = name,
                Description = description,
            });
            destWorldDir = MagnetarPaths.GetQuasarWorldTemplateWorldDirectory(template.WorldTemplateId);
            if (Path.GetFullPath(destWorldDir).StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath)) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Choose a world folder outside the destination template directory.");
            // Reserve the ID before yielding so simultaneous imports cannot share a destination.
            Directory.CreateDirectory(destWorldDir);
        }

        try
        {
            var files = await Task.Run(() => Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
                .Where(file => !string.Equals(Path.GetRelativePath(sourcePath, file)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0], "Backup", StringComparison.OrdinalIgnoreCase))
                .Select(file => { cancellationToken.ThrowIfCancellationRequested(); return file; }).ToArray(), cancellationToken);
            progress?.Report(new(0, files.Length));
            var completed = 0;
            foreach (var sourceFile in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destFile = Path.Combine(destWorldDir, Path.GetRelativePath(sourcePath, sourceFile));
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                await using var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await using var destination = new FileStream(destFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await source.CopyToAsync(destination, cancellationToken);
                progress?.Report(new(++completed, files.Length));
            }
            await SaveTemplateAsync(template, cancellationToken);
        }
        catch
        {
            try { Directory.Delete(MagnetarPaths.GetQuasarWorldTemplateDirectory(template.WorldTemplateId), recursive: true); }
            catch (Exception cleanupError) { _logger.LogWarning(cleanupError, "Could not remove incomplete world template {TemplateId}.", template.WorldTemplateId); }
            throw;
        }

        lock (_sync)
        {
            _templates.RemoveAll(t => string.Equals(t.WorldTemplateId, template.WorldTemplateId, StringComparison.OrdinalIgnoreCase));
            _templates.Add(Clone(template));
            _snapshot = CreateSnapshot(_templates);
        }

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

            return ChildFileWatcher
                .FindFiles(directory, "template.json")
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
            var pathTemplateId = GetTemplateIdFromDefinitionPath(path);

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

        Directory.CreateDirectory(historyDirectory);
        var historyPath = Path.Combine(historyDirectory, $"{template.UpdatedAtUtc:yyyyMMddHHmmssfff}.json");
        await AtomicFileWriter.WriteTextAsync(historyPath, json, cancellationToken);
        // Publish only after the copy and its history are complete.
        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);

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
        _watcher = new ChildFileWatcher(MagnetarPaths.GetQuasarWorldTemplatesDirectory(), "template.json", ReloadFromDisk);
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

    private static string GetTemplateIdFromDefinitionPath(string definitionPath) =>
        Path.GetFileName(Path.GetDirectoryName(definitionPath)) ?? string.Empty;
}
