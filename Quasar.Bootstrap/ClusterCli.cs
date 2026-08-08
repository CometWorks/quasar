using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CometWorks.ClusterGateway.AdminContract.V1;
using Magnetar.Protocol.Discovery;
using Magnetar.Protocol.Runtime;

namespace Quasar.Bootstrap;

internal static class ClusterCli
{
    private const string DefaultTokenVariable = "QUASAR_API_TOKEN";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<int> RunAsync(string[] args, HttpMessageHandler? handler = null,
        TextWriter? stdout = null, TextWriter? stderr = null, CancellationToken cancellationToken = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;
        if (!TryParse(args, stderr, out Options? options))
            return 2;

        string? baseUrl = options.BaseUrl ?? Environment.GetEnvironmentVariable("QUASAR_URL")
            ?? ReadDiscoveredBaseUrl();
        if (!TryNormalizeBaseUrl(baseUrl, out string? normalizedBaseUrl))
        {
            await stderr.WriteLineAsync("Quasar URL is required (--url, QUASAR_URL, or local discovery).");
            return 3;
        }

        if (string.IsNullOrWhiteSpace(options.TokenEnvironmentVariable))
        {
            await stderr.WriteLineAsync("Token environment variable name cannot be empty.");
            return 3;
        }
        if (!TryCreateRequest(options, normalizedBaseUrl!, stderr, out HttpRequestMessage? request,
                out bool mutation))
            return 2;

        string? token = Environment.GetEnvironmentVariable(options.TokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (mutation)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", options.IdempotencyKey);

        using var client = handler == null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        try
        {
            Response response = await SendAsync(client, request, cancellationToken);
            if (response.ExitCode != 0)
            {
                await stdout.WriteLineAsync(response.Json);
                await stderr.WriteLineAsync($"HTTP {(int)response.StatusCode} ({response.StatusCode}).");
                return response.ExitCode;
            }

            if (options.Wait && response.OperationState == "Running")
            {
                if (response.OperationRoute == null)
                {
                    await stderr.WriteLineAsync("Mutation response did not identify an operation route.");
                    return 8;
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(TimeSpan.FromSeconds(options.WaitTimeoutSeconds));
                try
                {
                    do
                    {
                        await Task.Delay(500, wait.Token);
                        using var poll = new HttpRequestMessage(HttpMethod.Get,
                            new Uri(new Uri(normalizedBaseUrl!), response.OperationRoute));
                        if (!string.IsNullOrWhiteSpace(token))
                            poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        response = await SendAsync(client, poll, wait.Token);
                        if (response.ExitCode != 0) break;
                    } while (response.OperationState == "Running");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await stderr.WriteLineAsync("Operation wait timed out.");
                    return 4;
                }
            }

            await stdout.WriteLineAsync(response.Json);
            if (response.ExitCode != 0)
            {
                await stderr.WriteLineAsync($"HTTP {(int)response.StatusCode} ({response.StatusCode}).");
                return response.ExitCode;
            }
            return response.OperationState == "Failed" ? 7 : 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await stderr.WriteLineAsync("Quasar request timed out.");
            return 4;
        }
        catch (HttpRequestException exception)
        {
            await stderr.WriteLineAsync("Quasar request failed: " + exception.Message);
            return 4;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            await stderr.WriteLineAsync(exception.Message);
            return 8;
        }
        finally
        {
            request.Dispose();
        }
    }

    private static async Task<Response> SendAsync(HttpClient client, HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.SendAsync(request,
            HttpCompletionOption.ResponseContentRead, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.Headers.TryGetValues("X-Cluster-Gateway-Protocol", out IEnumerable<string>? versions)
            || !versions.SequenceEqual([AdminProtocol.Version.ToString()]))
            throw new InvalidDataException("Quasar returned an incompatible cluster protocol.");
        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("protocolVersion", out JsonElement protocol)
            || protocol.GetInt32() != AdminProtocol.Version)
            throw new InvalidDataException("Quasar returned an incompatible cluster envelope.");
        string json = JsonSerializer.Serialize(document.RootElement, JsonOptions);
        bool hasData = document.RootElement.TryGetProperty("data", out JsonElement data);
        string? state = hasData && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("state", out JsonElement stateElement)
            ? stateElement.GetString() : null;
        string? operationRoute = response.Headers.Location?.ToString();
        if (operationRoute == null && hasData && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("operationId", out JsonElement operationId))
        {
            string path = request.RequestUri!.AbsolutePath;
            int clusterEnd = path.IndexOf('/', "/api/v1/clusters/".Length);
            if (clusterEnd > 0)
                operationRoute = path[..clusterEnd] + "/operations/"
                    + Uri.EscapeDataString(operationId.GetString() ?? string.Empty);
        }
        int exitCode = response.IsSuccessStatusCode ? 0
            : response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? 5 : 6;
        return new Response(response.StatusCode, json, state, operationRoute, exitCode);
    }

    private static bool TryCreateRequest(Options options, string baseUrl, TextWriter stderr,
        out HttpRequestMessage request, out bool mutation)
    {
        request = null!;
        mutation = false;
        string[] values = options.Positionals;
        string command = values.ElementAtOrDefault(0)?.ToLowerInvariant() ?? string.Empty;
        string? cluster = values.ElementAtOrDefault(1);
        string ClusterRoute(string suffix) => $"/api/v1/clusters/{Uri.EscapeDataString(cluster!)}/{suffix}";
        string? route = command switch
        {
            "list" when values.Length == 1 => "/api/v1/clusters",
            "health" when values.Length == 2 => ClusterRoute("health"),
            "status" when values.Length == 2 => ClusterRoute("status"),
            "lifecycle" when values.Length == 2 => ClusterRoute("lifecycle"),
            "plan" when values.Length == 2 => ClusterRoute("plan"),
            "recovery-readiness" when values.Length == 2 => ClusterRoute("recovery-readiness"),
            "config" when values.Length == 2 => ClusterRoute("config"),
            "operation" when values.Length == 3 => ClusterRoute(
                "operations/" + Uri.EscapeDataString(values[2])),
            "goal" when values.Length == 3 => ClusterRoute("goal"),
            "gateway-restart" when values.Length == 2 => ClusterRoute("gateway/restart"),
            _ => null,
        };
        if (route == null)
        {
            WriteUsage(stderr);
            return false;
        }

        object? body = null;
        if (command == "goal")
        {
            string goal = values[2].ToLowerInvariant() switch
            {
                "on" => "On",
                "off" => "Off",
                _ => string.Empty,
            };
            if (goal.Length == 0)
            {
                stderr.WriteLine("Cluster goal must be on or off.");
                return false;
            }
            body = new { goal };
            mutation = true;
        }
        else if (command == "gateway-restart")
        {
            if (options.RequestId is not Guid requestId || requestId == Guid.Empty)
            {
                stderr.WriteLine("Gateway restart requires --request-id <guid>.");
                return false;
            }
            body = new GatewayRestartRequest(requestId);
            mutation = true;
        }
        if (mutation && string.IsNullOrWhiteSpace(options.IdempotencyKey))
        {
            stderr.WriteLine("Mutations require --idempotency-key <key>.");
            return false;
        }
        if (!mutation && command != "operation" && options.Wait)
        {
            stderr.WriteLine("--wait is valid only for mutations or operation polling.");
            return false;
        }

        request = new HttpRequestMessage(mutation ? HttpMethod.Put : HttpMethod.Get, baseUrl + route);
        if (command == "gateway-restart") request.Method = HttpMethod.Post;
        if (body != null) request.Content = JsonContent.Create(body, options: JsonOptions);
        return true;
    }

    private static bool TryParse(string[] args, TextWriter stderr, out Options options)
    {
        string? baseUrl = null, tokenVariable = DefaultTokenVariable, idempotencyKey = null;
        Guid? requestId = null;
        int timeout = 30, waitTimeout = 900;
        bool wait = false;
        var positionals = new List<string>();
        for (int index = 1; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument == "--wait") { wait = true; continue; }
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }
            if (index + 1 >= args.Length)
            {
                stderr.WriteLine($"Option {argument} requires a value.");
                options = null!;
                return false;
            }
            string value = args[++index];
            switch (argument)
            {
                case "--url": baseUrl = value; break;
                case "--token-env": tokenVariable = value; break;
                case "--idempotency-key": idempotencyKey = value; break;
                case "--request-id" when Guid.TryParse(value, out Guid parsed): requestId = parsed; break;
                case "--timeout" when int.TryParse(value, out int seconds) && seconds is >= 1 and <= 3600:
                    timeout = seconds; break;
                case "--wait-timeout" when int.TryParse(value, out int seconds) && seconds is >= 1 and <= 86400:
                    waitTimeout = seconds; break;
                default:
                    stderr.WriteLine($"Invalid option or value: {argument}");
                    options = null!;
                    return false;
            }
        }
        options = new Options(baseUrl, tokenVariable, idempotencyKey, requestId,
            timeout, wait, waitTimeout, positionals.ToArray());
        return true;
    }

    private static bool TryNormalizeBaseUrl(string? value, out string? normalized)
    {
        normalized = null;
        if (!Uri.TryCreate(value?.Trim().TrimEnd('/'), UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")) return false;
        normalized = uri.ToString().TrimEnd('/');
        return true;
    }

    private static string? ReadDiscoveredBaseUrl()
    {
        try
        {
            string path = MagnetarPaths.GetWebServiceManifestPath();
            return File.Exists(path)
                ? JsonSerializer.Deserialize<WebServiceDiscoveryManifest>(File.ReadAllText(path), JsonOptions)?.BaseUrl
                : null;
        }
        catch { return null; }
    }

    private static void WriteUsage(TextWriter writer) => writer.WriteLine(
        "Usage: Quasar cluster <list|health|status|lifecycle|plan|recovery-readiness|config|operation|goal|gateway-restart> [cluster] [value] [--url URL] [--token-env NAME] [--idempotency-key KEY] [--request-id GUID] [--wait]");

    private sealed record Options(string? BaseUrl, string TokenEnvironmentVariable,
        string? IdempotencyKey, Guid? RequestId, int TimeoutSeconds, bool Wait,
        int WaitTimeoutSeconds, string[] Positionals);
    private sealed record Response(HttpStatusCode StatusCode, string Json, string? OperationState,
        string? OperationRoute, int ExitCode);
}
