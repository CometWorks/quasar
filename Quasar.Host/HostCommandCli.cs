using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Host;

internal static class HostCommandCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int?> TryRunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        bool status = args.Length > 0 && args[0] == "status";
        bool attachmentApply = args.Length > 1 && args[0] == "attachment" && args[1] == "apply";
        bool gatewayApply = args.Length > 1 && args[0] == "gateway" && args[1] == "apply";
        bool deploymentApply = args.Length > 1 && args[0] == "deployment" && args[1] == "activate";
        bool clusterDetach = args.Length > 1 && args[0] == "cluster" && args[1] == "detach";
        bool apply = attachmentApply || gatewayApply || deploymentApply;
        if (!status && !apply && !clusterDetach)
            return null;
        int start = status ? 1 : 2;
        if (!TryOptions(args, start, out string? url, out string? tokenVariable, out string? file,
                out string? clusterId, out string? sha256)
            || apply && string.IsNullOrWhiteSpace(file) || !apply && file is not null
            || clusterDetach != !string.IsNullOrWhiteSpace(clusterId) || !clusterDetach && sha256 is not null)
        {
            Console.Error.WriteLine("Usage: Quasar.Host status --url URL --token-env ENV"
                + " | attachment apply --url URL --token-env ENV --file FILE"
                + " | gateway apply --url URL --token-env ENV --file FILE"
                + " | deployment activate --url URL --token-env ENV --file FILE"
                + " | cluster detach --url URL --token-env ENV --cluster ID [--sha256 BUNDLE_SHA256]");
            return 2;
        }
        string? token = Environment.GetEnvironmentVariable(tokenVariable!);
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine($"credential environment variable '{tokenVariable}' is not set");
            return 2;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (status)
            {
                request.Method = HttpMethod.Get;
                request.RequestUri = new Uri(url!.TrimEnd('/') + HostContract.HostProtocol.StatusRoute);
            }
            else if (clusterDetach)
            {
                request.Method = HttpMethod.Delete;
                request.RequestUri = new Uri(url!.TrimEnd('/') + HostContract.HostProtocol.ClusterRoute(clusterId!)
                    + "?sha256=" + Uri.EscapeDataString(sha256 ?? ""));
            }
            else
            {
                request.Method = HttpMethod.Put;
                if (deploymentApply)
                {
                    var activation = JsonSerializer.Deserialize<HostContract.HostDeploymentActivation>(File.ReadAllText(file!), JsonOptions)
                        ?? throw new InvalidDataException("Deployment activation file is empty.");
                    request.RequestUri = new Uri(url!.TrimEnd('/') + HostContract.HostProtocol.RoutePrefix
                        + "/deployments/" + Uri.EscapeDataString(activation.ClusterId));
                    request.Content = JsonContent.Create(activation, options: JsonOptions);
                }
                else if (attachmentApply)
                {
                    HostContract.HostAttachmentSpec attachment =
                        JsonSerializer.Deserialize<HostContract.HostAttachmentSpec>(
                            File.ReadAllText(file!), JsonOptions)
                        ?? throw new InvalidDataException("Attachment file is empty");
                    request.RequestUri = new Uri(url!.TrimEnd('/')
                        + HostContract.HostProtocol.AttachmentRoute(attachment.ClusterId));
                    request.Content = JsonContent.Create(attachment, options: JsonOptions);
                }
                else
                {
                    HostContract.GatewaySpec gateway = JsonSerializer.Deserialize<HostContract.GatewaySpec>(
                        File.ReadAllText(file!), JsonOptions)
                        ?? throw new InvalidDataException("Gateway spec file is empty");
                    request.RequestUri = new Uri(url!.TrimEnd('/')
                        + HostContract.HostProtocol.GatewayRoute(gateway.ClusterId));
                    request.Content = JsonContent.Create(gateway, options: JsonOptions);
                }
            }
            using HttpResponseMessage response = await client.SendAsync(request,
                HttpCompletionOption.ResponseContentRead, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            ValidateProtocol(response, json);
            if (!response.IsSuccessStatusCode)
            {
                HostContract.HostErrorEnvelope? error = JsonSerializer.Deserialize<HostContract.HostErrorEnvelope>(
                    json, JsonOptions);
                Console.Error.WriteLine(error?.Error.Code ?? $"host_http_{(int)response.StatusCode}");
                return 3;
            }
            Console.WriteLine(json);
            return 0;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            or InvalidDataException or InvalidOperationException or UriFormatException)
        {
            Console.Error.WriteLine(exception.Message);
            return 4;
        }
    }

    private static bool TryOptions(string[] args, int start, out string? url,
        out string? tokenVariable, out string? file, out string? clusterId, out string? sha256)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        url = tokenVariable = file = clusterId = sha256 = null;
        for (int index = start; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length
                || args[index] is not ("--url" or "--token-env" or "--file" or "--cluster" or "--sha256")
                || !options.TryAdd(args[index], args[index + 1]))
                return false;
        }
        url = options.GetValueOrDefault("--url");
        tokenVariable = options.GetValueOrDefault("--token-env");
        file = options.GetValueOrDefault("--file");
        clusterId = options.GetValueOrDefault("--cluster");
        sha256 = options.GetValueOrDefault("--sha256");
        return Uri.TryCreate(url, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(tokenVariable);
    }

    private static void ValidateProtocol(HttpResponseMessage response, string json)
    {
        if (!response.Headers.TryGetValues(HostContract.HostProtocol.HeaderName,
                out IEnumerable<string>? values)
            || !values.SequenceEqual([HostContract.HostProtocol.Version.ToString()]))
            throw new InvalidDataException("Host protocol header is incompatible");
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("protocolVersion", out JsonElement version)
            || version.GetInt32() != HostContract.HostProtocol.Version)
            throw new InvalidDataException("Host protocol envelope is incompatible");
    }
}
