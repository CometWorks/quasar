using System.Xml;
using System.Xml.Linq;

namespace Quasar.Services.Auth;

public sealed class SteamProfileClient(HttpClient client, ILogger<SteamProfileClient> logger)
{
    public async Task<string?> GetDisplayNameAsync(string steamId, CancellationToken cancellationToken)
    {
        if (steamId.Length != 17 || !steamId.All(char.IsAsciiDigit))
            return null;

        try
        {
            using var response = await client.GetAsync(
                $"https://steamcommunity.com/profiles/{steamId}?xml=1", cancellationToken);
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
            if (profile?.Name != "profile" || profile.Element("steamID64")?.Value != steamId)
                return null;

            var name = profile.Element("steamID")?.Value.Trim();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception exception) when (exception is HttpRequestException or XmlException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Could not retrieve the Steam display name for {SteamId}.", steamId);
            return null;
        }
    }
}
