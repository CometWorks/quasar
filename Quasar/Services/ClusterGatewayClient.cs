using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quasar.Models;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services;

public sealed class ClusterGatewayClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        RespectRequiredConstructorParameters = true,
    };
    private readonly HttpClient _http;

    private readonly ClusterCredentialStore? _credentials;
    public ClusterGatewayClient(HttpClient http, ClusterCredentialStore? credentials = null)
        => (_http, _credentials) = (http, credentials);

    public Task<Admin.AdminEnvelope<Admin.GatewayHealth>> GetHealthAsync(
        ClusterDefinition cluster, CancellationToken cancellationToken) =>
        GetAsync<Admin.GatewayHealth>(cluster, "health", cancellationToken);

    public async Task<Admin.AdminEnvelope<Admin.ClusterStatus>> GetStatusAsync(
        ClusterDefinition cluster, CancellationToken cancellationToken)
    {
        var envelope = await GetAsync<Admin.ClusterStatus>(cluster, "status", cancellationToken);
        if (!string.Equals(envelope.Data.ClusterId, cluster.UniqueName, StringComparison.Ordinal))
            throw new ClusterGatewayException(HttpStatusCode.BadGateway, "cluster_identity_mismatch",
                "Cluster Registry returned a different cluster identity. Check the registered Gateway address.");
        return envelope;
    }

    public Task<Admin.AdminEnvelope<Admin.NodePlan[]>> GetPlanAsync(
        ClusterDefinition cluster, CancellationToken cancellationToken) =>
        GetAsync<Admin.NodePlan[]>(cluster, "plan", cancellationToken);

    public Task<Admin.AdminEnvelope<Admin.RecoveryReadiness>> GetRecoveryReadinessAsync(
        ClusterDefinition cluster, CancellationToken cancellationToken) =>
        GetAsync<Admin.RecoveryReadiness>(cluster, "recovery-readiness", cancellationToken);

    public Task<Admin.AdminEnvelope<JsonElement>> GetDiagnosticsAsync(ClusterDefinition cluster, CancellationToken token) =>
        GetAsync<JsonElement>(cluster, "diag", token);
    public Task<Admin.AdminEnvelope<JsonElement>> GetHandoverConfigAsync(ClusterDefinition cluster, CancellationToken token) =>
        GetAsync<JsonElement>(cluster, "handover-config", token);
    public Task<Admin.AdminEnvelope<Admin.ArtifactPage>> GetArtifactsAsync(ClusterDefinition cluster, CancellationToken token) =>
        GetAsync<Admin.ArtifactPage>(cluster, "artifacts", token);
    public Task<Admin.AdminEnvelope<Admin.ArtifactDescriptor>> GetArtifactAsync(ClusterDefinition cluster, string id, CancellationToken token) =>
        GetAsync<Admin.ArtifactDescriptor>(cluster, "artifacts/" + Uri.EscapeDataString(id), token);

    public Task<Admin.AdminEnvelope<Admin.AdminCapabilities>> GetCapabilitiesAsync(
        ClusterDefinition cluster, CancellationToken token) => GetAsync<Admin.AdminCapabilities>(cluster, "capabilities", token);
    public Task<Admin.AdminEnvelope<Admin.NodeStatus[]>> GetNodesAsync(
        ClusterDefinition cluster, CancellationToken token) => GetAsync<Admin.NodeStatus[]>(cluster, "nodes", token);
    public Task<Admin.AdminEnvelope<Admin.WorldAuthorityStatus>> GetWorldAuthorityAsync(
        ClusterDefinition cluster, CancellationToken token) => GetAsync<Admin.WorldAuthorityStatus>(cluster, "world-authority", token);
    public Task<Admin.AdminEnvelope<Admin.SnapshotSummary[]>> GetSnapshotsAsync(
        ClusterDefinition cluster, CancellationToken token) => GetAsync<Admin.SnapshotSummary[]>(cluster, "snapshots", token);
    public Task<Admin.AdminEnvelope<Admin.PartitionSummary[]>> GetPartitionsAsync(
        ClusterDefinition cluster, CancellationToken token) => GetAsync<Admin.PartitionSummary[]>(cluster, "partitions", token);
    public Task<Admin.AdminEnvelope<Admin.RotationStatus>> GetRotationAsync(
        ClusterDefinition cluster, CancellationToken token) => GetAsync<Admin.RotationStatus>(cluster, "rotation", token);
    public Task<Admin.AdminEnvelope<Admin.DiagnosticsSnapshot>> GetClientDiagnosticsAsync(
        ClusterDefinition cluster, string node, CancellationToken token) =>
        GetAsync<Admin.DiagnosticsSnapshot>(cluster, "client-diagnostics?node=" + Uri.EscapeDataString(node), token);

    public Task<Admin.AdminEnvelope<Admin.OfflinePartitionCatalog>> GetOfflinePartitionsAsync(
        ClusterDefinition cluster, CancellationToken token) => GetOfflineAsync<Admin.OfflinePartitionCatalog>(cluster, "offline/partitions", token);
    public Task<Admin.AdminEnvelope<Admin.OfflineCacheView>> GetOfflineCacheAsync(
        ClusterDefinition cluster, CancellationToken token) => GetOfflineAsync<Admin.OfflineCacheView>(cluster, "offline/cache", token);
    public Task<Admin.AdminEnvelope<Admin.OfflinePartitionDetails>> GetOfflineDetailsAsync(
        ClusterDefinition cluster, string node, ulong partition, CancellationToken token)
    {
        ValidateOfflineTarget(node, partition);
        return GetOfflineAsync<Admin.OfflinePartitionDetails>(cluster,
            $"offline/details?node={Uri.EscapeDataString(node)}&partition={partition}", token);
    }
    public Task<Admin.AdminEnvelope<Admin.OfflinePrediction>> GetOfflinePredictionAsync(
        ClusterDefinition cluster, string node, ulong partition, double hours, CancellationToken token)
    {
        ValidateOfflineTarget(node, partition);
        if (!double.IsFinite(hours) || hours is < 0 or > 168)
            throw new ClusterGatewayException(HttpStatusCode.BadRequest, "invalid_arguments", "Prediction hours must be finite and between 0 and 168.");
        return GetOfflineAsync<Admin.OfflinePrediction>(cluster,
            $"offline/prediction?node={Uri.EscapeDataString(node)}&partition={partition}&hours={hours.ToString("R", CultureInfo.InvariantCulture)}", token);
    }

    internal static void ValidateOfflineTarget(string? node, ulong partition)
    {
        if (string.IsNullOrWhiteSpace(node) || node.Length > 200 || partition == 0)
            throw new ClusterGatewayException(HttpStatusCode.BadRequest, "invalid_arguments", "An exact registered node and positive partition ID are required.");
    }

    internal async Task RequireOfflineAsync(ClusterDefinition cluster, string operation, CancellationToken token, bool manage = false)
    {
        var capabilities = (await GetCapabilitiesAsync(cluster, token)).Data;
        if (!SupportsOffline(capabilities) || capabilities.Operations?.Contains(operation, StringComparer.Ordinal) != true)
            throw new ClusterGatewayException(HttpStatusCode.NotImplemented, "offline_unsupported",
                "Offline compensation administration is unsupported by this cluster package. Ordinary cluster management remains available.");
        if (manage && capabilities.Scope != Admin.AdminScope.Manage)
            throw new ClusterGatewayException(HttpStatusCode.Forbidden, "manage_scope_required", "Cluster credentials allow observation only.");
    }

    internal static bool SupportsOffline(Admin.AdminCapabilities capabilities) =>
        capabilities.Features?.TryGetValue("offlinePartitions", out string? version) == true && version == "v1";

    private async Task<Admin.AdminEnvelope<T>> GetOfflineAsync<T>(ClusterDefinition cluster, string route, CancellationToken token)
    {
        await RequireOfflineAsync(cluster, "GET " + Admin.AdminProtocol.RoutePrefix + "/" + route.Split('?')[0], token);
        var envelope = await GetAsync<T>(cluster, route, token);
        if (envelope.Data is Admin.OfflinePartitionCatalog { Partitions: null }
            or Admin.OfflinePartitionDetails { Groups: null } or Admin.OfflinePartitionDetails { Results: null }
            or Admin.OfflineCacheView { Entries: null } or Admin.OfflinePrediction { Results: null })
            throw ProtocolMismatch();
        return envelope;
    }
    public Task<Admin.AdminEnvelope<Admin.AdminConfig>> GetPolicyAsync(
        ClusterDefinition cluster, CancellationToken token) => GetAsync<Admin.AdminConfig>(cluster, "config", token);
    public Task<Admin.AdminEnvelope<Admin.ClientSummary[]>> GetClientsAsync(
        ClusterDefinition cluster, CancellationToken token) => GetAsync<Admin.ClientSummary[]>(cluster, "clients", token);
    public Task<Admin.AdminEnvelope<Admin.AdmissionBans>> GetBansAsync(
        ClusterDefinition cluster, CancellationToken token) => GetAsync<Admin.AdmissionBans>(cluster, "admission/bans", token);
    public Task<Admin.AdminEnvelope<Admin.AdminEventPage>> GetEventsAsync(
        ClusterDefinition cluster, long cursor, int limit, CancellationToken token) =>
        GetAsync<Admin.AdminEventPage>(cluster, $"events?cursor={cursor}&limit={Math.Clamp(limit, 1, 500)}", token);
    public Task<Admin.AdminEnvelope<Admin.ChatHistory>> GetChatAsync(
        ClusterDefinition cluster, long cursor, int limit, CancellationToken token) =>
        GetAsync<Admin.ChatHistory>(cluster, $"chat/history?cursor={cursor}&limit={Math.Clamp(limit, 1, 500)}", token);
    public Task<Admin.AdminEnvelope<Admin.AdminOperationPage>> GetOperationsAsync(
        ClusterDefinition cluster, CancellationToken token) => GetAsync<Admin.AdminOperationPage>(cluster, "operations", token);
    public Task<Admin.AdminEnvelope<Admin.AdminOperation>> GetOperationAsync(
        ClusterDefinition cluster, string id, CancellationToken token) =>
        GetAsync<Admin.AdminOperation>(cluster, "operations/" + Uri.EscapeDataString(id), token);

    public Task<Admin.AdminEnvelope<Admin.AdminOperation>> SetPolicyAsync(
        ClusterDefinition cluster, Admin.AdminConfigUpdate policy, string key, CancellationToken token) =>
        SendAsync<Admin.AdminOperation>(cluster, "config", HttpMethod.Put, policy, token, key);
    public Task<Admin.AdminEnvelope<Admin.AdminOperation>> ShutdownAsync(
        ClusterDefinition cluster, Admin.ShutdownRequest request, string key, CancellationToken token) =>
        SendAsync<Admin.AdminOperation>(cluster, "shutdown", HttpMethod.Post, request, token, key);
    public Task<Admin.AdminEnvelope<Admin.AdminOperation>> RestartGatewayAsync(
        ClusterDefinition cluster, string key, CancellationToken token) =>
        SendAsync<Admin.AdminOperation>(cluster, "gateway/restart", HttpMethod.Post, new { }, token, key);

    internal Task<Admin.AdminEnvelope<Admin.AdminOperation>> MutateAsync(
        ClusterDefinition cluster, string route, HttpMethod method, object? body, string key, CancellationToken token) =>
        SendAsync<Admin.AdminOperation>(cluster, route, method, body, token, key);

    private async Task<Admin.AdminEnvelope<T>> GetAsync<T>(
        ClusterDefinition cluster, string route, CancellationToken cancellationToken)
        => await SendAsync<T>(cluster, route, HttpMethod.Get, null, cancellationToken);

    private async Task<Admin.AdminEnvelope<T>> SendAsync<T>(ClusterDefinition cluster, string route,
        HttpMethod method, object? body, CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method,
            $"{cluster.GatewayUrl}{Admin.AdminProtocol.RoutePrefix}/{route}");
        if (method != HttpMethod.Get)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
                throw new ClusterGatewayException(HttpStatusCode.BadRequest, "idempotency_key_required",
                    "A stable Idempotency-Key is required for Cluster Registry mutations.");
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        if (body != null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        if (!string.IsNullOrWhiteSpace(cluster.GatewayAdminTokenEnvironmentVariable))
        {
            string? token = _credentials?.Resolve(cluster.GatewayAdminTokenEnvironmentVariable)
                ?? Environment.GetEnvironmentVariable(cluster.GatewayAdminTokenEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(token))
                throw new ClusterGatewayException(HttpStatusCode.ServiceUnavailable, "gateway_credential_missing",
                    $"Cluster Registry credential environment variable '{cluster.GatewayAdminTokenEnvironmentVariable}' is not set.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(30));
            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token);
            string json = await response.Content.ReadAsStringAsync(timeoutSource.Token);
            ValidateProtocol(response, json);
            if (!response.IsSuccessStatusCode)
            {
                Admin.AdminErrorEnvelope? error = JsonSerializer.Deserialize<Admin.AdminErrorEnvelope>(json, JsonOptions);
                throw new ClusterGatewayException(response.StatusCode,
                    error?.Error.Code ?? "gateway_rejected",
                    error?.Error.Message ?? "Cluster Registry rejected the request.");
            }

            Admin.AdminEnvelope<T>? envelope = JsonSerializer.Deserialize<Admin.AdminEnvelope<T>>(json, JsonOptions);
            return envelope is not null && envelope.Data is not null ? envelope : throw ProtocolMismatch();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClusterGatewayException(HttpStatusCode.GatewayTimeout, "gateway_timeout",
                "Cluster Registry request timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new ClusterGatewayException(HttpStatusCode.ServiceUnavailable, "gateway_unavailable",
                $"Cannot reach the Cluster Registry at {new Uri(cluster.GatewayUrl).GetComponents(UriComponents.SchemeAndServer, UriFormat.SafeUnescaped)} ({exception.HttpRequestError}). Check that the Cluster Registry is installed and running, and that its admin port is reachable from Quasar.", exception);
        }
        catch (JsonException exception)
        {
            throw new ClusterGatewayException(HttpStatusCode.BadGateway, "protocol_mismatch",
                "Cluster Registry returned invalid contract JSON.", exception);
        }
    }

    private static void ValidateProtocol(HttpResponseMessage response, string json)
    {
        if (!response.Headers.TryGetValues("X-Cluster-Gateway-Protocol", out IEnumerable<string>? values)
            || !values.SequenceEqual([Admin.AdminProtocol.Version.ToString()]))
            throw ProtocolMismatch();
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("protocolVersion", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int protocol) || protocol != Admin.AdminProtocol.Version)
            throw ProtocolMismatch();
    }

    private static ClusterGatewayException ProtocolMismatch() =>
        new(HttpStatusCode.BadGateway, "protocol_mismatch", "Cluster Registry admin contract version is incompatible.");
}

public sealed class ClusterGatewayException : Exception
{
    public bool IsConnectivityFailure => Code is "gateway_unavailable" or "gateway_timeout";
    public ClusterGatewayException(HttpStatusCode statusCode, string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public HttpStatusCode StatusCode { get; }
    public string Code { get; }
}
