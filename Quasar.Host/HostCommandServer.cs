using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Host;

internal sealed class AttachmentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly Dictionary<string, HostContract.HostAttachmentSpec> _attachments =
        new(StringComparer.OrdinalIgnoreCase);

    public AttachmentStore(string stateDirectory, IEnumerable<HostContract.HostAttachmentSpec> configured)
    {
        _directory = Path.Combine(stateDirectory, "attachments");
        foreach (HostContract.HostAttachmentSpec attachment in configured)
            _attachments[attachment.ClusterId] = Validate(attachment);
        if (!Directory.Exists(_directory))
            return;
        foreach (string path in Directory.GetFiles(_directory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            HostContract.HostAttachmentSpec attachment = JsonSerializer.Deserialize<HostContract.HostAttachmentSpec>(
                File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Host attachment is empty");
            _attachments[attachment.ClusterId] = Validate(attachment);
        }
    }

    public HostContract.HostAttachmentSpec[] GetAll()
    {
        lock (_sync)
            return _attachments.Values.OrderBy(item => item.ClusterId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public HostContract.HostAttachmentSpec Apply(HostContract.HostAttachmentSpec attachment)
    {
        attachment = Validate(attachment);
        lock (_sync)
        {
            WriteAtomic(AttachmentPath(attachment.ClusterId), attachment);
            _attachments[attachment.ClusterId] = attachment;
            return attachment;
        }
    }

    public static HostContract.HostAttachmentSpec Validate(HostContract.HostAttachmentSpec attachment)
    {
        string clusterId = attachment.ClusterId?.Trim() ?? string.Empty;
        if (clusterId.Length == 0 || clusterId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ArgumentException("Cluster ID must contain only letters, digits, underscores, and hyphens");
        string gatewayUrl = attachment.GatewayUrl?.Trim().TrimEnd('/') ?? string.Empty;
        if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out Uri? gateway)
            || gateway.Scheme is not ("http" or "https"))
            throw new ArgumentException("Gateway URL must be absolute HTTP(S)");
        string tokenVariable = attachment.TokenEnvironmentVariable?.Trim() ?? string.Empty;
        if (tokenVariable.Length == 0)
            throw new ArgumentException("Executor credential environment variable is required");
        bool hasManifest = !string.IsNullOrWhiteSpace(attachment.BundleManifestPath);
        if (hasManifest != !string.IsNullOrWhiteSpace(attachment.BundleManifestSha256)
            || hasManifest != !string.IsNullOrWhiteSpace(attachment.RunRoot))
            throw new ArgumentException(
                "BundleManifestPath, BundleManifestSha256, and RunRoot must be configured together");
        string? manifest = hasManifest ? RequireAbsolute(attachment.BundleManifestPath!, "Bundle manifest") : null;
        string? runRoot = hasManifest ? RequireAbsolute(attachment.RunRoot!, "Run root") : null;
        string? hash = hasManifest ? NormalizeSha256(attachment.BundleManifestSha256!) : null;
        return attachment with
        {
            ClusterId = clusterId,
            GatewayUrl = gatewayUrl,
            TokenEnvironmentVariable = tokenVariable,
            BundleManifestPath = manifest,
            BundleManifestSha256 = hash,
            RunRoot = runRoot,
        };
    }

    private string AttachmentPath(string clusterId)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clusterId))).ToLowerInvariant();
        return Path.Combine(_directory, key + ".json");
    }

    private static string RequireAbsolute(string path, string label)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException(label + " path must be absolute on the executor host");
        return Path.GetFullPath(path);
    }

    private static string NormalizeSha256(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Bundle manifest SHA-256 must contain 64 hexadecimal characters");
        return normalized;
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.GetDirectoryName(path)!,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temporary = path + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }
}

internal sealed class HostCommandServer : IDisposable
{
    private const int MaxRequestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpListener _listener = new();
    private readonly byte[] _tokenHash;
    private readonly HostExecutorConfig _config;
    private readonly AttachmentStore _attachments;
    private CancellationTokenSource? _shutdown;
    private Task? _loop;

    public HostCommandServer(HostCommandConfig command, HostExecutorConfig config, AttachmentStore attachments)
    {
        string? token = Environment.GetEnvironmentVariable(command.TokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                $"host command credential environment variable '{command.TokenEnvironmentVariable}' is not set");
        _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        _config = config;
        _attachments = attachments;
        _listener.Prefixes.Add(command.Url.TrimEnd('/') + "/");
    }

    public void Start(CancellationToken cancellationToken)
    {
        _listener.Start();
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunAsync(_shutdown.Token);
    }

    public void Dispose()
    {
        _shutdown?.Cancel();
        if (_listener.IsListening)
            _listener.Stop();
        try
        {
            _loop?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        _shutdown?.Dispose();
        _listener.Close();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            _ = HandleProtectedAsync(context, cancellationToken);
        }
    }

    private async Task HandleProtectedAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            await HandleAsync(context, cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException
            or JsonException or IOException or UnauthorizedAccessException)
        {
            await WriteErrorAsync(context, 400, "invalid_host_command", exception.Message, cancellationToken);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"host-command error={exception.GetType().Name}: {exception.Message}");
            await WriteErrorAsync(context, 500, "host_command_failed",
                "Host command failed.", cancellationToken);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (!Authenticate(context.Request.Headers["Authorization"]))
        {
            await WriteErrorAsync(context, 401, "unauthorized", "Host command credential is invalid.", cancellationToken);
            return;
        }

        string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
        if (context.Request.HttpMethod == "GET"
            && path.Equals(HostContract.HostProtocol.StatusRoute, StringComparison.OrdinalIgnoreCase))
        {
            HostContract.HostAttachmentStatus[] attachments = _attachments.GetAll().Select(ToStatus).ToArray();
            await WriteAsync(context, 200, new HostContract.HostStatus(
                _config.ExecutorId, _config.HostId, attachments), cancellationToken);
            return;
        }

        string prefix = HostContract.HostProtocol.RoutePrefix + "/attachments/";
        if (context.Request.HttpMethod == "PUT" && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string clusterId = Uri.UnescapeDataString(path[prefix.Length..]);
            HostContract.HostAttachmentSpec attachment = await ReadJsonAsync<HostContract.HostAttachmentSpec>(
                context.Request, cancellationToken);
            if (!clusterId.Equals(attachment.ClusterId, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Route cluster ID does not match the attachment body");
            await WriteAsync(context, 200, ToStatus(_attachments.Apply(attachment)), cancellationToken);
            return;
        }

        await WriteErrorAsync(context, 404, "route_not_found", "Unknown Host command route.", cancellationToken);
    }

    private bool Authenticate(string? authorization)
    {
        if (!AuthenticationHeaderValue.TryParse(authorization, out AuthenticationHeaderValue? value)
            || !value.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(value.Parameter))
            return false;
        byte[] candidate = SHA256.HashData(Encoding.UTF8.GetBytes(value.Parameter));
        return CryptographicOperations.FixedTimeEquals(_tokenHash, candidate);
    }

    private static HostContract.HostAttachmentStatus ToStatus(HostContract.HostAttachmentSpec attachment) =>
        new(attachment.ClusterId, attachment.GatewayUrl,
            !string.IsNullOrWhiteSpace(attachment.BundleManifestPath),
            attachment.BundleManifestSha256, attachment.RunRoot);

    private static async Task<T> ReadJsonAsync<T>(HttpListenerRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength64 > MaxRequestBytes)
            throw new InvalidDataException("Host command body exceeds 64 KiB");
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            int read = await request.InputStream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            if (body.Length + read > MaxRequestBytes)
                throw new InvalidDataException("Host command body exceeds 64 KiB");
            body.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize<T>(body.ToArray(), JsonOptions)
            ?? throw new InvalidDataException("Host command body is empty");
    }

    private static Task WriteAsync<T>(HttpListenerContext context, int status, T data,
        CancellationToken cancellationToken) => WriteJsonAsync(context, status,
        new HostContract.HostEnvelope<T>(HostContract.HostProtocol.Version, DateTimeOffset.UtcNow, data),
        cancellationToken);

    private static Task WriteErrorAsync(HttpListenerContext context, int status, string code,
        string message, CancellationToken cancellationToken) => WriteJsonAsync(context, status,
        new HostContract.HostErrorEnvelope(HostContract.HostProtocol.Version, DateTimeOffset.UtcNow,
            new HostContract.HostError(code, message)), cancellationToken);

    private static async Task WriteJsonAsync<T>(HttpListenerContext context, int status, T value,
        CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.Headers[HostContract.HostProtocol.HeaderName] = HostContract.HostProtocol.Version.ToString();
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
    }
}
