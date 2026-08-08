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

    public Task<Admin.AdminEnvelope<Admin.ClusterPolicy?>> GetPolicyAsync(
        ClusterDefinition cluster, CancellationToken cancellationToken) =>
        GetAsync<Admin.ClusterPolicy?>(cluster, "config", cancellationToken);

    public Task<Admin.AdminEnvelope<Admin.ClusterPolicyApplied>> SetPolicyAsync(
        ClusterDefinition cluster, Admin.ClusterPolicy policy, CancellationToken cancellationToken) =>
        SendAsync<Admin.ClusterPolicyApplied>(cluster, "config", HttpMethod.Put, policy, cancellationToken);

    public Task<Admin.AdminEnvelope<Admin.GatewayLifecycleResult>> ShutdownAsync(
        ClusterDefinition cluster, Admin.ShutdownRequest request, CancellationToken cancellationToken) =>
        SendAsync<Admin.GatewayLifecycleResult>(cluster, "shutdown", HttpMethod.Post, request,
            cancellationToken, TimeSpan.FromSeconds(
                request.GracePeriodSeconds + request.CompletionTimeoutSeconds + 30));

    public Task<Admin.AdminEnvelope<Admin.GatewayLifecycleResult>> RestartGatewayAsync(
        ClusterDefinition cluster, Admin.GatewayRestartRequest request, CancellationToken cancellationToken) =>
        SendAsync<Admin.GatewayLifecycleResult>(cluster, "gateway/restart", HttpMethod.Post, request,
            cancellationToken);

    private async Task<Admin.AdminEnvelope<T>> GetAsync<T>(
        ClusterDefinition cluster, string route, CancellationToken cancellationToken)
        => await SendAsync<T>(cluster, route, HttpMethod.Get, null, cancellationToken);

    private async Task<Admin.AdminEnvelope<T>> SendAsync<T>(ClusterDefinition cluster, string route,
        HttpMethod method, object? body, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var request = new HttpRequestMessage(method,
            $"{cluster.GatewayUrl}{Admin.AdminProtocol.RoutePrefix}/{route}");
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
            timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
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
            return envelope ?? throw ProtocolMismatch();
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
            || version.GetInt32() != Admin.AdminProtocol.Version)
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
