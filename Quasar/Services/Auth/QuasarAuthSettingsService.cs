using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magnetar.Protocol.Runtime;
using Quasar.Services;

namespace Quasar.Services.Auth;

public sealed class QuasarAuthSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly QuasarAuthOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public QuasarAuthSettingsService(QuasarAuthOptions options)
    {
        _options = options;
    }

    public string SettingsPath => Path.Combine(MagnetarPaths.GetQuasarDirectory(), "appsettings.json");

    public QuasarTrustedNetworkSettings GetTrustedNetworkSettings()
    {
        var bypass = _options.TrustedNetworkBypass;
        return new QuasarTrustedNetworkSettings
        {
            AllowLoopback = bypass.AllowLoopback,
            AllowSameSubnet = bypass.AllowSameSubnet,
            TrustedProxies = bypass.TrustedProxies.ToList(),
        };
    }

    public async Task SaveTrustedNetworkSettingsAsync(
        QuasarTrustedNetworkSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalizedProxies = NormalizeTrustedProxies(settings.TrustedProxies);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = await ReadAppSettingsAsync(cancellationToken).ConfigureAwait(false);
            var quasar = GetOrCreateObject(root, "Quasar");
            var auth = GetOrCreateObject(quasar, "Auth");
            var bypass = GetOrCreateObject(auth, "TrustedNetworkBypass");

            bypass["AllowLoopback"] = settings.AllowLoopback;
            bypass["AllowSameSubnet"] = settings.AllowSameSubnet;

            var proxies = new JsonArray();
            foreach (var proxy in normalizedProxies)
                proxies.Add(proxy);
            bypass["TrustedProxies"] = proxies;

            await AtomicFileWriter.WriteTextAsync(
                    SettingsPath,
                    root.ToJsonString(JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);

            _options.TrustedNetworkBypass.AllowLoopback = settings.AllowLoopback;
            _options.TrustedNetworkBypass.AllowSameSubnet = settings.AllowSameSubnet;
            _options.TrustedNetworkBypass.TrustedProxies = normalizedProxies;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static List<string> ParseTrustedProxiesText(string text) =>
        NormalizeTrustedProxies(text.Split(
            ['\r', '\n', ',', ';', ' '],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private async Task<JsonObject> ReadAppSettingsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(SettingsPath))
            return new JsonObject();

        var text = await File.ReadAllTextAsync(SettingsPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject();

        return JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
    }

    private static List<string> NormalizeTrustedProxies(IEnumerable<string>? values)
    {
        var normalized = new List<string>();
        foreach (var value in values ?? [])
        {
            var candidate = value.Trim();
            if (candidate.Length == 0)
                continue;

            if (!IsValidProxyEntry(candidate))
                throw new InvalidOperationException($"'{candidate}' is not a valid IP address or CIDR range.");

            if (!normalized.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                normalized.Add(candidate);
        }

        return normalized;
    }

    private static bool IsValidProxyEntry(string value)
    {
        var slashIndex = value.IndexOf('/');
        if (slashIndex < 0)
            return IPAddress.TryParse(value, out _);

        var addressPart = value[..slashIndex];
        var prefixPart = value[(slashIndex + 1)..];
        if (!IPAddress.TryParse(addressPart, out var prefix) ||
            !int.TryParse(prefixPart, out var prefixLength))
        {
            return false;
        }

        var maxPrefixLength = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        return prefixLength >= 0 && prefixLength <= maxPrefixLength;
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }
}

public sealed class QuasarTrustedNetworkSettings
{
    public bool AllowLoopback { get; set; } = true;
    public bool AllowSameSubnet { get; set; } = true;
    public List<string> TrustedProxies { get; set; } = [];
}
