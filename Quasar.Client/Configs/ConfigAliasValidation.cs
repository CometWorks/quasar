using System.Text.RegularExpressions;
using Quasar.Models;

namespace Quasar.Services;

internal static class ConfigAliasValidation
{
    internal static void Validate(IReadOnlyList<QuasarCommandAlias>? aliases)
    {
        if (aliases is null) return;
        if (aliases.Count > 64) throw new ArgumentException("A profile supports at most 64 command aliases.");
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases)
        {
            if (alias is null || !ValidPath(alias.Source) || !ValidPath(alias.Target))
                throw new ArgumentException("Alias paths must start with ! and contain command words made of letters, digits, underscores or hyphens (maximum 512 characters).");
            string source = Normalize(alias.Source), target = Normalize(alias.Target);
            if (!sources.Add(source) || source.Split(' ')[0].Equals("!help", StringComparison.OrdinalIgnoreCase)
                || source.Equals(target, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Aliases must be unique, cannot alias themselves or replace !help.");
        }
    }

    private static bool ValidPath(string? path) => path is { Length: <= 512 }
        && Regex.IsMatch(path, @"\A![a-zA-Z0-9_-]+(?:[ \t]+[a-zA-Z0-9_-]+)*\z");
    internal static string Normalize(string path) => string.Join(' ', path.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
}
