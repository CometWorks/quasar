using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Host;

internal static class Program
{
    private const string Usage = "Usage: Quasar.Host run --config FILE [--once] | --self-test";
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
        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        do
        {
            foreach (ClusterAttachment attachment in config.Attachments)
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
        return 0;
    }

    private static async Task PollAsync(HttpClient client, NodeActualizer actualizer, HostExecutorConfig config,
        ClusterAttachment attachment, CancellationToken cancellationToken)
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
        ClusterAttachment attachment, string token, HttpMethod method, string route,
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
        ClusterAttachment[] attachments = config.Attachments ?? [];
        if (attachments.Length == 0)
            throw new ArgumentException("At least one cluster attachment is required");
        foreach (ClusterAttachment attachment in attachments)
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
        return config with
        {
            ExecutorId = executorId,
            HostId = hostId,
            StateDirectory = stateDirectory,
            Attachments = attachments,
        };
    }

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
            [new ClusterAttachment("demo", "http://127.0.0.1:28016", "DEMO_EXECUTOR_TOKEN")]),
            Path.GetTempPath());
        if (config.Attachments.Single().ClusterId != "demo"
            || !TryParse(["run", "--config", "host.json", "--once"], out string? path, out bool once)
            || path != "host.json" || !once)
            throw new InvalidOperationException("self-test failed");

        string root = Path.Combine(Path.GetTempPath(), "quasar-host-selftest-" + Guid.NewGuid().ToString("N"));
        int? childProcessId = null;
        try
        {
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
            ]);
            string manifestPath = Path.Combine(bundleRoot, "manifest.json");
            File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
            var attachment = new ClusterAttachment("demo", "http://127.0.0.1:28016",
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
        }
        finally
        {
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
    ClusterAttachment[] Attachments,
    string StateDirectory = "");

internal sealed record ClusterAttachment(
    string ClusterId,
    string GatewayUrl,
    string TokenEnvironmentVariable,
    string? BundleManifestPath = null,
    string? BundleManifestSha256 = null,
    string? RunRoot = null);
