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

    /// <summary>
    /// Creates a slug from <paramref name="source"/> (falling back to
    /// <paramref name="fallback"/> when the source yields no usable characters) and
    /// disambiguates it against existing slugs by appending a "-N" suffix incrementing
    /// from 1 until <paramref name="exists"/> reports the candidate is free.
    /// </summary>
    public static string CreateUnique(string? source, string fallback, Func<string, bool> exists)
    {
        var baseSlug = Create(source);
        if (string.IsNullOrEmpty(baseSlug))
            baseSlug = fallback;

        var candidate = baseSlug;
        var counter = 0;
        while (exists(candidate))
        {
            counter++;
            candidate = $"{baseSlug}-{counter}";
        }

        return candidate;
    }
}
