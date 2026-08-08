using System.Net.Http.Headers;
using System.Net.Http.Json;
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
            return SelfTest();
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
        do
        {
            foreach (ClusterAttachment attachment in config.Attachments)
            {
                try
                {
                    await PollAsync(client, config, attachment, shutdown.Token);
                    Console.WriteLine($"cluster={attachment.ClusterId} executor={config.ExecutorId} heartbeat=accepted");
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    return 0;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException
                    or JsonException or InvalidOperationException)
                {
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

    private static async Task PollAsync(HttpClient client, HostExecutorConfig config,
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

        await SendAsync<Admin.ExecutorHeartbeatAccepted>(client, attachment, token,
            HttpMethod.Post, Admin.AdminProtocol.ExecutorHeartbeatRoute(config.ExecutorId),
            new Admin.ExecutorHeartbeatRequest([]), cancellationToken);
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
        return Normalize(config);
    }

    private static HostExecutorConfig Normalize(HostExecutorConfig config)
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
        }
        if (attachments.Select(attachment => attachment.ClusterId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != attachments.Length)
            throw new ArgumentException("Cluster attachment IDs must be unique");
        return config with { ExecutorId = executorId, HostId = hostId, Attachments = attachments };
    }

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

    private static int SelfTest()
    {
        HostExecutorConfig config = Normalize(new HostExecutorConfig("executor-a", "host-a", 2,
            [new ClusterAttachment("demo", "http://127.0.0.1:28016", "DEMO_EXECUTOR_TOKEN")]));
        if (config.Attachments.Single().ClusterId != "demo"
            || !TryParse(["run", "--config", "host.json", "--once"], out string? path, out bool once)
            || path != "host.json" || !once)
            throw new InvalidOperationException("self-test failed");
        Console.Error.WriteLine("self-test ok");
        return 0;
    }
}

internal sealed record HostExecutorConfig(
    string ExecutorId,
    string HostId,
    int PollIntervalSeconds,
    ClusterAttachment[] Attachments);

internal sealed record ClusterAttachment(
    string ClusterId,
    string GatewayUrl,
    string TokenEnvironmentVariable);
