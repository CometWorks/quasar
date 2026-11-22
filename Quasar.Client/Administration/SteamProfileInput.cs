using System.Text.RegularExpressions;

namespace Quasar.Client.Administration;

public static class SteamProfileInput
{
    public static bool IsSupported(string? input) => TryGetProfilePath(input, out _, out _);
    public static bool IsSteamId64(string? input) => input is { Length: 17 } && input.All(char.IsAsciiDigit);

    public static bool TryGetProfilePath(string? input, out string path, out string? steamId)
    {
        path = string.Empty;
        steamId = null;
        input = input?.Trim();
        if (IsSteamId64(input))
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
            if (!IsSteamId64(steamId))
                return false;
        }

        path = $"{match.Groups[1].Value}/{match.Groups[2].Value}";
        return true;
    }
}
