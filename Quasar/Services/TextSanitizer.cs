using System.Globalization;
using System.Text;

namespace Quasar.Services;

public static class TextSanitizer
{
    public static string CleanGameText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (ShouldDrop(rune))
                continue;

            builder.Append(rune);
        }

        return builder.ToString().Trim();
    }

    private static bool ShouldDrop(Rune rune)
    {
        if (rune.Value == 0xFFFD)
            return true;

        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control
            or UnicodeCategory.PrivateUse
            or UnicodeCategory.Surrogate
            or UnicodeCategory.OtherNotAssigned;
    }
}
