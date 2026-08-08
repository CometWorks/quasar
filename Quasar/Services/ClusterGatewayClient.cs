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

    private async Task<Admin.AdminEnvelope<T>> GetAsync<T>(
        ClusterDefinition cluster, string route, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{cluster.GatewayUrl}{Admin.AdminProtocol.RoutePrefix}/{route}");
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
            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
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
