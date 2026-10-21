using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Quasar.Diagnostics;
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
            try
            {
                HostContract.HostAttachmentSpec attachment = JsonSerializer.Deserialize<HostContract.HostAttachmentSpec>(
                    File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Host attachment is empty");
                _attachments[attachment.ClusterId] = Validate(attachment);
            }
            // One invalid file must not stop the Host; the cluster stays detached until it is applied again.
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException
                or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Host attachment {path} is invalid and was ignored: {exception.Message}");
            }
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

internal sealed class GatewaySpecStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        WriteIndented = true,
    };
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly Dictionary<string, HostContract.GatewaySpec> _specs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HostContract.GatewayStatus> _statuses =
        new(StringComparer.OrdinalIgnoreCase);

    public GatewaySpecStore(string stateDirectory)
    {
        _directory = Path.Combine(stateDirectory, "gateways");
        if (!Directory.Exists(_directory))
            return;
        foreach (string path in Directory.GetFiles(_directory, "*.json").OrderBy(path => path,
                     StringComparer.Ordinal))
        {
            try
            {
                HostContract.GatewaySpec spec = JsonSerializer.Deserialize<HostContract.GatewaySpec>(
                    File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Gateway spec is empty");
                spec = Validate(spec);
                _specs[spec.ClusterId] = spec;
            }
            // One invalid file must not stop the Host; that Gateway stays unmanaged until its spec is applied again.
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException
                or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Gateway spec {path} is invalid and was ignored: {exception.Message}");
            }
        }
    }

    public HostContract.GatewaySpec[] GetAll()
    {
        lock (_sync)
            return _specs.Values.OrderBy(item => item.ClusterId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public HostContract.GatewaySpec Apply(HostContract.GatewaySpec spec)
    {
        spec = Validate(spec);
        lock (_sync)
        {
            WriteAtomic(SpecPath(spec.ClusterId), spec);
            _specs[spec.ClusterId] = spec;
            return spec;
        }
    }

    public void SetStatus(HostContract.GatewayStatus status)
    {
        lock (_sync)
            _statuses[status.ClusterId] = status;
    }

    public HostContract.GatewayStatus[] GetStatuses()
    {
        lock (_sync)
            return _specs.Values.Select(spec => _statuses.GetValueOrDefault(spec.ClusterId)
                    ?? new HostContract.GatewayStatus(spec.ClusterId, spec.Goal,
                        HostContract.GatewayObservedState.Missing, spec.BundleManifestSha256,
                        spec.ConfigRevision, spec.Ports, spec.RunRoot, null, null, null))
                .OrderBy(item => item.ClusterId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static HostContract.GatewaySpec Validate(HostContract.GatewaySpec spec)
    {
        string clusterId = spec.ClusterId?.Trim() ?? string.Empty;
        if (clusterId.Length == 0 || clusterId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ArgumentException("Cluster ID must contain only letters, digits, underscores, and hyphens");
        if (!Enum.IsDefined(spec.Goal))
            throw new ArgumentException("Gateway goal is invalid");
        if (spec.StopFence is { } fence && (spec.Goal != HostContract.GatewayGoal.Off || fence.ProcessId <= 0))
            throw new ArgumentException("A stop fence requires goal Off and a positive process ID");
        string manifest = RequireAbsolute(spec.BundleManifestPath, "Bundle manifest");
        string runRoot = RequireAbsolute(spec.RunRoot, "Run root");
        string hash = NormalizeSha256(spec.BundleManifestSha256);
        string revision = spec.ConfigRevision?.Trim() ?? string.Empty;
        if (revision.Length is 0 or > 256)
            throw new ArgumentException("Gateway config revision is required and cannot exceed 256 characters");
        int[] ports = spec.Ports ?? [];
        if (ports.Length == 0 || ports.Any(port => port is < 1 or > 65535)
            || ports.Distinct().Count() != ports.Length)
            throw new ArgumentException("Gateway ports must contain unique values between 1 and 65535");
        return spec with
        {
            ClusterId = clusterId,
            BundleManifestPath = manifest,
            BundleManifestSha256 = hash,
            ConfigRevision = revision,
            Ports = ports.Order().ToArray(),
            RunRoot = runRoot,
        };
    }

    private string SpecPath(string clusterId)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clusterId))).ToLowerInvariant();
        return Path.Combine(_directory, key + ".json");
    }

    private static string RequireAbsolute(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException(label + " path must be absolute on the executor host");
        return Path.GetFullPath(path);
    }

    private static string NormalizeSha256(string value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
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
    private const int MaxRequestBytes = 20 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
    private readonly HttpListener _listener = new();
    private readonly byte[] _tokenHash;
    private readonly Quasar.Networking.MachineEndpointPolicy _policy;
    private readonly HostExecutorConfig _config;
    private readonly AttachmentStore _attachments;
    private readonly GatewaySpecStore _gateways;
    private readonly GatewayActualizer _gatewayActualizer;
    private readonly SemaphoreSlim _executionGate;
    private readonly DeploymentActivation _deployments;
    private readonly DeploymentSnapshots _snapshots;
    private readonly DiagnosticCollector _diagnostics;
    private CancellationTokenSource? _shutdown;
    private Task? _loop;

    public HostCommandServer(HostCommandConfig command, HostExecutorConfig config,
        AttachmentStore attachments, GatewaySpecStore gateways, GatewayActualizer gatewayActualizer,
        SemaphoreSlim? executionGate = null, DeploymentActivation? deployments = null, DiagnosticCollector? diagnostics = null)
    {
        string? token = Environment.GetEnvironmentVariable(command.TokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                $"host command credential environment variable '{command.TokenEnvironmentVariable}' is not set");
        _policy = new(command.AllowedSources ?? Quasar.Networking.MachineEndpointPolicy.LoopbackSources,
            command.AllowedHosts ?? [new Uri(command.Url).Authority]);
        _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        _config = config;
        _attachments = attachments;
        _gateways = gateways;
        _gatewayActualizer = gatewayActualizer;
        _executionGate = executionGate ?? new SemaphoreSlim(1, 1);
        _diagnostics = diagnostics ?? DiagnosticStorage.Create(Path.Combine(config.StateDirectory, "diagnostics"), config.HostId,
            config.DiagnosticsDirectory);
        _deployments = deployments ?? new DeploymentActivation(config.StateDirectory, config.HostId,
            attachments, gateways, new NodeActualizer(config.StateDirectory, config.HostId, _diagnostics), gatewayActualizer);
        _snapshots = new(config.StateDirectory, config.HostId, attachments, new NodeActualizer(config.StateDirectory, config.HostId, _diagnostics), gatewayActualizer);
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

    private static bool OnlineCapture(HttpListenerRequest request) => request.HttpMethod == "POST"
        && request.QueryString["online"] == "true"
        && request.Url?.AbsolutePath.StartsWith(HostContract.HostProtocol.RoutePrefix + "/snapshots/", StringComparison.Ordinal) == true;

    private async Task HandleProtectedAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        bool entered = false;
        IDisposable? storage = null;
        try
        {
            if (!_policy.Allows(context.Request.RemoteEndPoint?.Address,
                    context.Request.Headers.AllKeys.Contains("Origin", StringComparer.OrdinalIgnoreCase),
                    context.Request.Headers["Host"] ?? ""))
            {
                await WriteErrorAsync(context, 403, "forbidden", "Host command transport policy rejected the request.", cancellationToken);
                return;
            }
            if (!Authenticate(_tokenHash, context.Request.Headers["Authorization"]))
            {
                await WriteErrorAsync(context, 401, "unauthorized", "Host command credential is invalid.", cancellationToken);
                return;
            }
            // Candidate preparation writes only immutable directories and must not suspend executor heartbeats.
            bool preparing = context.Request.Url?.AbsolutePath.StartsWith(HostContract.HostProtocol.RoutePrefix + "/conversion-inputs/", StringComparison.Ordinal) == true || context.Request.HttpMethod == "POST" &&
                context.Request.Url?.AbsolutePath.StartsWith(HostContract.HostProtocol.RoutePrefix + "/deployment-preparations/", StringComparison.Ordinal) == true;
            bool diagnostics = context.Request.Url?.AbsolutePath.StartsWith(HostContract.HostProtocol.RoutePrefix + "/diagnostics/", StringComparison.Ordinal) == true;
            bool collecting = context.Request.Url?.AbsolutePath.StartsWith(HostContract.HostProtocol.RoutePrefix + "/deployment-references/", StringComparison.Ordinal) == true;
            // An online snapshot copies for minutes; holding the gate would stop executor heartbeats of a live cluster.
            bool onlineCapture = OnlineCapture(context.Request);
            if (!collecting && !diagnostics)
                storage = await Quasar.ClusterDeployment.ClusterStorageActivity.BeginAsync(cancellationToken);
            if (!preparing && !diagnostics && !collecting && !onlineCapture) { await _executionGate.WaitAsync(cancellationToken); entered = true; }
            await HandleAsync(context, cancellationToken);
        }
        catch (OperationCanceledException) { context.Response.Abort(); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException
            or JsonException or IOException or UnauthorizedAccessException)
        {
            await WriteErrorAsync(context, 400, "invalid_host_command", exception.Message, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await WriteErrorAsync(context, 409, "host_state_conflict", exception.Message, cancellationToken);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"host-command error={exception.GetType().Name}: {exception.Message}");
            await WriteErrorAsync(context, 500, "host_command_failed",
                "Host command failed.", cancellationToken);
        }
        finally
        {
            storage?.Dispose();
            if (entered) _executionGate.Release();
            context.Response.Close();
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
        string diagnosticsPrefix = HostContract.HostProtocol.RoutePrefix + "/diagnostics/";
        if (context.Request.HttpMethod == "PUT" && path == diagnosticsPrefix + "policy")
        {
            var policy = await ReadJsonAsync<HostContract.HostDiagnosticPolicy>(context.Request, cancellationToken);
            await _diagnostics.ApplyPolicyAsync(policy, cancellationToken);
            await WriteAsync(context, 200, policy, cancellationToken);
            return;
        }
        if (context.Request.HttpMethod == "GET" && path == diagnosticsPrefix + "reports")
        {
            await WriteAsync(context, 200, _diagnostics.GetReports(), cancellationToken);
            return;
        }
        if (path.StartsWith(diagnosticsPrefix + "reports/", StringComparison.Ordinal))
        {
            string id = path[(diagnosticsPrefix + "reports/").Length..];
            if (context.Request.HttpMethod == "DELETE")
            {
                _diagnostics.DeleteReport(id);
                await WriteAsync(context, 200, new { acknowledged = true }, cancellationToken);
                return;
            }
            if (context.Request.HttpMethod == "GET")
            {
                using var sharing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _diagnostics.SharingToken);
                using var input = _diagnostics.OpenReport(id);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/zip";
                context.Response.ContentLength64 = input.Length;
                await input.CopyToAsync(context.Response.OutputStream, sharing.Token);
                return;
            }
        }
        if (context.Request.HttpMethod == "PUT" && path == HostContract.HostProtocol.RoutePrefix + "/managed-credentials")
        {
            var credentials = await ReadJsonAsync<HostContract.HostManagedCredentials>(context.Request, cancellationToken);
            HostCredentials.Install(_config.StateDirectory, credentials);
            await WriteAsync(context, 200, new { installed = true }, cancellationToken);
            return;
        }
        if (context.Request.HttpMethod == "PUT" && path == HostContract.HostProtocol.SteamClientLibraryRoute)
        {
            await WriteAsync(context, 200, await SteamClientLibrary.InstallAsync(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                context.Request.QueryString["sha256"] ?? "", context.Request.InputStream, cancellationToken), cancellationToken);
            return;
        }
        if (context.Request.HttpMethod == "GET"
            && path.Equals(HostContract.HostProtocol.StatusRoute, StringComparison.OrdinalIgnoreCase))
        {
            HostContract.HostAttachmentStatus[] attachments = _attachments.GetAll().Select(ToStatus).ToArray();
            await WriteAsync(context, 200, new HostContract.HostStatus(
                _config.ExecutorId, _config.HostId, attachments, _gateways.GetStatuses(),
                GatewayStopFencing: true, SteamClientLibrary: true, StartedAt: Program.StartedAt,
                Warnings: HostShutdown.Warnings(_config.HostId)), cancellationToken);
            return;
        }

        string inputsPrefix = HostContract.HostProtocol.RoutePrefix + "/deployment-inputs/";
        if (context.Request.HttpMethod == "GET" && path.StartsWith(inputsPrefix, StringComparison.Ordinal))
        {
            try
            {
                var inputs = await Quasar.ClusterDeployment.ClusterDeploymentFiles.ReadInstalledInputsAsync(
                    Path.Combine(_config.StateDirectory, "conversions/installations"), path[inputsPrefix.Length..], cancellationToken);
                await WriteAsync(context, 200, inputs, cancellationToken);
            }
            catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
            { await WriteErrorAsync(context, 404, "installation_missing", "Deployment inputs are not installed.", cancellationToken); }
            return;
        }
        string referencesPrefix = HostContract.HostProtocol.RoutePrefix + "/deployment-references/";
        if (context.Request.HttpMethod == "POST" && path.StartsWith(referencesPrefix, StringComparison.Ordinal))
        {
            string clusterId = Uri.UnescapeDataString(path[referencesPrefix.Length..]);
            var references = await ReadJsonAsync<HostContract.HostDeploymentReferences>(context.Request, cancellationToken);
            if (references.Installations is null || references.Mods is null || references.Worlds is null || references.Configurations is null
                || references.Installations.Length + references.Mods.Length + references.Worlds.Length + references.Configurations.Length > 30_000)
                throw new InvalidDataException("Invalid deployment references.");
            using var collection = await Quasar.ClusterDeployment.ClusterStorageActivity.CollectAsync(cancellationToken);
            var activeRoots = _attachments.GetAll().Where(a => a.BundleManifestPath is not null).Select(a =>
                ExecutionBundle.ReadManifest(a.BundleManifestPath!, a.BundleManifestSha256!).ArtifactRoot
                    ?? Path.GetDirectoryName(a.BundleManifestPath!)!).ToArray();
            using (var content = await Quasar.ClusterDeployment.ClusterContentStore.OpenAsync(Path.Combine(_config.StateDirectory, "conversions"), cancellationToken))
                DeploymentConfigurationReferences.Retain(_config.StateDirectory, clusterId, references.Configurations,
                    _attachments.GetAll().Where(a => a.BundleManifestPath is not null).Select(a => Path.GetDirectoryName(a.BundleManifestPath!)!));
            int removed = await Quasar.ClusterDeployment.ClusterDeploymentFiles.CollectInstallationsAsync(
                Path.Combine(_config.StateDirectory, "conversions/installations"), clusterId,
                references.Installations.ToHashSet(StringComparer.Ordinal), activeRoots, cancellationToken);
            await Quasar.ClusterDeployment.ClusterInputOwners.RetainAsync(Path.Combine(_config.StateDirectory, "conversions/mods"),
                clusterId, references.Mods.ToHashSet(StringComparer.Ordinal), cancellationToken);
            await Quasar.ClusterDeployment.ClusterInputOwners.RetainAsync(Path.Combine(_config.StateDirectory, "conversions/worlds"),
                clusterId, references.Worlds.ToHashSet(StringComparer.Ordinal), cancellationToken);
            await WriteAsync(context, 200, removed, cancellationToken);
            return;
        }
        string modsPrefix = HostContract.HostProtocol.RoutePrefix + "/deployment-mods/";
        if (context.Request.HttpMethod == "GET" && path.StartsWith(modsPrefix, StringComparison.Ordinal))
        {
            try
            {
                var pins = await Quasar.ClusterDeployment.ClusterWorldFiles.ReadPinsAsync(
                    Path.Combine(_config.StateDirectory, "conversions/mods"), path[modsPrefix.Length..], cancellationToken);
                await WriteAsync(context, 200, pins, cancellationToken);
            }
            catch (DirectoryNotFoundException)
            { await WriteErrorAsync(context, 404, "mods_missing", "Mod inputs are not installed.", cancellationToken); }
            return;
        }
        string worldsPrefix = HostContract.HostProtocol.RoutePrefix + "/deployment-worlds/";
        if (context.Request.HttpMethod == "GET" && path.StartsWith(worldsPrefix, StringComparison.Ordinal))
        {
            try
            {
                var pins = await Quasar.ClusterDeployment.ClusterWorldFiles.ReadPinsAsync(
                    Path.Combine(_config.StateDirectory, "conversions/worlds"), path[worldsPrefix.Length..], cancellationToken);
                await WriteAsync(context, 200, pins, cancellationToken);
            }
            catch (DirectoryNotFoundException)
            { await WriteErrorAsync(context, 404, "world_missing", "World inputs are not installed.", cancellationToken); }
            return;
        }
        string conversionPrefix = HostContract.HostProtocol.RoutePrefix + "/conversion-inputs/";
        if (context.Request.HttpMethod == "PUT" && path.StartsWith(conversionPrefix, StringComparison.Ordinal))
        {
            string[] parts = path[conversionPrefix.Length..].Split('/');
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out var id) || id == Guid.Empty)
                throw new InvalidDataException("Invalid conversion identity.");
            string hash = context.Request.QueryString["sha256"] ?? "";
            string root = Path.Combine(_config.StateDirectory, "conversions");
            Directory.CreateDirectory(root);
            Quasar.ClusterDeployment.ClusterWorldFiles.Private(root);
            if (parts[1] is "mods" or "world" && context.Request.QueryString["clusterId"] is { Length: > 0 } owner)
                Quasar.ClusterDeployment.ClusterInputOwners.Track(Path.Combine(root, parts[1] == "world" ? "worlds" : "mods"), hash, owner);
            string directory = parts[1] switch
            {
                "installation" => (await Quasar.ClusterDeployment.ClusterDeploymentFiles.ImportAsync(
                    context.Request.InputStream, hash, Path.Combine(root, "installations"), cancellationToken,
                    context.Request.QueryString["baseInputsSha256"])).Directory,
                "world" => await Quasar.ClusterDeployment.ClusterWorldFiles.UnpackAsync(
                    context.Request.InputStream, hash, Path.Combine(root, "worlds"), cancellationToken,
                    baseHash: context.Request.QueryString["baseInputsSha256"]),
                "mods" => await Quasar.ClusterDeployment.ClusterWorldFiles.UnpackAsync(
                    context.Request.InputStream, hash, Path.Combine(root, "mods"), cancellationToken, requireCheckpoint: false,
                    baseHash: context.Request.QueryString["baseInputsSha256"]),
                _ => throw new InvalidDataException("Unknown conversion input."),
            };
            string workspace = Path.Combine(root, id.ToString("N"));
            await WriteAsync(context, 200, new HostContract.HostConversionPaths(_config.HostId, directory,
                Path.Combine(workspace, "configuration"), Path.Combine(workspace, "runtime")), cancellationToken);
            return;
        }
        string recoveryPrefix = HostContract.HostProtocol.RoutePrefix + "/recovery-readiness/";
        if (context.Request.HttpMethod == "GET" && path.StartsWith(recoveryPrefix, StringComparison.Ordinal))
        {
            var readiness = _deployments.CheckRecovery(Uri.UnescapeDataString(path[recoveryPrefix.Length..]),
                context.Request.QueryString["sha256"] ?? "");
            await WriteAsync(context, 200, readiness, cancellationToken);
            return;
        }
        string artifactPrefix = HostContract.HostProtocol.RoutePrefix + "/artifacts/";
        if (context.Request.HttpMethod == "GET" && path.StartsWith(artifactPrefix, StringComparison.Ordinal))
        {
            string[] parts = path[artifactPrefix.Length..].Split('/');
            if (parts.Length != 2) throw new ArgumentException("Artifact route needs a cluster and artifact ID.");
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/x-tar";
            context.Response.SendChunked = true;
            _snapshots.ExportArtifact(Uri.UnescapeDataString(parts[0]), parts[1], context.Response.OutputStream);
            return;
        }
        string snapshotsPrefix = HostContract.HostProtocol.RoutePrefix + "/snapshots/";
        if (path.StartsWith(snapshotsPrefix, StringComparison.Ordinal))
        {
            string[] parts = path[snapshotsPrefix.Length..].Split('/');
            if (parts.Length != 2 || !Guid.TryParse(parts[1], out var id) || id == Guid.Empty)
                throw new ArgumentException("Snapshot route needs a cluster ID and UUID.");
            string clusterId = Uri.UnescapeDataString(parts[0]);
            if (context.Request.HttpMethod == "POST")
            {
                var request = await ReadJsonAsync<HostContract.HostSnapshotRequest>(context.Request, cancellationToken);
                if (request.ClusterId != clusterId || request.SnapshotId != id || request.Online != OnlineCapture(context.Request))
                    throw new ArgumentException("Snapshot route identity mismatch.");
                await WriteAsync(context, 200, _snapshots.Capture(request), cancellationToken);
                return;
            }
            if (context.Request.HttpMethod == "GET")
            {
                string archive = _snapshots.ArchivePath(clusterId, id);
                _snapshots.Describe(archive, clusterId, id);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/x-tar";
                using var input = File.OpenRead(archive);
                context.Response.ContentLength64 = input.Length;
                await input.CopyToAsync(context.Response.OutputStream, cancellationToken);
                return;
            }
            if (context.Request.HttpMethod == "DELETE")
            {
                _snapshots.Release(clusterId, id, context.Request.QueryString["sha256"] ?? "");
                await WriteAsync(context, 200, new { released = true }, cancellationToken);
                return;
            }
            if (context.Request.HttpMethod == "PUT")
            {
                await _snapshots.ReceiveAsync(clusterId, id, context.Request.QueryString["sha256"] ?? "", context.Request.InputStream, cancellationToken);
                await WriteAsync(context, 200, new { accepted = true }, cancellationToken);
                return;
            }
        }
        string restorePrefix = HostContract.HostProtocol.RoutePrefix + "/restores/";
        if (context.Request.HttpMethod is "POST" or "PUT" && path.StartsWith(restorePrefix, StringComparison.Ordinal))
        {
            var request = await ReadJsonAsync<HostContract.HostSnapshotRestore>(context.Request, cancellationToken);
            if (Uri.UnescapeDataString(path[restorePrefix.Length..]) != request.ClusterId) throw new ArgumentException("Restore route identity mismatch.");
            _snapshots.Restore(request, preview: context.Request.HttpMethod == "PUT");
            await WriteAsync(context, 200, new { request.RestoreId }, cancellationToken);
            return;
        }
        string preparationPrefix = HostContract.HostProtocol.RoutePrefix + "/deployment-preparations/";
        if (context.Request.HttpMethod == "POST" && path.StartsWith(preparationPrefix, StringComparison.Ordinal))
        {
            var request = await ReadJsonAsync<HostContract.HostDeploymentPreparation>(context.Request, cancellationToken);
            if (Uri.UnescapeDataString(path[preparationPrefix.Length..]) != request.ClusterId)
                throw new ArgumentException("Route cluster ID does not match deployment preparation.");
            var prepared = await ManagedPreparation.PrepareAsync(request, _config.HostId, cancellationToken);
            DeploymentConfigurationReferences.Track(_config.StateDirectory, request.ClusterId, request.ConfigurationDirectory);
            await WriteAsync(context, 200, prepared, cancellationToken);
            return;
        }
        string prefix = HostContract.HostProtocol.RoutePrefix + "/deployments/";
        if (context.Request.HttpMethod is "PUT" or "POST" && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var request = await ReadJsonAsync<HostContract.HostDeploymentActivation>(context.Request, cancellationToken);
            if (Uri.UnescapeDataString(path[prefix.Length..]) != request.ClusterId)
                throw new ArgumentException("Route cluster ID does not match deployment activation.");
            if (context.Request.HttpMethod == "PUT") _snapshots.RefuseDuringOnlineCapture(request.ClusterId);
            await WriteAsync(context, 200, _deployments.Apply(request, preview: context.Request.HttpMethod == "POST", online: context.Request.QueryString["online"] == "true"), cancellationToken);
            return;
        }
        prefix = HostContract.HostProtocol.RoutePrefix + "/gateways/";
        if (context.Request.HttpMethod == "PUT" && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string clusterId = Uri.UnescapeDataString(path[prefix.Length..]);
            HostContract.GatewaySpec spec = await ReadJsonAsync<HostContract.GatewaySpec>(
                context.Request, cancellationToken);
            if (!clusterId.Equals(spec.ClusterId, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Route cluster ID does not match the Gateway spec body");
            if (_deployments.IsManaged(clusterId)
                && (spec.StartGeneration is null || spec.StartGeneration == Guid.Empty
                    || _attachments.GetAll().Single(item => item.ClusterId == clusterId).BundleManifestSha256 != spec.BundleManifestSha256))
                throw new InvalidOperationException("Gateway must use the active managed deployment and a nonempty start generation.");
            spec = _gateways.Apply(spec);
            HostContract.GatewayStatus status = await _gatewayActualizer.ReconcileAsync(spec, cancellationToken);
            _gateways.SetStatus(status);
            await WriteAsync(context, 200, status, cancellationToken);
            return;
        }

        prefix = HostContract.HostProtocol.RoutePrefix + "/attachments/";
        if (context.Request.HttpMethod == "PUT" && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string clusterId = Uri.UnescapeDataString(path[prefix.Length..]);
            HostContract.HostAttachmentSpec attachment = await ReadJsonAsync<HostContract.HostAttachmentSpec>(
                context.Request, cancellationToken);
            if (!clusterId.Equals(attachment.ClusterId, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Route cluster ID does not match the attachment body");
            if (_deployments.IsManaged(clusterId))
                throw new InvalidOperationException("Managed attachments change through stopped deployment activation.");
            await WriteAsync(context, 200, ToStatus(_attachments.Apply(attachment)), cancellationToken);
            return;
        }

        await WriteErrorAsync(context, 404, "route_not_found", "Unknown Host command route.", cancellationToken);
    }

    internal static bool Authenticate(byte[] tokenHash, string? authorization)
    {
        if (!AuthenticationHeaderValue.TryParse(authorization, out AuthenticationHeaderValue? value)
            || !value.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(value.Parameter))
            return false;
        byte[] candidate = SHA256.HashData(Encoding.UTF8.GetBytes(value.Parameter));
        return CryptographicOperations.FixedTimeEquals(tokenHash, candidate);
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
