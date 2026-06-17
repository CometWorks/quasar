using System.Globalization;
using System.Text;

namespace Quasar.Services;

public static class TextSanitizer
{
    private const int SpaceEngineersPcPlatformIcon = 0xE030;
    private const int SpaceEngineersPlayStationPlatformIcon = 0xE031;
    private const int SpaceEngineersXboxPlatformIcon = 0xE032;

    public static string CleanGameText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        var hasVisibleText = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (ShouldDrop(rune))
                continue;

            if (!hasVisibleText && ShouldDropLeadingPrefix(rune))
                continue;

            builder.Append(rune);
            hasVisibleText = true;
        }

        return builder.ToString().Trim();
    }

    private static bool ShouldDrop(Rune rune)
    {
        if (rune.Value == 0xFFFD || IsSpaceEngineersPlatformIcon(rune))
            return true;

        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control
            or UnicodeCategory.PrivateUse
            or UnicodeCategory.Surrogate
            or UnicodeCategory.OtherNotAssigned;
    }

    private static bool ShouldDropLeadingPrefix(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Format
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
    }

    private static bool IsSpaceEngineersPlatformIcon(Rune rune) =>
        rune.Value is SpaceEngineersPcPlatformIcon
            or SpaceEngineersPlayStationPlatformIcon
            or SpaceEngineersXboxPlatformIcon;
}
