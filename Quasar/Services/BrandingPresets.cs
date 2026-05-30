using Quasar.Models;

namespace Quasar.Services;

public sealed record BrandingPresetDefinition(string Id, string DisplayName);

/// <summary>
/// Built-in theme presets. "quasar" is the single source of truth derived from
/// <see cref="QuasarTheme"/>; the others apply a small set of identity/surface
/// overrides on top of the Quasar base palettes so they stay internally coherent.
/// </summary>
public static class BrandingPresets
{
    public const string QuasarId = "quasar";
    public const string MidnightId = "midnight";
    public const string SlateId = "slate";
    public const string HighContrastId = "high-contrast";

    public static IReadOnlyList<BrandingPresetDefinition> All { get; } =
    [
        new(QuasarId, "Quasar Default"),
        new(MidnightId, "Midnight Blue"),
        new(SlateId, "Slate"),
        new(HighContrastId, "High Contrast"),
    ];

    public static bool IsKnownPreset(string? presetId)
    {
        return presetId is not null && All.Any(preset =>
            string.Equals(preset.Id, presetId, StringComparison.OrdinalIgnoreCase));
    }

    public static ThemePalette GetLightPalette(string presetId)
    {
        return presetId?.ToLowerInvariant() switch
        {
            MidnightId => MidnightLight(),
            SlateId => SlateLight(),
            HighContrastId => HighContrastLight(),
            _ => ThemePalette.QuasarLight(),
        };
    }

    public static ThemePalette GetDarkPalette(string presetId)
    {
        return presetId?.ToLowerInvariant() switch
        {
            MidnightId => MidnightDark(),
            SlateId => SlateDark(),
            HighContrastId => HighContrastDark(),
            _ => ThemePalette.QuasarDark(),
        };
    }

    private static ThemePalette MidnightLight()
    {
        var palette = ThemePalette.QuasarLight();
        palette.Primary = "#1e3a5f";
        palette.PrimaryContrastText = "#f0f4ff";
        palette.Secondary = "#475c7a";
        palette.Background = "#f0f4ff";
        palette.BackgroundGray = "#e1e8f7";
        palette.Surface = "#ffffff";
        palette.DrawerBackground = "#e8eefc";
        palette.DrawerText = "#1e3a5f";
        palette.DrawerIcon = "#3b5278";
        palette.AppbarBackground = "#ffffff";
        palette.AppbarText = "#1e3a5f";
        palette.TextPrimary = "#16263f";
        palette.TextSecondary = "#3b5278";
        palette.Info = "#1e3a5f";
        return palette;
    }

    private static ThemePalette MidnightDark()
    {
        var palette = ThemePalette.QuasarDark();
        palette.Primary = "#93c5fd";
        palette.PrimaryContrastText = "#0f1729";
        palette.Secondary = "#7f9cc4";
        palette.Background = "#0f1729";
        palette.BackgroundGray = "#1a2740";
        palette.Surface = "#16203a";
        palette.DrawerBackground = "#131d33";
        palette.DrawerText = "#dce6f7";
        palette.DrawerIcon = "#9db4d8";
        palette.AppbarBackground = "#131d33";
        palette.AppbarText = "#dce6f7";
        palette.TextPrimary = "#dce6f7";
        palette.TextSecondary = "#9db4d8";
        palette.LinesDefault = "#2a3a57";
        palette.LinesInputs = "#3d5277";
        palette.TableLines = "#23314c";
        palette.Divider = "#2a3a57";
        palette.DividerLight = "#23314c";
        palette.Info = "#93c5fd";
        return palette;
    }

    private static ThemePalette SlateLight()
    {
        var palette = ThemePalette.QuasarLight();
        palette.Primary = "#475569";
        palette.PrimaryContrastText = "#f8fafc";
        palette.Secondary = "#64748b";
        palette.Background = "#f8fafc";
        palette.BackgroundGray = "#eef2f6";
        palette.Surface = "#ffffff";
        palette.DrawerBackground = "#f1f5f9";
        palette.DrawerText = "#1e293b";
        palette.DrawerIcon = "#475569";
        palette.AppbarBackground = "#ffffff";
        palette.AppbarText = "#1e293b";
        palette.TextPrimary = "#1e293b";
        palette.TextSecondary = "#475569";
        palette.Info = "#475569";
        return palette;
    }

    private static ThemePalette SlateDark()
    {
        var palette = ThemePalette.QuasarDark();
        palette.Primary = "#cbd5e1";
        palette.PrimaryContrastText = "#1e293b";
        palette.Secondary = "#94a3b8";
        palette.Background = "#1e293b";
        palette.BackgroundGray = "#283548";
        palette.Surface = "#243044";
        palette.DrawerBackground = "#202c3e";
        palette.DrawerText = "#e2e8f0";
        palette.DrawerIcon = "#cbd5e1";
        palette.AppbarBackground = "#202c3e";
        palette.AppbarText = "#e2e8f0";
        palette.TextPrimary = "#e2e8f0";
        palette.TextSecondary = "#cbd5e1";
        palette.LinesDefault = "#3a485f";
        palette.LinesInputs = "#4c5d78";
        palette.TableLines = "#313e54";
        palette.Divider = "#3a485f";
        palette.DividerLight = "#313e54";
        palette.Info = "#cbd5e1";
        return palette;
    }

    private static ThemePalette HighContrastLight()
    {
        var palette = ThemePalette.QuasarLight();
        palette.Primary = "#000000";
        palette.PrimaryContrastText = "#ffffff";
        palette.Secondary = "#000000";
        palette.SecondaryContrastText = "#ffffff";
        palette.Background = "#ffffff";
        palette.BackgroundGray = "#f0f0f0";
        palette.Surface = "#ffffff";
        palette.DrawerBackground = "#f0f0f0";
        palette.DrawerText = "#000000";
        palette.DrawerIcon = "#000000";
        palette.AppbarBackground = "#ffffff";
        palette.AppbarText = "#000000";
        palette.TextPrimary = "#000000";
        palette.TextSecondary = "#1a1a1a";
        palette.LinesDefault = "#000000";
        palette.LinesInputs = "#000000";
        palette.TableLines = "#767676";
        palette.Divider = "#000000";
        palette.DividerLight = "#767676";
        palette.Info = "#000080";
        palette.InfoContrastText = "#ffffff";
        palette.Success = "#006400";
        palette.SuccessContrastText = "#ffffff";
        palette.Warning = "#7c5000";
        palette.WarningContrastText = "#ffffff";
        palette.Error = "#c00000";
        palette.ErrorContrastText = "#ffffff";
        return palette;
    }

    private static ThemePalette HighContrastDark()
    {
        var palette = ThemePalette.QuasarDark();
        palette.Primary = "#ffffff";
        palette.PrimaryContrastText = "#000000";
        palette.Secondary = "#ffff00";
        palette.SecondaryContrastText = "#000000";
        palette.Background = "#000000";
        palette.BackgroundGray = "#0a0a0a";
        palette.Surface = "#0f0f0f";
        palette.DrawerBackground = "#0a0a0a";
        palette.DrawerText = "#ffffff";
        palette.DrawerIcon = "#ffffff";
        palette.AppbarBackground = "#000000";
        palette.AppbarText = "#ffffff";
        palette.TextPrimary = "#ffffff";
        palette.TextSecondary = "#ffff00";
        palette.LinesDefault = "#ffffff";
        palette.LinesInputs = "#ffffff";
        palette.TableLines = "#767676";
        palette.Divider = "#ffffff";
        palette.DividerLight = "#767676";
        palette.Info = "#00ffff";
        palette.InfoContrastText = "#000000";
        palette.Success = "#00ff00";
        palette.SuccessContrastText = "#000000";
        palette.Warning = "#ffff00";
        palette.WarningContrastText = "#000000";
        palette.Error = "#ff4444";
        palette.ErrorContrastText = "#000000";
        return palette;
    }
}
