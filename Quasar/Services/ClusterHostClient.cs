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

    private readonly ClusterCredentialStore? _credentials;
    public ClusterHostClient(HttpClient http, ClusterCredentialStore? credentials = null)
        => (_http, _credentials) = (http, credentials);

    private string? ResolveCredential(string reference) => _credentials?.Resolve(reference) ?? Environment.GetEnvironmentVariable(reference);
    private static void TunnelAuthority(HttpRequestMessage request)
    {
        if (request.RequestUri!.Host.EndsWith(".quasar-host.invalid", StringComparison.Ordinal))
            request.Headers.Host = "127.0.0.1:" + request.RequestUri.Port;
    }

    public Task<HostContract.HostEnvelope<HostContract.HostStatus>> GetStatusAsync(
        ClusterDefinition cluster, CancellationToken cancellationToken) => SendAsync<HostContract.HostStatus>(
        cluster, HttpMethod.Get, HostContract.HostProtocol.StatusRoute, null, cancellationToken);

    public Task<HostContract.HostEnvelope<JsonElement>> InstallCredentialsAsync(ClusterDefinition target,
        HostContract.HostManagedCredentials credentials, CancellationToken token) => SendAsync<JsonElement>(target,
            HttpMethod.Put, HostContract.HostProtocol.RoutePrefix + "/managed-credentials", credentials, token);

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

    public Task<HostContract.HostEnvelope<HostContract.HostRecoveryReadiness>> CheckRecoveryAsync(
        ClusterDefinition cluster, string expectedHash, CancellationToken token) => SendAsync<HostContract.HostRecoveryReadiness>(cluster,
            HttpMethod.Get, HostContract.HostProtocol.RoutePrefix + "/recovery-readiness/" + Uri.EscapeDataString(cluster.UniqueName)
                + "?sha256=" + Uri.EscapeDataString(expectedHash), null, token);

    public Task<HostContract.HostEnvelope<HostContract.HostActiveDeployment>> ActivateDeploymentAsync(
        ClusterDefinition cluster, HostContract.HostDeploymentActivation request, CancellationToken cancellationToken) =>
        SendAsync<HostContract.HostActiveDeployment>(cluster, HttpMethod.Put,
            HostContract.HostProtocol.RoutePrefix + "/deployments/" + Uri.EscapeDataString(request.ClusterId), request, cancellationToken, longRunning: true);

    public Task<HostContract.HostEnvelope<HostContract.HostActiveDeployment>> PreviewDeploymentAsync(
        ClusterDefinition cluster, HostContract.HostDeploymentActivation request, CancellationToken cancellationToken, bool online = false) =>
        SendAsync<HostContract.HostActiveDeployment>(cluster, HttpMethod.Post,
            HostContract.HostProtocol.RoutePrefix + "/deployments/" + Uri.EscapeDataString(request.ClusterId) + (online ? "?online=true" : ""), request, cancellationToken, longRunning: true);

    public Task<HostContract.HostEnvelope<HostContract.HostPreparedConfiguration>> PrepareDeploymentAsync(
        ClusterDefinition cluster, HostContract.HostDeploymentPreparation request, CancellationToken cancellationToken) =>
        SendAsync<HostContract.HostPreparedConfiguration>(cluster, HttpMethod.Post,
            HostContract.HostProtocol.RoutePrefix + "/deployment-preparations/" + Uri.EscapeDataString(request.ClusterId), request, cancellationToken, longRunning: true);

    public Task<HostContract.HostEnvelope<HostContract.HostSnapshot>> CaptureSnapshotAsync(ClusterDefinition cluster,
        HostContract.HostSnapshotRequest request, CancellationToken token) => SendAsync<HostContract.HostSnapshot>(cluster,
            HttpMethod.Post, HostContract.HostProtocol.RoutePrefix + "/snapshots/" + Uri.EscapeDataString(cluster.UniqueName) + "/" + request.SnapshotId,
            request, token, longRunning: true);

    public Task<HostContract.HostEnvelope<JsonElement>> RestoreSnapshotAsync(ClusterDefinition cluster,
        HostContract.HostSnapshotRestore request, CancellationToken token, bool preview = false) => SendAsync<JsonElement>(cluster,
            preview ? HttpMethod.Put : HttpMethod.Post, HostContract.HostProtocol.RoutePrefix + "/restores/" + Uri.EscapeDataString(cluster.UniqueName), request, token, longRunning: true);

    public Task<HostContract.HostEnvelope<JsonElement>> ReleaseSnapshotAsync(ClusterDefinition cluster, HostContract.HostSnapshot snapshot,
        CancellationToken token) => SendAsync<JsonElement>(cluster, HttpMethod.Delete,
            HostContract.HostProtocol.RoutePrefix + "/snapshots/" + Uri.EscapeDataString(cluster.UniqueName) + "/" + snapshot.SnapshotId
                + "?sha256=" + snapshot.ArchiveSha256, null, token);

    public async Task RetrieveArtifactAsync(ClusterDefinition cluster, CometWorks.ClusterGateway.AdminContract.V1.ArtifactDescriptor artifact,
        string directory, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(2));
        token = deadline.Token;
        if (artifact.FileChecksums.Count is < 1 or > 500_000 || artifact.SizeBytes is < 0 or > 1024L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Artifact exceeds supported bounds.");
        string manifest = string.Join('\n', artifact.FileChecksums.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + p.Value));
        if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest)))
            .Equals(artifact.ManifestSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Artifact manifest checksum mismatch.");
        using var request = new HttpRequestMessage(HttpMethod.Get, cluster.HostCommandUrl + HostContract.HostProtocol.RoutePrefix
            + "/artifacts/" + Uri.EscapeDataString(cluster.UniqueName) + "/" + Uri.EscapeDataString(artifact.ArtifactId));
        TunnelAuthority(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ResolveCredential(cluster.HostCommandTokenEnvironmentVariable)
            ?? throw new InvalidOperationException("Host credential is unavailable."));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        using var input = await response.Content.ReadAsStreamAsync(token);
        using var reader = new System.Formats.Tar.TarReader(input);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;
        while (await reader.GetNextEntryAsync(cancellationToken: token) is { } entry)
        {
            if (entry.EntryType != System.Formats.Tar.TarEntryType.RegularFile || Path.IsPathRooted(entry.Name) || entry.Name.Contains('\\')
                || entry.Name.Split('/').Any(p => p is "" or "." or "..") || !seen.Add(entry.Name)
                || !artifact.FileChecksums.TryGetValue(entry.Name, out string? expected) || entry.Length > artifact.SizeBytes - bytes)
                throw new InvalidDataException("Invalid artifact entry.");
            bytes += entry.Length;
            string path = Path.Combine(directory, entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var output = new FileStream(path, FileMode.CreateNew))
            { if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, token); output.Flush(true); }
            using var file = File.OpenRead(path);
            if (!Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file, token)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Artifact content checksum mismatch.");
        }
        if (seen.Count != artifact.FileChecksums.Count || bytes != artifact.SizeBytes) throw new InvalidDataException("Incomplete artifact.");
    }

    public async Task TransferSnapshotAsync(ClusterDefinition cluster, HostContract.HostSnapshot snapshot,
        string file, bool upload, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(2));
        token = deadline.Token;
        string url = cluster.HostCommandUrl + HostContract.HostProtocol.RoutePrefix + "/snapshots/"
            + Uri.EscapeDataString(cluster.UniqueName) + "/" + snapshot.SnapshotId + (upload ? "?sha256=" + snapshot.ArchiveSha256 : "");
        using var request = new HttpRequestMessage(upload ? HttpMethod.Put : HttpMethod.Get, url);
        string credential = ResolveCredential(cluster.HostCommandTokenEnvironmentVariable)
            ?? throw new InvalidOperationException("Host credential is unavailable.");
        TunnelAuthority(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        if (upload) request.Content = new StreamContent(File.OpenRead(file));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (upload) return;
        if (response.Content.Headers.ContentLength != snapshot.ArchiveBytes)
            throw new InvalidDataException("Snapshot download length mismatch.");
        using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            total += read;
            if (total > snapshot.ArchiveBytes) throw new InvalidDataException("Snapshot download exceeds descriptor size.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        output.Flush(true);
        if (total != snapshot.ArchiveBytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(snapshot.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Snapshot download failed SHA-256 verification.");
    }

    public async Task<HostContract.HostConversionPaths> TransferConversionInputAsync(ClusterDefinition cluster, Guid id,
        string kind, string archive, string hash, CancellationToken token)
    {
        using var content = new StreamContent(File.OpenRead(archive));
        var response = await SendAsync<HostContract.HostConversionPaths>(cluster, HttpMethod.Put,
            HostContract.HostProtocol.RoutePrefix + "/conversion-inputs/" + id + "/" + kind + "?sha256=" + hash,
            content, token, longRunning: true);
        return response.Data;
    }

    private async Task<HostContract.HostEnvelope<T>> SendAsync<T>(ClusterDefinition cluster,
        HttpMethod method, string route, object? body, CancellationToken cancellationToken, bool longRunning = false)
    {
        if (string.IsNullOrWhiteSpace(cluster.HostCommandUrl))
            throw new ClusterHostException(HttpStatusCode.ServiceUnavailable, "host_command_unconfigured",
                "Cluster Host command endpoint is not configured.");
        string? token = ResolveCredential(cluster.HostCommandTokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(token))
            throw new ClusterHostException(HttpStatusCode.ServiceUnavailable, "host_credential_missing",
                $"Host credential environment variable '{cluster.HostCommandTokenEnvironmentVariable}' is not set.");
        using var request = new HttpRequestMessage(method, cluster.HostCommandUrl + route);
        TunnelAuthority(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = body as HttpContent ?? JsonContent.Create(body, options: JsonOptions);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(longRunning ? TimeSpan.FromHours(2) : TimeSpan.FromSeconds(30));
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request,
                HttpCompletionOption.ResponseContentRead, deadline.Token);
            string json = await response.Content.ReadAsStringAsync(deadline.Token);
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
                $"Cannot reach the Host executor at {new Uri(cluster.HostCommandUrl).GetComponents(UriComponents.SchemeAndServer, UriFormat.SafeUnescaped)} ({exception.HttpRequestError}). Install or start Quasar.Host on that machine and check its control port.", exception);
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
