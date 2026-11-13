using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Quasar.Services.Auth;

namespace Quasar.Services.ServerList;

/// <summary>Signed replacement sets sent directly to the public server directory.</summary>
public sealed class ServerListPublisher : BackgroundService
{
    private readonly ServerListOptions _options;
    private readonly RbacConfigCatalog _rbac;
    private readonly HttpClient _http;
    private readonly Func<ServerListOptions, CancellationToken, Task<IReadOnlyList<ServerListListing>>> _collect;
    private readonly ILogger<ServerListPublisher> _logger;
    private readonly string _dataDirectory;
    private readonly Uri _url;
    private RSA? _key;
    private sealed record PublisherIdentity(string InstallationId, string PrivateKeyPem);
    private readonly ServerListSettingsCatalog? _settings;
    private readonly DedicatedServerCatalog? _servers;
    private readonly ClusterCatalog? _clusters;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationTokenSource _transfers = new();
    private FileStream? _processLock;
    private string? _installationId;
    private string? _failure;
    private long _epoch;
    private long _sequence;
    private bool _disposed;

    public ServerListPublisher(ServerListSettingsCatalog settings, RbacConfigCatalog rbac,
        IHttpClientFactory factory, AgentRegistry agents, DedicatedServerCatalog servers, ClusterCatalog clusters,
        ClusterGatewayClient gateway, ILogger<ServerListPublisher> logger)
        : this(rbac, factory.CreateClient("ServerList"),
            (options, token) => CollectAsync(options, agents, servers, clusters, gateway, token),
            MagnetarPaths.GetQuasarDirectory(), logger, settings)
    {
        (_servers, _clusters) = (servers, clusters);
        servers.Changed += Invalidate;
        clusters.Changed += Invalidate;
    }

    internal ServerListPublisher(RbacConfigCatalog rbac, HttpClient http,
        Func<ServerListOptions, CancellationToken, Task<IReadOnlyList<ServerListListing>>> collect,
        string dataDirectory, ILogger<ServerListPublisher> logger, ServerListSettingsCatalog? settings = null,
        ServerListOptions? options = null, Uri? endpoint = null)
    {
        (_options, _rbac, _http, _collect, _dataDirectory, _logger, _settings) =
            (options ?? new(), rbac, http, collect, dataDirectory, logger, settings);
        _url = endpoint ?? new Uri("https://se-servers.net/");
        rbac.Changed += Invalidate;
        if (settings is not null) settings.Changed += Invalidate;
    }

    private void Invalidate()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _transfers.Cancel();
            _transfers.Dispose();
            _transfers = new();
        }
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await RunOnceAsync(stoppingToken);
            var remaining = TimeSpan.FromMinutes(1) - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            try { await _wake.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Retries every minute, so an outage is logged when it starts, when its cause changes and when it ends.
    internal async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        string failure;
        try
        {
            await PublishOnceAsync(stoppingToken);
            if (_failure is not null) _logger.LogInformation("Server list update succeeded after deferral ({Failure}).", _failure);
            _failure = null;
            return;
        }
        catch (OperationCanceledException) { return; }
        catch (HttpRequestException exception) { failure = exception.StatusCode is { } code ? $"HTTP {(int)code}" : nameof(HttpRequestException); }
        // Transport messages can contain credentials and private endpoints.
        catch (Exception exception) { failure = exception.GetType().Name; }
        if (failure == _failure) _logger.LogDebug("Server list update still deferred ({Failure}).", failure);
        else _logger.LogWarning("Server list update deferred ({Failure}), retrying every minute.", failure);
        _failure = failure;
    }

    // Called serially. Every attempt has a higher sequence, including withdrawal and failed sends.
    internal async Task PublishOnceAsync(CancellationToken stoppingToken)
    {
        var (options, revision) = _settings?.GetSnapshot() ?? (_options.Clone(), 0L);
        // An installation that has never published need not create an identity or contact the directory.
        if (!options.Enabled && !Directory.Exists(Path.Combine(_dataDirectory, "ServerList"))) return;
        await InitializeIdentityAsync(stoppingToken);
        foreach (var entry in options.Listings.Where(l => l.ListingId.Length == 0))
            entry.ListingId = Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(_installationId + "\n" + entry.Kind + "\n" + entry.UniqueName.ToLowerInvariant()))[..16]);
        CancellationTokenSource cycle;
        long sequence;
        lock (_sync)
        {
            cycle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _transfers.Token);
            sequence = checked(++_sequence);
        }
        using (cycle)
        {
            cycle.CancelAfter(TimeSpan.FromSeconds(20));
            var token = cycle.Token;
            var admins = ServerListProtocol.GetAdmins(_rbac.GetConfig());
            var listings = options.Enabled && admins.Length > 0 ? await _collect(options, token) : [];
            var publication = new ServerListPublication(ServerListProtocol.Version, _installationId!, _epoch, sequence,
                DateTimeOffset.UtcNow, listings.Count == 0 ? [] : admins, listings);
            var payload = JsonSerializer.SerializeToUtf8Bytes(publication, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (payload.Length > 4 * 1024 * 1024) throw new InvalidDataException("Server list publication exceeds limit.");
            byte[] signed = [.. Encoding.ASCII.GetBytes("CometWorks.Directory.Publication.v2\n"), .. payload];
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(_url, "v2/listings"))
            {
                Content = JsonContent.Create(new SignedServerListPublication(Convert.ToBase64String(_key!.ExportSubjectPublicKeyInfo()),
                    Convert.ToBase64String(payload), Convert.ToBase64String(_key.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))))
            };
            Task<HttpResponseMessage> sending;
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                if (_settings is not null && !_settings.IsCurrent(revision)
                    || !admins.SequenceEqual(ServerListProtocol.GetAdmins(_rbac.GetConfig())))
                    throw new OperationCanceledException(token);
                sending = _http.SendAsync(request, token);
            }
            using var response = await sending;
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task InitializeIdentityAsync(CancellationToken token)
    {
        if (_installationId is not null) return;
        var directory = Path.Combine(_dataDirectory, "ServerList");
        Directory.CreateDirectory(directory);
        _processLock ??= new FileStream(Path.Combine(directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var identityPath = Path.Combine(directory, "identity.json");
        var legacyKey = Path.Combine(_dataDirectory, "Diagnostics", "machine-key.pem");
        var legacyIdentity = Path.Combine(_dataDirectory, "Diagnostics", "installation-id");
        PublisherIdentity identity;
        using var key = RSA.Create();
        if (File.Exists(identityPath))
        {
            if (new FileInfo(identityPath).Length > 16384) throw new InvalidDataException("Invalid publisher identity.");
            identity = JsonSerializer.Deserialize<PublisherIdentity>(await File.ReadAllTextAsync(identityPath, token))
                ?? throw new InvalidDataException("Invalid publisher identity.");
            key.ImportFromPem(identity.PrivateKeyPem);
        }
        else
        {
            // Adopt the existing machine identity once so listing IDs, edits and votes survive the move.
            // Diagnostics consent, enrollment and delivery are never consulted.
            if (File.Exists(legacyKey) && File.Exists(legacyIdentity))
            {
                if (new FileInfo(legacyKey).Length > 8192 || new FileInfo(legacyIdentity).Length > 64)
                    throw new InvalidDataException("Invalid legacy publisher identity.");
                key.ImportFromPem(await File.ReadAllTextAsync(legacyKey, token));
                identity = new((await File.ReadAllTextAsync(legacyIdentity, token)).Trim(), key.ExportPkcs8PrivateKeyPem());
            }
            else
            {
                key.KeySize = 3072;
                identity = new(Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..32], key.ExportPkcs8PrivateKeyPem());
            }
        }
        if (!ServerListProtocol.IsId(identity.InstallationId) || key.KeySize != 3072 || key.ExportParameters(true).D is null)
            throw new InvalidDataException("Invalid publisher identity.");
        // One atomic private file keeps the ID and its owning key inseparable across interrupted writes.
        if (!File.Exists(identityPath)) await AtomicFileWriter.WritePrivateTextAsync(identityPath, JsonSerializer.Serialize(identity), token);
        var epochPath = Path.Combine(directory, "epoch");
        long previous = File.Exists(epochPath)
            ? long.Parse(await File.ReadAllTextAsync(epochPath, token), CultureInfo.InvariantCulture) : 0;
        if (previous < 0) throw new InvalidDataException("Invalid publication epoch.");
        _epoch = checked(previous + 1);
        await AtomicFileWriter.WriteTextAsync(epochPath, _epoch.ToString(CultureInfo.InvariantCulture), token);
        _key?.Dispose();
        _key = RSA.Create(); _key.ImportParameters(key.ExportParameters(true));
        _installationId = identity.InstallationId;
    }

    private static async Task<IReadOnlyList<ServerListListing>> CollectAsync(ServerListOptions options,
        AgentRegistry agents, DedicatedServerCatalog servers, ClusterCatalog clusters, ClusterGatewayClient gateway,
        CancellationToken token)
    {
        var result = new List<ServerListListing>();
        var snapshots = agents.GetAgents();
        foreach (var entry in options.Listings.Where(l => l.Enabled))
        {
            if (entry.Kind == "server")
            {
                if (servers.GetServer(entry.UniqueName) is not null)
                    result.Add(ServerListProtocol.FromAgent(entry, snapshots, DateTimeOffset.UtcNow));
            }
            else if (clusters.GetCluster(entry.UniqueName) is { } cluster)
            {
                try
                {
                    var status = (await gateway.GetStatusAsync(cluster, token)).Data;
                    result.Add(ServerListProtocol.FromCluster(entry, status, cluster, snapshots, DateTimeOffset.UtcNow));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { result.Add(ServerListProtocol.UnknownCluster(entry)); }
            }
        }
        return result;
    }

    public override void Dispose()
    {
        _rbac.Changed -= Invalidate;
        if (_settings is not null) _settings.Changed -= Invalidate;
        if (_servers is not null) _servers.Changed -= Invalidate;
        if (_clusters is not null) _clusters.Changed -= Invalidate;
        base.Dispose();
        lock (_sync)
        {
            _disposed = true;
            _transfers.Dispose();
        }
        _key?.Dispose();
        _processLock?.Dispose();
    }
}
