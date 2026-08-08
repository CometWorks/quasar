using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Host;

internal static class Program
{
    private const string Usage = "Usage: Quasar.Host run --config FILE [--once] | status ..."
        + " | attachment apply ... | gateway apply ... | --self-test";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.SequenceEqual(["--self-test"]))
            return await SelfTestAsync();
        if (args.SequenceEqual(["--self-test-child"]))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        int? hostCommand = await HostCommandCli.TryRunAsync(args);
        if (hostCommand.HasValue)
            return hostCommand.Value;
        if (!TryParse(args, out string? path, out bool once))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        HostExecutorConfig config;
        try
        {
            config = Load(path!);
        }
        catch (Exception exception) when (exception is IOException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var actualizer = new NodeActualizer(config.StateDirectory, config.HostId);
        var gatewayActualizer = new GatewayActualizer(config.StateDirectory, config.HostId);
        AttachmentStore attachments;
        GatewaySpecStore gateways;
        HostCommandServer? commandServer = null;
        try
        {
            attachments = new AttachmentStore(config.StateDirectory, config.Attachments);
            gateways = new GatewaySpecStore(config.StateDirectory);
            if (config.Command is not null)
            {
                commandServer = new HostCommandServer(config.Command, config, attachments,
                    gateways, gatewayActualizer);
                commandServer.Start(shutdown.Token);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or HttpListenerException)
        {
            Console.Error.WriteLine(exception.Message);
            commandServer?.Dispose();
            return 2;
        }
        using (commandServer)
        {
            var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            do
            {
                foreach (HostContract.GatewaySpec gateway in gateways.GetAll())
                {
                    HostContract.GatewayStatus status = await gatewayActualizer.ReconcileAsync(
                        gateway, shutdown.Token);
                    gateways.SetStatus(status);
                    if (once)
                        Console.WriteLine($"cluster={gateway.ClusterId} gateway={status.Observed.ToString().ToLowerInvariant()}");
                }
                foreach (HostContract.HostAttachmentSpec attachment in attachments.GetAll())
                {
                    try
                    {
                        await PollAsync(client, actualizer, config, attachment, shutdown.Token);
                        if (once || connected.Add(attachment.ClusterId))
                            Console.WriteLine($"cluster={attachment.ClusterId} executor={config.ExecutorId} heartbeat=accepted");
                    }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                    {
                        return 0;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or IOException
                        or JsonException or InvalidOperationException or UnauthorizedAccessException
                        or CryptographicException)
                    {
                        connected.Remove(attachment.ClusterId);
                        Console.Error.WriteLine($"cluster={attachment.ClusterId} error={exception.Message}");
                        if (once)
                            return 4;
                    }
                }
                if (!once)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(config.PollIntervalSeconds), shutdown.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return 0;
                    }
                }
            } while (!once);
        }
        return 0;
    }

    private static async Task PollAsync(HttpClient client, NodeActualizer actualizer, HostExecutorConfig config,
        HostContract.HostAttachmentSpec attachment, CancellationToken cancellationToken)
    {
        string? token = Environment.GetEnvironmentVariable(attachment.TokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                $"credential environment variable '{attachment.TokenEnvironmentVariable}' is not set");

        Admin.AdminEnvelope<Admin.NodePlan[]> plan = await SendAsync<Admin.NodePlan[]>(client, attachment,
            token, HttpMethod.Get, Admin.AdminProtocol.ExecutorPlanRoute(config.ExecutorId),
            null, cancellationToken);
        if (plan.Data.Any(slot => !slot.Host.Equals(config.HostId, StringComparison.Ordinal)))
            throw new InvalidOperationException("Gateway returned a slot assigned to another host");

        Admin.ExecutorObservation[] observations = await actualizer.ReconcileAsync(
            attachment, plan.Data, cancellationToken);
        await SendAsync<Admin.ExecutorHeartbeatAccepted>(client, attachment, token,
            HttpMethod.Post, Admin.AdminProtocol.ExecutorHeartbeatRoute(config.ExecutorId),
            new Admin.ExecutorHeartbeatRequest(observations), cancellationToken);
    }

    private static async Task<Admin.AdminEnvelope<T>> SendAsync<T>(HttpClient client,
        HostContract.HostAttachmentSpec attachment, string token, HttpMethod method, string route,
        object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, attachment.GatewayUrl.TrimEnd('/') + route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        using HttpResponseMessage response = await client.SendAsync(request,
            HttpCompletionOption.ResponseContentRead, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        ValidateProtocol(response, json);
        if (!response.IsSuccessStatusCode)
        {
            Admin.AdminErrorEnvelope? error = JsonSerializer.Deserialize<Admin.AdminErrorEnvelope>(json, JsonOptions);
            throw new InvalidOperationException(error?.Error.Code ?? $"gateway_http_{(int)response.StatusCode}");
        }
        Admin.AdminEnvelope<T>? envelope = JsonSerializer.Deserialize<Admin.AdminEnvelope<T>>(json, JsonOptions);
        return envelope ?? throw new InvalidOperationException("Gateway returned an empty contract envelope");
    }

    private static void ValidateProtocol(HttpResponseMessage response, string json)
    {
        if (!response.Headers.TryGetValues("X-Cluster-Gateway-Protocol", out IEnumerable<string>? values)
            || !values.SequenceEqual([Admin.AdminProtocol.Version.ToString()]))
            throw new InvalidOperationException("Gateway protocol header is incompatible");
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("protocolVersion", out JsonElement version)
            || version.GetInt32() != Admin.AdminProtocol.Version)
            throw new InvalidOperationException("Gateway protocol envelope is incompatible");
    }

    private static HostExecutorConfig Load(string path)
    {
        HostExecutorConfig config = JsonSerializer.Deserialize<HostExecutorConfig>(File.ReadAllText(path), JsonOptions)
            ?? throw new ArgumentException("Host executor config is empty");
        return Normalize(config, Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    private static HostExecutorConfig Normalize(HostExecutorConfig config, string configDirectory)
    {
        string executorId = config.ExecutorId?.Trim() ?? string.Empty;
        string hostId = config.HostId?.Trim() ?? string.Empty;
        if (executorId.Length == 0 || hostId.Length == 0)
            throw new ArgumentException("ExecutorId and HostId are required");
        if (config.PollIntervalSeconds is < 1 or > 300)
            throw new ArgumentException("PollIntervalSeconds must be between 1 and 300");
        HostContract.HostAttachmentSpec[] attachments = config.Attachments ?? [];
        if (attachments.Length == 0)
            throw new ArgumentException("At least one cluster attachment is required");
        foreach (HostContract.HostAttachmentSpec attachment in attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment.ClusterId)
                || string.IsNullOrWhiteSpace(attachment.TokenEnvironmentVariable)
                || !Uri.TryCreate(attachment.GatewayUrl, UriKind.Absolute, out Uri? gateway)
                || gateway.Scheme is not ("http" or "https"))
                throw new ArgumentException("Each attachment requires a cluster ID, HTTP(S) Gateway URL, and credential variable");
            bool hasManifest = !string.IsNullOrWhiteSpace(attachment.BundleManifestPath);
            if (hasManifest != !string.IsNullOrWhiteSpace(attachment.BundleManifestSha256)
                || hasManifest != !string.IsNullOrWhiteSpace(attachment.RunRoot))
                throw new ArgumentException(
                    "BundleManifestPath, BundleManifestSha256, and RunRoot must be configured together");
        }
        if (attachments.Select(attachment => attachment.ClusterId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != attachments.Length)
            throw new ArgumentException("Cluster attachment IDs must be unique");
        attachments = attachments.Select(attachment => attachment with
        {
            BundleManifestPath = ResolveOptionalPath(configDirectory, attachment.BundleManifestPath),
            RunRoot = ResolveOptionalPath(configDirectory, attachment.RunRoot),
        }).ToArray();
        string stateDirectory = string.IsNullOrWhiteSpace(config.StateDirectory)
            ? Path.Combine(configDirectory, "host-state")
            : ResolvePath(configDirectory, config.StateDirectory);
        HostCommandConfig? command = config.Command;
        if (command is not null)
        {
            string url = command.Url?.Trim().TrimEnd('/') ?? string.Empty;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? commandUri)
                || commandUri.Scheme != "http" || commandUri.AbsolutePath != "/"
                || !IsLoopbackHost(commandUri.Host))
                throw new ArgumentException("Host command URL must be an HTTP loopback origin");
            string tokenVariable = command.TokenEnvironmentVariable?.Trim() ?? string.Empty;
            if (tokenVariable.Length == 0)
                throw new ArgumentException("Host command credential environment variable is required");
            command = command with { Url = url, TokenEnvironmentVariable = tokenVariable };
        }
        return config with
        {
            ExecutorId = executorId,
            HostId = hostId,
            StateDirectory = stateDirectory,
            Attachments = attachments,
            Command = command,
        };
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);

    private static string? ResolveOptionalPath(string directory, string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : ResolvePath(directory, path);

    private static string ResolvePath(string directory, string path) => Path.GetFullPath(
        Path.IsPathFullyQualified(path) ? path : Path.Combine(directory, path));

    private static bool TryParse(string[] args, out string? path, out bool once)
    {
        path = null;
        once = false;
        if (args.Length < 3 || args[0] != "run")
            return false;
        for (int index = 1; index < args.Length; index++)
        {
            if (args[index] == "--config" && index + 1 < args.Length)
                path = args[++index];
            else if (args[index] == "--once")
                once = true;
            else
                return false;
        }
        return !string.IsNullOrWhiteSpace(path);
    }

    private static async Task<int> SelfTestAsync()
    {
        HostExecutorConfig config = Normalize(new HostExecutorConfig("executor-a", "host-a", 2,
            [new HostContract.HostAttachmentSpec("demo", "http://127.0.0.1:28016", "DEMO_EXECUTOR_TOKEN")]),
            Path.GetTempPath());
        if (config.Attachments.Single().ClusterId != "demo"
            || !TryParse(["run", "--config", "host.json", "--once"], out string? path, out bool once)
            || path != "host.json" || !once)
            throw new InvalidOperationException("self-test failed");

        string root = Path.Combine(Path.GetTempPath(), "quasar-host-selftest-" + Guid.NewGuid().ToString("N"));
        const string commandTokenVariable = "QUASAR_HOST_SELF_TEST_TOKEN";
        string? previousCommandToken = Environment.GetEnvironmentVariable(commandTokenVariable);
        int? childProcessId = null;
        int? gatewayProcessId = null;
        try
        {
            int commandPort;
            var commandProbe = new TcpListener(IPAddress.Loopback, 0);
            commandProbe.Start();
            commandPort = ((IPEndPoint)commandProbe.LocalEndpoint).Port;
            commandProbe.Stop();
            string commandToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            Environment.SetEnvironmentVariable(commandTokenVariable, commandToken);
            string commandUrl = $"http://127.0.0.1:{commandPort}";
            HostExecutorConfig commandConfig = config with
            {
                StateDirectory = Path.Combine(root, "command-state"),
                Command = new HostCommandConfig(commandUrl, commandTokenVariable),
            };
            var attachmentStore = new AttachmentStore(commandConfig.StateDirectory, commandConfig.Attachments);
            var gatewayStore = new GatewaySpecStore(commandConfig.StateDirectory);
            var commandGatewayActualizer = new GatewayActualizer(commandConfig.StateDirectory, commandConfig.HostId);
            using (var commandShutdown = new CancellationTokenSource())
            using (var commandServer = new HostCommandServer(commandConfig.Command, commandConfig,
                       attachmentStore, gatewayStore, commandGatewayActualizer))
            {
                commandServer.Start(commandShutdown.Token);
                using var commandClient = new HttpClient();
                commandClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", commandToken);
                using HttpResponseMessage status = await commandClient.GetAsync(
                    commandUrl + HostContract.HostProtocol.StatusRoute);
                if (!status.IsSuccessStatusCode
                    || status.Headers.GetValues(HostContract.HostProtocol.HeaderName).Single() != "1")
                    throw new InvalidOperationException("self-test host command status failed");
                var updatedAttachment = new HostContract.HostAttachmentSpec("demo",
                    "http://127.0.0.1:29000", "DEMO_EXECUTOR_TOKEN");
                using HttpResponseMessage applied = await commandClient.PutAsJsonAsync(
                    commandUrl + HostContract.HostProtocol.AttachmentRoute("demo"), updatedAttachment, JsonOptions);
                if (!applied.IsSuccessStatusCode
                    || attachmentStore.GetAll().Single().GatewayUrl != "http://127.0.0.1:29000")
                    throw new InvalidOperationException("self-test host attachment apply failed");
                commandShutdown.Cancel();
            }

            string bundleRoot = Path.Combine(root, "bundle");
            string runRoot = Path.Combine(root, "runs");
            string stateRoot = Path.Combine(root, "state");
            Directory.CreateDirectory(bundleRoot);
            var files = new List<BundleFile>();
            foreach (string source in Directory.GetFiles(AppContext.BaseDirectory))
            {
                string name = Path.GetFileName(source);
                string target = Path.Combine(bundleRoot, name);
                File.Copy(source, target);
                files.Add(new BundleFile(name, ComputeSha256(target)));
            }
            string executable = OperatingSystem.IsWindows() ? "Quasar.Host.exe" : "Quasar.Host";
            string copiedExecutable = Path.Combine(bundleRoot, executable);
            if (!File.Exists(copiedExecutable))
                throw new InvalidOperationException("self-test app host is unavailable");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(copiedExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);

            int reservedPort;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            reservedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var manifest = new BundleManifest(1, "self-test", files.ToArray(),
            [
                new NodeSpawnSpec("slot-a", Admin.NodeRole.Regular, "node-a", executable, string.Empty,
                    ["--self-test-child"], [], [reservedPort], 30),
            ], new GatewaySpawnSpec(executable, string.Empty, ["--self-test-child"], []));
            string manifestPath = Path.Combine(bundleRoot, "manifest.json");
            File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
            var gatewaySpec = new HostContract.GatewaySpec("demo", HostContract.GatewayGoal.On,
                manifestPath, ComputeSha256(manifestPath), "config-self-test", [reservedPort],
                Path.Combine(root, "gateway-run"));
            var persistedGateways = new GatewaySpecStore(stateRoot);
            gatewaySpec = persistedGateways.Apply(gatewaySpec);
            HostContract.GatewayStatus gatewayRunning = await new GatewayActualizer(stateRoot, "host-a")
                .ReconcileAsync(gatewaySpec, CancellationToken.None);
            gatewayProcessId = gatewayRunning.ProcessId;
            HostContract.GatewayStatus gatewayReadopted = await new GatewayActualizer(stateRoot, "host-a")
                .ReconcileAsync(gatewaySpec, CancellationToken.None);
            HostContract.GatewayStatus gatewayMismatch = await new GatewayActualizer(stateRoot, "host-a")
                .ReconcileAsync(gatewaySpec with { ConfigRevision = "config-other" }, CancellationToken.None);
            if (gatewayRunning.Observed != HostContract.GatewayObservedState.Running
                || gatewayProcessId is null || gatewayReadopted.ProcessId != gatewayProcessId
                || gatewayMismatch.Observed != HostContract.GatewayObservedState.Failed
                || Process.GetProcessById(gatewayProcessId.Value).HasExited
                || new GatewaySpecStore(stateRoot).GetAll().Single().ConfigRevision != "config-self-test")
                throw new InvalidOperationException("self-test Gateway start/re-adoption failed");
            HostContract.GatewayStatus gatewayStopped = await new GatewayActualizer(stateRoot, "host-a")
                .ReconcileAsync(gatewaySpec with { Goal = HostContract.GatewayGoal.Off }, CancellationToken.None);
            if (gatewayStopped.Observed != HostContract.GatewayObservedState.Missing
                || gatewayStopped.ProcessId is not null || gatewayStopped.LaunchedAt is not null)
                throw new InvalidOperationException("self-test exact Gateway stop failed");
            gatewayProcessId = null;

            var attachment = new HostContract.HostAttachmentSpec("demo", "http://127.0.0.1:28016",
                "DEMO_EXECUTOR_TOKEN", manifestPath, ComputeSha256(manifestPath), runRoot);
            var wanted = new Admin.NodePlan("slot-a", "host-a", null, Admin.NodeRole.Regular,
                Admin.NodeGoal.Wanted, Admin.NodeObservation.Missing, null, Admin.IncumbentAction.None,
                null, 0, null, null, null, null, 0, false, true);

            var actualizer = new NodeActualizer(stateRoot, "host-a");
            Admin.ExecutorObservation spawning = (await actualizer.ReconcileAsync(
                attachment, [wanted], CancellationToken.None)).Single();
            if (spawning.State != Admin.NodeObservation.Spawning)
                throw new InvalidOperationException("self-test spawn failed: " + spawning.Failure);

            string recordPath = Directory.GetFiles(Path.Combine(stateRoot, "launch-records"), "*.json").Single();
            LaunchRecord record = JsonSerializer.Deserialize<LaunchRecord>(File.ReadAllText(recordPath), JsonOptions)
                ?? throw new InvalidOperationException("self-test launch record is empty");
            childProcessId = record.ProcessId;
            string slotDirectory = Directory.GetDirectories(runRoot).Single();
            var receipt = new ReadyReceipt(1, "demo", "slot-a", record.AttemptKey,
                "node-a", 7, "127.0.0.1:30000", record.ProcessId!.Value);
            File.WriteAllBytes(Path.Combine(slotDirectory, ".quasar-node-ready.json"),
                JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions));

            actualizer = new NodeActualizer(stateRoot, "host-a");
            Admin.ExecutorObservation ready = (await actualizer.ReconcileAsync(
                attachment, [wanted], CancellationToken.None)).Single();
            if (ready.State != Admin.NodeObservation.Ready || ready.Node != "node-a")
                throw new InvalidOperationException("self-test re-adoption failed");

            Admin.NodePlan kill = wanted with
            {
                Observed = Admin.NodeObservation.Ready,
                ObservedNode = "node-a",
                IncumbentAction = Admin.IncumbentAction.Kill,
                IncumbentNode = "node-a",
                IncumbentEpoch = 7,
                SpawnAllowed = false,
            };
            Admin.ExecutorObservation gone = (await actualizer.ReconcileAsync(
                attachment, [kill], CancellationToken.None)).Single();
            if (gone.State != Admin.NodeObservation.Gone)
                throw new InvalidOperationException("self-test exact kill failed");
            childProcessId = null;

            using var conflict = new TcpListener(IPAddress.Loopback, reservedPort);
            conflict.Start();
            Admin.ExecutorObservation blocked = (await actualizer.ReconcileAsync(
                attachment, [wanted], CancellationToken.None)).Single();
            if (blocked.State != Admin.NodeObservation.Failed
                || !blocked.Failure!.StartsWith("unmanaged_conflict:", StringComparison.Ordinal))
                throw new InvalidOperationException("self-test port conflict was not fail-closed");

            if (!OperatingSystem.IsWindows())
            {
                const string linkedExecutable = "linked-host";
                File.CreateSymbolicLink(Path.Combine(bundleRoot, linkedExecutable), executable);
                var linkedManifest = manifest with
                {
                    Revision = "self-test-linked",
                    Files = [.. files, new BundleFile(linkedExecutable, ComputeSha256(copiedExecutable))],
                    Nodes =
                    [
                        new NodeSpawnSpec("slot-a", Admin.NodeRole.Regular, "node-a", linkedExecutable,
                            string.Empty, ["--self-test-child"], [], [reservedPort], 30),
                    ],
                };
                File.WriteAllBytes(manifestPath,
                    JsonSerializer.SerializeToUtf8Bytes(linkedManifest, JsonOptions));
                HostContract.HostAttachmentSpec linkedAttachment = attachment with
                {
                    BundleManifestSha256 = ComputeSha256(manifestPath),
                };
                Admin.ExecutorObservation linked = (await actualizer.ReconcileAsync(
                    linkedAttachment, [wanted], CancellationToken.None)).Single();
                if (linked.State != Admin.NodeObservation.Failed
                    || !linked.Failure!.Contains("symbolic links", StringComparison.Ordinal))
                    throw new InvalidOperationException("self-test bundle symlink was not rejected");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(commandTokenVariable, previousCommandToken);
            if (childProcessId is int pid)
            {
                try
                {
                    using Process process = Process.GetProcessById(pid);
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                catch (ArgumentException)
                {
                }
            }
            if (gatewayProcessId is int gatewayPid)
            {
                try
                {
                    using Process process = Process.GetProcessById(gatewayPid);
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                catch (ArgumentException)
                {
                }
            }
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        Console.Error.WriteLine("self-test ok");
        return 0;
    }

    private static string ComputeSha256(string path)
    {
        using FileStream file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
}

internal sealed record HostExecutorConfig(
    string ExecutorId,
    string HostId,
    int PollIntervalSeconds,
    HostContract.HostAttachmentSpec[] Attachments,
    string StateDirectory = "",
    HostCommandConfig? Command = null);

internal sealed record HostCommandConfig(string Url, string TokenEnvironmentVariable);
