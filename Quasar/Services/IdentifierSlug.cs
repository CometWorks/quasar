using System.Text;

namespace Quasar.Services;

public static class IdentifierSlug
{
    /// <summary>
    /// Converts a human-readable name into a lowercase identifier slug containing
    /// only letters, digits, and single hyphens. Whitespace, underscores, and
    /// hyphens collapse into a single hyphen; any other character is dropped.
    /// Returns an empty string when the source has no usable characters.
    /// </summary>
    public static string Create(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return string.Empty;

        var trimmed = source.Trim().ToLowerInvariant();
        var builder = new StringBuilder(trimmed.Length);
        char lastAppended = '\0';

        foreach (var ch in trimmed)
        {
            char mapped;
            if (char.IsLetterOrDigit(ch))
            {
                mapped = ch;
            }
            else if (ch is '-' or '_' || char.IsWhiteSpace(ch))
            {
                mapped = '-';
            }
            else
            {
                continue;
            }

            if (mapped == '-' && lastAppended == '-')
                continue;

            builder.Append(mapped);
            lastAppended = mapped;
        }

        return builder.ToString().Trim('-');
    }
}
