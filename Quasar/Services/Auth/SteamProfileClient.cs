using Quasar.Client.Administration;
using System.Xml;
using System.Xml.Linq;

namespace Quasar.Services.Auth;

public sealed class SteamProfileClient(HttpClient client, ILogger<SteamProfileClient> logger)
{
    public static bool IsSupportedInput(string? input) => SteamProfileInput.TryGetProfilePath(input, out _, out _);

    public async Task<string> ResolveSteamIdAsync(string? input, CancellationToken cancellationToken = default)
    {
        if (!SteamProfileInput.TryGetProfilePath(input, out var path, out var steamId))
            throw new ArgumentException("Enter a 17-digit SteamID64 or a Steam Community profile URL.", nameof(input));

        if (steamId is not null)
            return steamId;

        var profile = await GetProfileAsync(path, cancellationToken);
        steamId = profile?.Element("steamID64")?.Value;
        if (!RbacConfigCatalog.IsSteamId64(steamId))
            throw new InvalidOperationException("Could not resolve the Steam profile. Check the URL or enter its 17-digit SteamID64 and try again.");

        return steamId!;
    }

    public async Task<string?> GetDisplayNameAsync(string steamId, CancellationToken cancellationToken)
    {
        if (!RbacConfigCatalog.IsSteamId64(steamId))
            return null;

        var profile = await GetProfileAsync($"profiles/{steamId}", cancellationToken);
        if (profile?.Element("steamID64")?.Value != steamId)
            return null;

        var name = profile.Element("steamID")?.Value.Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private async Task<XElement?> GetProfileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(
                $"https://steamcommunity.com/{path}?xml=1", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 128 * 1024,
            });
            var profile = (await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken)).Root;
            return profile?.Name == "profile" ? profile : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or XmlException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Could not retrieve the Steam profile at {ProfilePath}.", path);
            return null;
        }
    }
}
