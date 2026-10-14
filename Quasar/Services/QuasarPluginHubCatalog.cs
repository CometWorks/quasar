using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

/// <summary>The extra plugin hubs registered by the operator, kept in plugin-hubs.json.</summary>
public sealed class QuasarPluginHubCatalog
{
    public const string DefaultBranch = "main";

    private static readonly Regex RepoPattern = new(
        "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$",
        RegexOptions.CultureInvariant
    );

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<QuasarPluginHubCatalog> _logger;
    private List<QuasarPluginHub> _hubs;

    public QuasarPluginHubCatalog(ILogger<QuasarPluginHubCatalog> logger)
    {
        _logger = logger;
        _hubs = Load();
    }

    public event Action? Changed;

    public IReadOnlyList<QuasarPluginHub> GetHubs()
    {
        lock (_sync)
            return _hubs.Select(Clone).ToList();
    }

    /// <summary>Re-reads the hubs from disk (used after a backup restore).</summary>
    public void ReloadFromDisk()
    {
        var reloaded = Load();
        lock (_sync)
            _hubs = reloaded;

        Changed?.Invoke();
    }

    public async Task AddAsync(QuasarPluginHub hub, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(hub);
        lock (_sync)
        {
            Validate(normalized, _hubs);
            _hubs.Add(normalized);
        }

        await SaveAsync(cancellationToken);
        Changed?.Invoke();
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_sync)
            _hubs.RemoveAll(hub => IsSameKey(hub.Key, key));

        await SaveAsync(cancellationToken);
        Changed?.Invoke();
    }

    internal static QuasarPluginHub Normalize(QuasarPluginHub hub)
    {
        var folder = hub.Folder?.Trim() ?? string.Empty;
        if (folder.Length > 1)
            folder = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repo = folder.Length == 0 ? hub.Repo?.Trim().Trim('/') ?? string.Empty : string.Empty;
        var branch = folder.Length == 0 ? hub.Branch?.Trim() ?? string.Empty : string.Empty;
        if (repo.Length != 0 && branch.Length == 0)
            branch = DefaultBranch;
        var name = hub.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            name = folder.Length != 0 ? Path.GetFileName(folder) : repo;
        return new()
        {
            Name = name,
            Repo = repo,
            Branch = branch,
            Folder = folder,
        };
    }

    internal static void Validate(QuasarPluginHub hub, IReadOnlyList<QuasarPluginHub> existing)
    {
        if (hub.IsLocal)
        {
            if (!Path.IsPathFullyQualified(hub.Folder))
                throw new InvalidOperationException("The hub folder must be an absolute path.");
            if (!Directory.Exists(hub.Folder))
                throw new InvalidOperationException($"Hub folder {hub.Folder} does not exist.");
        }
        else
        {
            if (!RepoPattern.IsMatch(hub.Repo))
                throw new InvalidOperationException(
                    "The hub repository must be a GitHub owner/name."
                );
            if (IsSameKey(hub.Repo, QuasarPluginCatalogService.DefaultHubRepo))
                throw new InvalidOperationException(
                    "MagnetarHub is always used; it doesn't need to be added."
                );
            if (hub.Branch.Any(char.IsWhiteSpace))
                throw new InvalidOperationException("The branch name can't contain spaces.");
        }

        if (existing.Any(other => IsSameKey(other.Key, hub.Key)))
            throw new InvalidOperationException(
                hub.IsLocal
                    ? "This folder is already a registered hub."
                    : "This repository is already a registered hub. Magnetar reads one branch per repository."
            );
        if (
            string.Equals(
                hub.Name,
                QuasarPluginCatalogService.DefaultHubName,
                StringComparison.OrdinalIgnoreCase
            )
            || existing.Any(other =>
                string.Equals(other.Name, hub.Name, StringComparison.OrdinalIgnoreCase)
            )
        )
            throw new InvalidOperationException(
                $"A hub named {hub.Name} already exists. Pick another name."
            );
    }

    private static bool IsSameKey(string left, string? right) =>
        string.Equals(left, right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private List<QuasarPluginHub> Load()
    {
        try
        {
            var path = GetPath();
            if (!File.Exists(path))
                return [];

            var hubs =
                JsonSerializer.Deserialize<List<QuasarPluginHub>>(
                    File.ReadAllText(path),
                    JsonOptions
                ) ?? [];
            return hubs.Select(Normalize)
                .Where(hub => hub.IsLocal || hub.Repo.Length != 0)
                .DistinctBy(hub => hub.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load Quasar plugin hubs.");
            return [];
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        List<QuasarPluginHub> snapshot;
        lock (_sync)
            snapshot = _hubs.Select(Clone).ToList();

        await AtomicFileWriter.WriteTextAsync(
            GetPath(),
            JsonSerializer.Serialize(snapshot, JsonOptions),
            cancellationToken
        );
    }

    private static QuasarPluginHub Clone(QuasarPluginHub hub) =>
        new()
        {
            Name = hub.Name,
            Repo = hub.Repo,
            Branch = hub.Branch,
            Folder = hub.Folder,
        };

    private static string GetPath() =>
        Path.Combine(MagnetarPaths.GetQuasarDirectory(), "plugin-hubs.json");
}
