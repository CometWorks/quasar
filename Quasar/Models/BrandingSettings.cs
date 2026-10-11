using Quasar.Services;

namespace Quasar.Models;

/// <summary>
/// Persistent branding and theme configuration serialized to <c>branding.json</c>.
/// Captures custom app identity (name, subtitle, logos, favicon) plus full light
/// and dark palettes. A null <see cref="PresetId"/> means the palettes
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
/// Framework-independent palette values stored as plain strings so existing
/// branding files round-trip without a UI framework dependency.
/// </summary>
public sealed class ThemePalette
{
    public string Primary { get; set; } = "#111111";
    public string PrimaryContrastText { get; set; } = "#f5f5f5";
    public string Secondary { get; set; } = "#6b7280";
    public string SecondaryContrastText { get; set; } = "#ffffff";
    public string HoverBackground { get; set; } = string.Empty;
    public string HoverContrastText { get; set; } = string.Empty;
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

    public static ThemePalette QuasarLight() => new()
    {
        Primary = "#111111",
        PrimaryContrastText = "#f5f5f5",
        Secondary = "#6b7280",
        SecondaryContrastText = "#ffffff",
        HoverBackground = "#9ca3af",
        HoverContrastText = "#111111",
        Background = "#f5f5f5",
        BackgroundGray = "#ebebeb",
        Surface = "#ffffff",
        DrawerBackground = "#fafafa",
        DrawerText = "#111111",
        DrawerIcon = "#4b5563",
        AppbarBackground = "#ffffff",
        AppbarText = "#111111",
        TextPrimary = "#111111",
        TextSecondary = "#4b5563",
        LinesDefault = "#d4d4d8",
        LinesInputs = "#a1a1aa",
        TableLines = "#e4e4e7",
        Divider = "#d4d4d8",
        DividerLight = "#e4e4e7",
        Info = "#6b7280",
        InfoContrastText = "#ffffff",
        Success = "#166534",
        SuccessContrastText = "#ffffff",
        Warning = "#a16207",
        WarningContrastText = "#ffffff",
        Error = "#b91c1c",
        ErrorContrastText = "#ffffff",
    };

    public static ThemePalette QuasarDark() => new()
    {
        Primary = "#f5f5f5",
        PrimaryContrastText = "#111111",
        Secondary = "#9ca3af",
        SecondaryContrastText = "#111111",
        HoverBackground = "#bfbfbfff",
        HoverContrastText = "#111111",
        Background = "#18181b",
        BackgroundGray = "#27272a",
        Surface = "#232326",
        DrawerBackground = "#202024",
        DrawerText = "#f5f5f5",
        DrawerIcon = "#d4d4d8",
        AppbarBackground = "#202024",
        AppbarText = "#f5f5f5",
        TextPrimary = "#f5f5f5",
        TextSecondary = "#d4d4d8",
        LinesDefault = "#3f3f46",
        LinesInputs = "#5a5a63",
        TableLines = "#35353b",
        Divider = "#3f3f46",
        DividerLight = "#35353b",
        Info = "#a3a3a3",
        InfoContrastText = "#111111",
        Success = "#86efac",
        SuccessContrastText = "#111111",
        Warning = "#facc15",
        WarningContrastText = "#111111",
        Error = "#fca5a5",
        ErrorContrastText = "#111111",
    };

    public ThemePalette Clone()
    {
        return new ThemePalette
        {
            Primary = Primary,
            PrimaryContrastText = PrimaryContrastText,
            Secondary = Secondary,
            SecondaryContrastText = SecondaryContrastText,
            HoverBackground = HoverBackground,
            HoverContrastText = HoverContrastText,
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
            HoverBackground = Pick(palette.HoverBackground, fallback.HoverBackground),
            HoverContrastText = Pick(palette.HoverContrastText, fallback.HoverContrastText),
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

    // Keep binary compatibility for existing UI plugins during the staged migration.
    public MudBlazor.PaletteLight ToMudPaletteLight() => QuasarTheme.ToMudPaletteLight(this);
    public MudBlazor.PaletteDark ToMudPaletteDark() => QuasarTheme.ToMudPaletteDark(this);

    private static string Pick(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
