using MudBlazor;
using Quasar.Services;

namespace Quasar.Models;

/// <summary>
/// Persistent branding and theme configuration serialized to <c>branding.json</c>.
/// Captures custom app identity (name, subtitle, logos, favicon) plus full light
/// and dark MudBlazor palettes. A null <see cref="PresetId"/> means the palettes
/// were hand-edited (custom); a non-null value identifies a built-in preset.
/// </summary>
public sealed class BrandingSettings
{
    public string? PresetId { get; set; } = BrandingPresets.QuasarId;

    public string AppName { get; set; } = "Quasar";

    public string AppSubtitle { get; set; } = "Supervisor control plane";

    public string? LogoLightPath { get; set; }

    public string? LogoDarkPath { get; set; }

    public string? FaviconPath { get; set; }

    public ThemePalette LightPalette { get; set; } = ThemePalette.QuasarLight();

    public ThemePalette DarkPalette { get; set; } = ThemePalette.QuasarDark();

    public BrandingSettings Clone()
    {
        return new BrandingSettings
        {
            PresetId = PresetId,
            AppName = AppName,
            AppSubtitle = AppSubtitle,
            LogoLightPath = LogoLightPath,
            LogoDarkPath = LogoDarkPath,
            FaviconPath = FaviconPath,
            LightPalette = LightPalette.Clone(),
            DarkPalette = DarkPalette.Clone(),
        };
    }

    /// <summary>
    /// Returns a fully-populated settings object: fills null palettes/fields from
    /// the Quasar defaults and trims branding text. A null input yields the
    /// out-of-the-box Quasar branding (preset "quasar").
    /// </summary>
    public static BrandingSettings Normalize(BrandingSettings? settings)
    {
        settings ??= new BrandingSettings();

        var appName = string.IsNullOrWhiteSpace(settings.AppName) ? "Quasar" : settings.AppName.Trim();
        var appSubtitle = string.IsNullOrWhiteSpace(settings.AppSubtitle)
            ? "Supervisor control plane"
            : settings.AppSubtitle.Trim();

        return new BrandingSettings
        {
            PresetId = string.IsNullOrWhiteSpace(settings.PresetId) ? null : settings.PresetId.Trim(),
            AppName = appName,
            AppSubtitle = appSubtitle,
            LogoLightPath = NormalizePath(settings.LogoLightPath),
            LogoDarkPath = NormalizePath(settings.LogoDarkPath),
            FaviconPath = NormalizePath(settings.FaviconPath),
            LightPalette = ThemePalette.Normalize(settings.LightPalette, ThemePalette.QuasarLight()),
            DarkPalette = ThemePalette.Normalize(settings.DarkPalette, ThemePalette.QuasarDark()),
        };
    }

    private static string? NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }
}

/// <summary>
/// Hex colour values mirroring the fields of <see cref="PaletteLight"/> /
/// <see cref="PaletteDark"/> used by <see cref="QuasarTheme"/>. Stored as plain
/// strings so the palette round-trips cleanly through JSON.
/// </summary>
public sealed class ThemePalette
{
    public string Primary { get; set; } = "#111111";
    public string PrimaryContrastText { get; set; } = "#f5f5f5";
    public string Secondary { get; set; } = "#6b7280";
    public string SecondaryContrastText { get; set; } = "#ffffff";
    public string Background { get; set; } = "#f5f5f5";
    public string BackgroundGray { get; set; } = "#ebebeb";
    public string Surface { get; set; } = "#ffffff";
    public string DrawerBackground { get; set; } = "#fafafa";
    public string DrawerText { get; set; } = "#111111";
    public string DrawerIcon { get; set; } = "#4b5563";
    public string AppbarBackground { get; set; } = "#ffffff";
    public string AppbarText { get; set; } = "#111111";
    public string TextPrimary { get; set; } = "#111111";
    public string TextSecondary { get; set; } = "#4b5563";
    public string LinesDefault { get; set; } = "#d4d4d8";
    public string LinesInputs { get; set; } = "#a1a1aa";
    public string TableLines { get; set; } = "#e4e4e7";
    public string Divider { get; set; } = "#d4d4d8";
    public string DividerLight { get; set; } = "#e4e4e7";
    public string Info { get; set; } = "#6b7280";
    public string InfoContrastText { get; set; } = "#ffffff";
    public string Success { get; set; } = "#166534";
    public string SuccessContrastText { get; set; } = "#ffffff";
    public string Warning { get; set; } = "#a16207";
    public string WarningContrastText { get; set; } = "#ffffff";
    public string Error { get; set; } = "#b91c1c";
    public string ErrorContrastText { get; set; } = "#ffffff";

    public static ThemePalette QuasarLight()
    {
        var palette = QuasarTheme.Default.PaletteLight;
        return new ThemePalette
        {
            Primary = palette.Primary.Value,
            PrimaryContrastText = palette.PrimaryContrastText.Value,
            Secondary = palette.Secondary.Value,
            SecondaryContrastText = palette.SecondaryContrastText.Value,
            Background = palette.Background.Value,
            BackgroundGray = palette.BackgroundGray.Value,
            Surface = palette.Surface.Value,
            DrawerBackground = palette.DrawerBackground.Value,
            DrawerText = palette.DrawerText.Value,
            DrawerIcon = palette.DrawerIcon.Value,
            AppbarBackground = palette.AppbarBackground.Value,
            AppbarText = palette.AppbarText.Value,
            TextPrimary = palette.TextPrimary.Value,
            TextSecondary = palette.TextSecondary.Value,
            LinesDefault = palette.LinesDefault.Value,
            LinesInputs = palette.LinesInputs.Value,
            TableLines = palette.TableLines.Value,
            Divider = palette.Divider.Value,
            DividerLight = palette.DividerLight.Value,
            Info = palette.Info.Value,
            InfoContrastText = palette.InfoContrastText.Value,
            Success = palette.Success.Value,
            SuccessContrastText = palette.SuccessContrastText.Value,
            Warning = palette.Warning.Value,
            WarningContrastText = palette.WarningContrastText.Value,
            Error = palette.Error.Value,
            ErrorContrastText = palette.ErrorContrastText.Value,
        };
    }

    public static ThemePalette QuasarDark()
    {
        var palette = QuasarTheme.Default.PaletteDark;
        return new ThemePalette
        {
            Primary = palette.Primary.Value,
            PrimaryContrastText = palette.PrimaryContrastText.Value,
            Secondary = palette.Secondary.Value,
            SecondaryContrastText = palette.SecondaryContrastText.Value,
            Background = palette.Background.Value,
            BackgroundGray = palette.BackgroundGray.Value,
            Surface = palette.Surface.Value,
            DrawerBackground = palette.DrawerBackground.Value,
            DrawerText = palette.DrawerText.Value,
            DrawerIcon = palette.DrawerIcon.Value,
            AppbarBackground = palette.AppbarBackground.Value,
            AppbarText = palette.AppbarText.Value,
            TextPrimary = palette.TextPrimary.Value,
            TextSecondary = palette.TextSecondary.Value,
            LinesDefault = palette.LinesDefault.Value,
            LinesInputs = palette.LinesInputs.Value,
            TableLines = palette.TableLines.Value,
            Divider = palette.Divider.Value,
            DividerLight = palette.DividerLight.Value,
            Info = palette.Info.Value,
            InfoContrastText = palette.InfoContrastText.Value,
            Success = palette.Success.Value,
            SuccessContrastText = palette.SuccessContrastText.Value,
            Warning = palette.Warning.Value,
            WarningContrastText = palette.WarningContrastText.Value,
            Error = palette.Error.Value,
            ErrorContrastText = palette.ErrorContrastText.Value,
        };
    }

    public ThemePalette Clone()
    {
        return new ThemePalette
        {
            Primary = Primary,
            PrimaryContrastText = PrimaryContrastText,
            Secondary = Secondary,
            SecondaryContrastText = SecondaryContrastText,
            Background = Background,
            BackgroundGray = BackgroundGray,
            Surface = Surface,
            DrawerBackground = DrawerBackground,
            DrawerText = DrawerText,
            DrawerIcon = DrawerIcon,
            AppbarBackground = AppbarBackground,
            AppbarText = AppbarText,
            TextPrimary = TextPrimary,
            TextSecondary = TextSecondary,
            LinesDefault = LinesDefault,
            LinesInputs = LinesInputs,
            TableLines = TableLines,
            Divider = Divider,
            DividerLight = DividerLight,
            Info = Info,
            InfoContrastText = InfoContrastText,
            Success = Success,
            SuccessContrastText = SuccessContrastText,
            Warning = Warning,
            WarningContrastText = WarningContrastText,
            Error = Error,
            ErrorContrastText = ErrorContrastText,
        };
    }

    /// <summary>Fills any null/blank colour field from <paramref name="fallback"/>.</summary>
    public static ThemePalette Normalize(ThemePalette? palette, ThemePalette fallback)
    {
        if (palette is null)
            return fallback.Clone();

        return new ThemePalette
        {
            Primary = Pick(palette.Primary, fallback.Primary),
            PrimaryContrastText = Pick(palette.PrimaryContrastText, fallback.PrimaryContrastText),
            Secondary = Pick(palette.Secondary, fallback.Secondary),
            SecondaryContrastText = Pick(palette.SecondaryContrastText, fallback.SecondaryContrastText),
            Background = Pick(palette.Background, fallback.Background),
            BackgroundGray = Pick(palette.BackgroundGray, fallback.BackgroundGray),
            Surface = Pick(palette.Surface, fallback.Surface),
            DrawerBackground = Pick(palette.DrawerBackground, fallback.DrawerBackground),
            DrawerText = Pick(palette.DrawerText, fallback.DrawerText),
            DrawerIcon = Pick(palette.DrawerIcon, fallback.DrawerIcon),
            AppbarBackground = Pick(palette.AppbarBackground, fallback.AppbarBackground),
            AppbarText = Pick(palette.AppbarText, fallback.AppbarText),
            TextPrimary = Pick(palette.TextPrimary, fallback.TextPrimary),
            TextSecondary = Pick(palette.TextSecondary, fallback.TextSecondary),
            LinesDefault = Pick(palette.LinesDefault, fallback.LinesDefault),
            LinesInputs = Pick(palette.LinesInputs, fallback.LinesInputs),
            TableLines = Pick(palette.TableLines, fallback.TableLines),
            Divider = Pick(palette.Divider, fallback.Divider),
            DividerLight = Pick(palette.DividerLight, fallback.DividerLight),
            Info = Pick(palette.Info, fallback.Info),
            InfoContrastText = Pick(palette.InfoContrastText, fallback.InfoContrastText),
            Success = Pick(palette.Success, fallback.Success),
            SuccessContrastText = Pick(palette.SuccessContrastText, fallback.SuccessContrastText),
            Warning = Pick(palette.Warning, fallback.Warning),
            WarningContrastText = Pick(palette.WarningContrastText, fallback.WarningContrastText),
            Error = Pick(palette.Error, fallback.Error),
            ErrorContrastText = Pick(palette.ErrorContrastText, fallback.ErrorContrastText),
        };
    }

    public PaletteLight ToMudPaletteLight()
    {
        return new PaletteLight
        {
            Primary = Primary,
            PrimaryContrastText = PrimaryContrastText,
            Secondary = Secondary,
            SecondaryContrastText = SecondaryContrastText,
            Background = Background,
            BackgroundGray = BackgroundGray,
            Surface = Surface,
            DrawerBackground = DrawerBackground,
            DrawerText = DrawerText,
            DrawerIcon = DrawerIcon,
            AppbarBackground = AppbarBackground,
            AppbarText = AppbarText,
            TextPrimary = TextPrimary,
            TextSecondary = TextSecondary,
            LinesDefault = LinesDefault,
            LinesInputs = LinesInputs,
            TableLines = TableLines,
            Divider = Divider,
            DividerLight = DividerLight,
            Info = Info,
            InfoContrastText = InfoContrastText,
            Success = Success,
            SuccessContrastText = SuccessContrastText,
            Warning = Warning,
            WarningContrastText = WarningContrastText,
            Error = Error,
            ErrorContrastText = ErrorContrastText,
        };
    }

    public PaletteDark ToMudPaletteDark()
    {
        return new PaletteDark
        {
            Primary = Primary,
            PrimaryContrastText = PrimaryContrastText,
            Secondary = Secondary,
            SecondaryContrastText = SecondaryContrastText,
            Background = Background,
            BackgroundGray = BackgroundGray,
            Surface = Surface,
            DrawerBackground = DrawerBackground,
            DrawerText = DrawerText,
            DrawerIcon = DrawerIcon,
            AppbarBackground = AppbarBackground,
            AppbarText = AppbarText,
            TextPrimary = TextPrimary,
            TextSecondary = TextSecondary,
            LinesDefault = LinesDefault,
            LinesInputs = LinesInputs,
            TableLines = TableLines,
            Divider = Divider,
            DividerLight = DividerLight,
            Info = Info,
            InfoContrastText = InfoContrastText,
            Success = Success,
            SuccessContrastText = SuccessContrastText,
            Warning = Warning,
            WarningContrastText = WarningContrastText,
            Error = Error,
            ErrorContrastText = ErrorContrastText,
        };
    }

    private static string Pick(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
