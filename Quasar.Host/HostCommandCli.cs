using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HostContract = global::Quasar.Host.Contract.V1;

namespace Quasar.Host;

internal static class HostCommandCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int?> TryRunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        bool status = args.Length > 0 && args[0] == "status";
        bool apply = args.Length > 1 && args[0] == "attachment" && args[1] == "apply";
        if (!status && !apply)
            return null;
        int start = status ? 1 : 2;
        if (!TryOptions(args, start, out string? url, out string? tokenVariable, out string? file)
            || apply && string.IsNullOrWhiteSpace(file) || status && file is not null)
        {
            Console.Error.WriteLine("Usage: Quasar.Host status --url URL --token-env ENV"
                + " | attachment apply --url URL --token-env ENV --file FILE");
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
            else
            {
                HostContract.HostAttachmentSpec attachment = JsonSerializer.Deserialize<HostContract.HostAttachmentSpec>(
                    File.ReadAllText(file!), JsonOptions) ?? throw new InvalidDataException("Attachment file is empty");
                request.Method = HttpMethod.Put;
                request.RequestUri = new Uri(url!.TrimEnd('/')
                    + HostContract.HostProtocol.AttachmentRoute(attachment.ClusterId));
                request.Content = JsonContent.Create(attachment, options: JsonOptions);
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
        out string? tokenVariable, out string? file)
    {
        url = null;
        tokenVariable = null;
        file = null;
        for (int index = start; index < args.Length; index++)
        {
            if (index + 1 >= args.Length)
                return false;
            string value = args[++index];
            if (args[index - 1] == "--url" && url is null)
                url = value;
            else if (args[index - 1] == "--token-env" && tokenVariable is null)
                tokenVariable = value;
            else if (args[index - 1] == "--file" && file is null)
                file = value;
            else
                return false;
        }
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
