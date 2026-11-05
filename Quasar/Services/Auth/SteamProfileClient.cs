using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Quasar.Services.Auth;

public sealed class SteamProfileClient(HttpClient client, ILogger<SteamProfileClient> logger)
{
    public static bool IsSupportedInput(string? input) => TryGetProfilePath(input, out _, out _);

    public async Task<string> ResolveSteamIdAsync(string? input, CancellationToken cancellationToken = default)
    {
        if (!TryGetProfilePath(input, out var path, out var steamId))
            throw new ArgumentException("Enter a 17-digit SteamID64 or a Steam Community profile URL.", nameof(input));

        if (steamId is not null)
            return steamId;

        var profile = await GetProfileAsync(path, cancellationToken);
        steamId = profile?.Element("steamID64")?.Value;
        if (!RbacConfigCatalog.IsSteamId64(steamId))
            throw new InvalidOperationException("Could not resolve the Steam profile. Check the URL or enter its 17-digit SteamID64 and try again.");

        return steamId!;
    }

    private static bool TryGetProfilePath(string? input, out string path, out string? steamId)
    {
        path = string.Empty;
        steamId = null;
        input = input?.Trim();
        if (RbacConfigCatalog.IsSteamId64(input))
        {
            steamId = input;
            path = $"profiles/{steamId}";
            return true;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !(uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("www.steamcommunity.com", StringComparison.OrdinalIgnoreCase)))
            return false;

        var match = Regex.Match(uri.AbsolutePath, "^/(id|profiles)/([a-zA-Z0-9_-]{1,64})/?$", RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        if (match.Groups[1].Value == "profiles")
        {
            steamId = match.Groups[2].Value;
            if (!RbacConfigCatalog.IsSteamId64(steamId))
                return false;
        }

        path = $"{match.Groups[1].Value}/{match.Groups[2].Value}";
        return true;
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
