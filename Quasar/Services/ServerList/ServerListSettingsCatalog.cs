using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services.ServerList;

public sealed class ServerListSettingsCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private ServerListOptions _options;
    private long _revision;
    public string SettingsPath { get; }
    public string? LoadError { get; private set; }
    public event Action? Changed;

    public ServerListSettingsCatalog(IConfiguration configuration, ILogger<ServerListSettingsCatalog> logger)
        : this(Path.Combine(MagnetarPaths.GetQuasarDirectory(), "server-list.json"), configuration, logger) { }

    internal ServerListSettingsCatalog(string path, IConfiguration configuration, ILogger<ServerListSettingsCatalog> logger)
    {
        SettingsPath = path;
        try
        {
            _options = File.Exists(path)
                ? JsonSerializer.Deserialize<ServerListOptions>(File.ReadAllText(path), Json)
                    ?? throw new InvalidDataException("Empty listing settings.")
                : ServerListOptions.Create(configuration);
            _options.Validate(allowEmpty: true);
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            _options = new();
            LoadError = "Saved listing settings could not be loaded. Publishing is disabled until you save valid settings.";
            logger.LogWarning("Server listing settings disabled ({FailureType}).", error.GetType().Name);
        }
    }

    public (ServerListOptions Options, long Revision) GetSnapshot()
    {
        lock (_sync) return (_options.Clone(), _revision);
    }

    internal bool IsCurrent(long revision) { lock (_sync) return revision == _revision; }

    public async Task SaveAsync(ServerListOptions options, long expectedRevision, CancellationToken token = default)
    {
        var saved = options.Clone();
        saved.DirectoryUrl = null;
        saved.InstallationToken = null;
        foreach (var entry in saved.Listings)
        {
            entry.Name = entry.Name.Trim();
            entry.PublicHost = entry.PublicHost.Trim();
        }
        saved.Validate(allowEmpty: true);
        await _writeGate.WaitAsync(token);
        try
        {
            lock (_sync)
                if (expectedRevision != _revision)
                    throw new InvalidOperationException("Listing settings changed in another session. Reload before saving.");
            await AtomicFileWriter.WriteTextAsync(SettingsPath, JsonSerializer.Serialize(saved, Json), token);
            lock (_sync) { _options = saved; _revision++; LoadError = null; }
            Changed?.Invoke();
        }
        finally { _writeGate.Release(); }
    }

    // Generate editable drafts locally; public addresses are never guessed from admin endpoints.
    public static List<ServerListEntry> CreateDrafts(ServerListOptions saved,
        IReadOnlyList<DedicatedServerDefinition> servers, IReadOnlyList<ClusterDefinition> clusters)
    {
        var entries = saved.Listings.Select(l => l.Clone()).ToList();
        foreach (var server in servers)
            if (!entries.Any(e => e.Kind == "server" && e.UniqueName.Equals(server.UniqueName, StringComparison.OrdinalIgnoreCase)))
                entries.Add(new() { Enabled = false, Kind = "server", UniqueName = server.UniqueName,
                    Name = FirstName(server.InGameServerName, server.DisplayName, server.UniqueName),
                    PublicPort = server.ServerPort });
        foreach (var cluster in clusters)
            if (!entries.Any(e => e.Kind == "cluster" && e.UniqueName.Equals(cluster.UniqueName, StringComparison.OrdinalIgnoreCase)))
                entries.Add(new() { Enabled = false, Kind = "cluster", UniqueName = cluster.UniqueName,
                    Name = FirstName(cluster.DisplayName, cluster.UniqueName), PublicPort = ClusterSteamPort(cluster) });
        return entries.OrderBy(e => e.Kind).ThenBy(e => e.UniqueName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string FirstName(params string[] names) => names.First(n => !string.IsNullOrWhiteSpace(n));

    private static int ClusterSteamPort(ClusterDefinition cluster)
    {
        var preparation = cluster.ActiveDeployment?.Provenance?.Preparation ?? cluster.Preparation;
        if (preparation is not null)
            try
            {
                using var spec = JsonDocument.Parse(preparation.SpecificationJson);
                if (spec.RootElement.TryGetProperty("steamListen", out var listen) && listen.ValueKind == JsonValueKind.String
                    && IPEndPoint.TryParse(listen.GetString(), out var endpoint)) return endpoint.Port;
            }
            catch (JsonException) { }
        return 28000;
    }
}
