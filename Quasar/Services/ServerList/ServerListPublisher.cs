using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Quasar.Services.Auth;

namespace Quasar.Services.ServerList;

/// <summary>Outbound publication only. The directory verifies diagnostics and enforces visibility leases.</summary>
public sealed class ServerListPublisher : BackgroundService
{
    private readonly ServerListOptions _options;
    private readonly DataHandlingConsentCatalog _consent;
    private readonly RbacConfigCatalog _rbac;
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ServerListListing>>> _collect;
    private readonly ILogger<ServerListPublisher> _logger;
    private readonly string _dataDirectory;
    private readonly Uri? _url;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationTokenSource _transfers = new();
    private FileStream? _processLock;
    private string? _installationId;
    private long _epoch;
    private long _sequence;
    private bool _disposed;

    public ServerListPublisher(ServerListOptions options, DataHandlingConsentCatalog consent, RbacConfigCatalog rbac,
        IHttpClientFactory factory, AgentRegistry agents, DedicatedServerCatalog servers, ClusterCatalog clusters,
        ClusterGatewayClient gateway, ILogger<ServerListPublisher> logger)
        : this(options, consent, rbac, factory.CreateClient("ServerList"),
            token => CollectAsync(options, agents, servers, clusters, gateway, token),
            MagnetarPaths.GetQuasarDirectory(), logger) { }

    internal ServerListPublisher(ServerListOptions options, DataHandlingConsentCatalog consent, RbacConfigCatalog rbac,
        HttpClient http, Func<CancellationToken, Task<IReadOnlyList<ServerListListing>>> collect,
        string dataDirectory, ILogger<ServerListPublisher> logger)
    {
        (_options, _consent, _rbac, _http, _collect, _dataDirectory, _logger) =
            (options, consent, rbac, http, collect, dataDirectory, logger);
        if (options.Enabled)
        {
            if (!Uri.TryCreate(options.DirectoryUrl, UriKind.Absolute, out var url)
                || !ServerListOptions.IsAllowedEndpoint(url) || string.IsNullOrWhiteSpace(options.InstallationToken))
                throw new InvalidDataException("Invalid server list endpoint or credential.");
            _url = new Uri(url.AbsoluteUri.TrimEnd('/') + "/");
        }
        consent.Changed += Invalidate;
        rbac.Changed += Invalidate;
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
        if (_url is null) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PublishOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                // Transport messages can contain credentials and private endpoints.
                _logger.LogWarning("Server list update deferred ({FailureType}).", exception.GetType().Name);
            }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Called serially. Every attempt has a higher sequence, including withdrawal and failed sends.
    internal async Task PublishOnceAsync(CancellationToken stoppingToken)
    {
        if (_url is null || !await InitializeIdentityAsync(stoppingToken)) return;
        DataHandlingConsentSettings consent;
        CancellationTokenSource cycle;
        long sequence;
        lock (_sync)
        {
            consent = _consent.GetSettings();
            cycle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _transfers.Token);
            sequence = checked(++_sequence);
        }
        using (cycle)
        {
            cycle.CancelAfter(TimeSpan.FromSeconds(20));
            var token = cycle.Token;
            var admins = ServerListProtocol.GetAdmins(_rbac.GetConfig());
            if (!ServerListProtocol.HasConsent(consent) || admins.Length == 0)
            {
                using var withdrawal = Request(HttpMethod.Post, "v1/quasar/withdraw");
                withdrawal.Content = JsonContent.Create(new ServerListWithdrawal(ServerListProtocol.Version,
                    _installationId!, _epoch, sequence));
                using var response = await _http.SendAsync(withdrawal, token);
                response.EnsureSuccessStatusCode();
                return;
            }

            // No server details or admin identities leave Quasar before the backend verifies log availability.
            using var challengeRequest = Request(HttpMethod.Get, "v1/quasar/challenge");
            challengeRequest.Headers.Add("X-Diagnostics-Generation", consent.Generation.ToString(CultureInfo.InvariantCulture));
            using var challengeResponse = await _http.SendAsync(challengeRequest, HttpCompletionOption.ResponseHeadersRead, token);
            challengeResponse.EnsureSuccessStatusCode();
            var challenge = await ReadChallengeAsync(challengeResponse, token);
            var now = DateTimeOffset.UtcNow;
            if (challenge.DiagnosticsGeneration != consent.Generation || challenge.ExpiresAtUtc <= now
                || challenge.ExpiresAtUtc > now.AddSeconds(30) || challenge.Nonce.Length is < 32 or > 256)
                throw new InvalidDataException("Invalid diagnostics publication challenge.");

            var listings = await _collect(token);
            using var request = Request(HttpMethod.Put, "v1/quasar/publication");
            request.Content = JsonContent.Create(new ServerListPublication(ServerListProtocol.Version, _installationId!,
                _epoch, sequence, consent.Generation, consent.DecisionDateUtc, challenge.Nonce, admins, listings));
            Task<HttpResponseMessage> sending;
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                if (!ServerListProtocol.HasConsent(_consent.GetSettings())
                    || _consent.GetSettings().Generation != consent.Generation
                    || _consent.GetSettings().DecisionDateUtc != consent.DecisionDateUtc
                    || !admins.SequenceEqual(ServerListProtocol.GetAdmins(_rbac.GetConfig()))
                    || challenge.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                    throw new OperationCanceledException(token);
                sending = _http.SendAsync(request, token);
            }
            using var publicationResponse = await sending;
            publicationResponse.EnsureSuccessStatusCode();
        }
    }

    private async Task<bool> InitializeIdentityAsync(CancellationToken token)
    {
        // Diagnostics owns this identity; do not invent a second enrollment or create it ahead of the uplink.
        var path = Path.Combine(_dataDirectory, "Diagnostics", "installation-id");
        if (!File.Exists(path)) return false;
        if (new FileInfo(path).Length > 64) throw new InvalidDataException("Invalid diagnostics identity.");
        var identity = (await File.ReadAllTextAsync(path, token)).Trim();
        if (!ServerListProtocol.IsId(identity)) throw new InvalidDataException("Invalid diagnostics identity.");
        if (_installationId is not null)
        {
            if (_installationId != identity) throw new InvalidDataException("Diagnostics identity changed; restart to re-enroll.");
            return true;
        }
        var directory = Path.Combine(_dataDirectory, "ServerList");
        Directory.CreateDirectory(directory);
        _processLock ??= new FileStream(Path.Combine(directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var epochPath = Path.Combine(directory, "epoch");
        long previous = File.Exists(epochPath)
            ? long.Parse(await File.ReadAllTextAsync(epochPath, token), CultureInfo.InvariantCulture) : 0;
        if (previous < 0) throw new InvalidDataException("Invalid publication epoch.");
        _epoch = checked(previous + 1);
        // Reserve before any network activity. A restart always fences earlier in-flight updates.
        await AtomicFileWriter.WriteTextAsync(epochPath, _epoch.ToString(CultureInfo.InvariantCulture), token);
        _installationId = identity;
        return true;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_url!, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.InstallationToken);
        request.Headers.Add("X-Installation-Id", _installationId);
        return request;
    }

    private static async Task<ServerListChallenge> ReadChallengeAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[4097];
        int size = 0, read;
        while (size < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(size), token)) > 0) size += read;
        if (size > 4096) throw new InvalidDataException("Publication challenge exceeds limit.");
        return JsonSerializer.Deserialize<ServerListChallenge>(buffer.AsSpan(0, size), new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { RespectRequiredConstructorParameters = true }) ?? throw new InvalidDataException("Empty publication challenge.");
    }

    private static async Task<IReadOnlyList<ServerListListing>> CollectAsync(ServerListOptions options,
        AgentRegistry agents, DedicatedServerCatalog servers, ClusterCatalog clusters, ClusterGatewayClient gateway,
        CancellationToken token)
    {
        var result = new List<ServerListListing>();
        var snapshots = agents.GetAgents();
        foreach (var entry in options.Listings)
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
                    result.Add(ServerListProtocol.IsFresh(status.ObservedAt, DateTimeOffset.UtcNow)
                        ? new(entry.ListingId, entry.Name, entry.PublicHost, entry.PublicPort, status.ObservedAt,
                            status.AcceptingPlayers, ServerListProtocol.NonNegative(status.Counts.ConnectedClients), null, null)
                        : ServerListProtocol.Unknown(entry));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { result.Add(ServerListProtocol.Unknown(entry)); }
            }
        }
        return result;
    }

    public override void Dispose()
    {
        _consent.Changed -= Invalidate;
        _rbac.Changed -= Invalidate;
        base.Dispose();
        lock (_sync)
        {
            _disposed = true;
            _transfers.Dispose();
        }
        _processLock?.Dispose();
    }
}
