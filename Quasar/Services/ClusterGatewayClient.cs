using System.Net;
using System.Net.Http.Headers;
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

    public ClusterGatewayClient(HttpClient http) => _http = http;

    public Task<Admin.AdminEnvelope<Admin.GatewayHealth>> GetHealthAsync(
        ClusterDefinition cluster, CancellationToken cancellationToken) =>
        GetAsync<Admin.GatewayHealth>(cluster, "health", cancellationToken);

    public Task<Admin.AdminEnvelope<Admin.ClusterStatus>> GetStatusAsync(
        ClusterDefinition cluster, CancellationToken cancellationToken) =>
        GetAsync<Admin.ClusterStatus>(cluster, "status", cancellationToken);

    public Task<Admin.AdminEnvelope<Admin.NodePlan[]>> GetPlanAsync(
        ClusterDefinition cluster, CancellationToken cancellationToken) =>
        GetAsync<Admin.NodePlan[]>(cluster, "plan", cancellationToken);

    public Task<Admin.AdminEnvelope<Admin.RecoveryReadiness>> GetRecoveryReadinessAsync(
        ClusterDefinition cluster, CancellationToken cancellationToken) =>
        GetAsync<Admin.RecoveryReadiness>(cluster, "recovery-readiness", cancellationToken);

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
                    "A stable Idempotency-Key is required for Gateway mutations.");
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        if (body != null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        if (!string.IsNullOrWhiteSpace(cluster.GatewayAdminTokenEnvironmentVariable))
        {
            string? token = Environment.GetEnvironmentVariable(cluster.GatewayAdminTokenEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(token))
                throw new ClusterGatewayException(HttpStatusCode.ServiceUnavailable, "gateway_credential_missing",
                    $"Gateway credential environment variable '{cluster.GatewayAdminTokenEnvironmentVariable}' is not set.");
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
                    error?.Error.Message ?? "Gateway rejected the request.");
            }

            Admin.AdminEnvelope<T>? envelope = JsonSerializer.Deserialize<Admin.AdminEnvelope<T>>(json, JsonOptions);
            return envelope is not null && envelope.Data is not null ? envelope : throw ProtocolMismatch();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClusterGatewayException(HttpStatusCode.GatewayTimeout, "gateway_timeout",
                "Gateway request timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new ClusterGatewayException(HttpStatusCode.ServiceUnavailable, "gateway_unavailable",
                "Gateway request failed.", exception);
        }
        catch (JsonException exception)
        {
            throw new ClusterGatewayException(HttpStatusCode.BadGateway, "protocol_mismatch",
                "Gateway returned invalid contract JSON.", exception);
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
        new(HttpStatusCode.BadGateway, "protocol_mismatch", "Gateway admin contract version is incompatible.");
}

public sealed class ClusterGatewayException : Exception
{
    public ClusterGatewayException(HttpStatusCode statusCode, string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public HttpStatusCode StatusCode { get; }
    public string Code { get; }
}
