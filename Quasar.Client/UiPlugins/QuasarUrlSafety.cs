namespace Quasar.Components.Shared;

/// <summary>
/// Allows relative links and the small set of absolute schemes Quasar uses.
/// </summary>
internal static class QuasarUrlSafety
{
    public static bool IsSafeHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return false;

        var value = href.Trim();
        if (value.Length == 0 || value.Any(character => character < ' '))
            return false;
        if (value.StartsWith("//", StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal))
            return false;
        if (value[0] is '/' or '#' or '?')
            return true;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("tel", StringComparison.OrdinalIgnoreCase);
        }

        return !value.Contains(':', StringComparison.Ordinal);
    }
}
