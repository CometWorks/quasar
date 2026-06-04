using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

public sealed class QuasarDevFolderCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<QuasarDevFolderCatalog> _logger;
    private List<QuasarDevFolderSelection> _devFolders;

    public QuasarDevFolderCatalog(ILogger<QuasarDevFolderCatalog> logger)
    {
        _logger = logger;
        _devFolders = Load();
    }

    public event Action? Changed;

    public IReadOnlyList<QuasarDevFolderSelection> GetDevFolders()
    {
        lock (_sync)
            return _devFolders.Select(Clone).ToList();
    }

    public QuasarDevFolderSelection? GetDevFolder(string folderPath, string dataFile)
    {
        lock (_sync)
        {
            return _devFolders
                .Where(devFolder => IsSameDevFolder(devFolder, folderPath, dataFile))
                .Select(Clone)
                .FirstOrDefault();
        }
    }

    public async Task UpsertAsync(QuasarDevFolderSelection devFolder, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(devFolder);
        if (string.IsNullOrWhiteSpace(normalized.FolderPath) || string.IsNullOrWhiteSpace(normalized.DataFile))
            throw new InvalidOperationException("Folder and manifest file are required.");

        lock (_sync)
        {
            var index = _devFolders.FindIndex(existing => IsSameDevFolder(existing, normalized.FolderPath, normalized.DataFile));
            if (index >= 0)
                _devFolders[index] = normalized;
            else
                _devFolders.Add(normalized);

            _devFolders = NormalizeList(_devFolders);
        }

        await SaveAsync(cancellationToken);
        Changed?.Invoke();
    }

    public async Task DeleteAsync(string folderPath, string dataFile, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _devFolders.RemoveAll(devFolder => IsSameDevFolder(devFolder, folderPath, dataFile));
        }

        await SaveAsync(cancellationToken);
        Changed?.Invoke();
    }

    private List<QuasarDevFolderSelection> Load()
    {
        try
        {
            var path = GetPath();
            if (!File.Exists(path))
                return [];

            var json = File.ReadAllText(path);
            return NormalizeList(JsonSerializer.Deserialize<List<QuasarDevFolderSelection>>(json, JsonOptions) ?? []);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load Quasar dev folders.");
            return [];
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        List<QuasarDevFolderSelection> snapshot;
        lock (_sync)
            snapshot = _devFolders.Select(Clone).ToList();

        var path = GetPath();
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);
    }

    private static List<QuasarDevFolderSelection> NormalizeList(IEnumerable<QuasarDevFolderSelection> devFolders) =>
        devFolders
            .Select(Normalize)
            .Where(devFolder => !string.IsNullOrWhiteSpace(devFolder.FolderPath) && !string.IsNullOrWhiteSpace(devFolder.DataFile))
            .DistinctBy(devFolder => (devFolder.FolderPath, devFolder.DataFile))
            .OrderBy(devFolder => devFolder.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static QuasarDevFolderSelection Normalize(QuasarDevFolderSelection devFolder) =>
        new()
        {
            Name = devFolder.Name?.Trim() ?? string.Empty,
            FolderPath = devFolder.FolderPath?.Trim() ?? string.Empty,
            DataFile = devFolder.DataFile?.Trim() ?? string.Empty,
            PluginId = devFolder.PluginId?.Trim() ?? string.Empty,
            DebugBuild = devFolder.DebugBuild,
            Enabled = devFolder.Enabled,
        };

    private static bool IsSameDevFolder(QuasarDevFolderSelection devFolder, string folderPath, string dataFile) =>
        string.Equals(devFolder.FolderPath, folderPath?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(devFolder.DataFile, dataFile?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static QuasarDevFolderSelection Clone(QuasarDevFolderSelection devFolder) =>
        JsonSerializer.Deserialize<QuasarDevFolderSelection>(JsonSerializer.Serialize(devFolder, JsonOptions), JsonOptions)
        ?? new QuasarDevFolderSelection();

    private static string GetPath() =>
        Path.Combine(MagnetarPaths.GetQuasarDirectory(), "dev-folders.json");
}
