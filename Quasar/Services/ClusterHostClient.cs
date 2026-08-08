using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quasar.Models;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Services;

public sealed class ClusterHostClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly HttpClient _http;

    public ClusterHostClient(HttpClient http) => _http = http;

    public Task<HostContract.HostEnvelope<HostContract.HostStatus>> GetStatusAsync(
        ClusterDefinition cluster, CancellationToken cancellationToken) => SendAsync<HostContract.HostStatus>(
        cluster, HttpMethod.Get, HostContract.HostProtocol.StatusRoute, null, cancellationToken);

    public Task<HostContract.HostEnvelope<HostContract.HostAttachmentStatus>> ApplyAttachmentAsync(
        ClusterDefinition cluster, HostContract.HostAttachmentSpec attachment,
        CancellationToken cancellationToken) => SendAsync<HostContract.HostAttachmentStatus>(cluster,
        HttpMethod.Put, HostContract.HostProtocol.AttachmentRoute(attachment.ClusterId),
        attachment, cancellationToken);

    public Task<HostContract.HostEnvelope<HostContract.GatewayStatus>> ApplyGatewayAsync(
        ClusterDefinition cluster, HostContract.GatewaySpec gateway,
        CancellationToken cancellationToken) => SendAsync<HostContract.GatewayStatus>(cluster,
        HttpMethod.Put, HostContract.HostProtocol.GatewayRoute(gateway.ClusterId),
        gateway, cancellationToken);

    private async Task<HostContract.HostEnvelope<T>> SendAsync<T>(ClusterDefinition cluster,
        HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cluster.HostCommandUrl))
            throw new ClusterHostException(HttpStatusCode.ServiceUnavailable, "host_command_unconfigured",
                "Cluster Host command endpoint is not configured.");
        string? token = Environment.GetEnvironmentVariable(cluster.HostCommandTokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(token))
            throw new ClusterHostException(HttpStatusCode.ServiceUnavailable, "host_credential_missing",
                $"Host credential environment variable '{cluster.HostCommandTokenEnvironmentVariable}' is not set.");
        using var request = new HttpRequestMessage(method, cluster.HostCommandUrl + route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request,
                HttpCompletionOption.ResponseContentRead, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            ValidateProtocol(response, json);
            if (!response.IsSuccessStatusCode)
            {
                HostContract.HostErrorEnvelope? error =
                    JsonSerializer.Deserialize<HostContract.HostErrorEnvelope>(json, JsonOptions);
                throw new ClusterHostException(response.StatusCode,
                    error?.Error.Code ?? "host_rejected",
                    error?.Error.Message ?? "Host rejected the command.");
            }
            return JsonSerializer.Deserialize<HostContract.HostEnvelope<T>>(json, JsonOptions)
                ?? throw ProtocolMismatch();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClusterHostException(HttpStatusCode.GatewayTimeout, "host_timeout",
                "Host command timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new ClusterHostException(HttpStatusCode.ServiceUnavailable, "host_unavailable",
                "Host command failed.", exception);
        }
        catch (JsonException exception)
        {
            throw new ClusterHostException(HttpStatusCode.BadGateway, "host_protocol_mismatch",
                "Host returned invalid contract JSON.", exception);
        }
    }

    private static void ValidateProtocol(HttpResponseMessage response, string json)
    {
        if (!response.Headers.TryGetValues(HostContract.HostProtocol.HeaderName,
                out IEnumerable<string>? values)
            || !values.SequenceEqual([HostContract.HostProtocol.Version.ToString()]))
            throw ProtocolMismatch();
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("protocolVersion", out JsonElement version)
            || version.GetInt32() != HostContract.HostProtocol.Version)
            throw ProtocolMismatch();
    }

    private static ClusterHostException ProtocolMismatch() => new(HttpStatusCode.BadGateway,
        "host_protocol_mismatch", "Host command contract version is incompatible.");
}

public sealed class ClusterHostException : Exception
{
    public ClusterHostException(HttpStatusCode statusCode, string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public HttpStatusCode StatusCode { get; }
    public string Code { get; }
}
