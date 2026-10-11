using System.Text;
using MudBlazor;
using MudBlazor.Utilities;
using Quasar.Models;

namespace Quasar.Services;

/// <summary>
/// Compatibility adapter for pages and UI plugins that still use MudBlazor.
/// The persisted palette and native CSS tokens are owned by Quasar.
/// </summary>
public static class QuasarTheme
{
    public static readonly MudTheme Default = Create(BrandingSettings.Normalize(null));

    public static MudTheme Create(BrandingSettings settings) => new()
    {
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "4px" },
        PaletteLight = ToMudPaletteLight(settings.LightPalette),
        PaletteDark = ToMudPaletteDark(settings.DarkPalette),
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["DM Sans", "system-ui", "sans-serif"], FontSize = "0.8125rem" },
            H5 = new H5Typography { FontFamily = ["Manrope", "system-ui", "sans-serif"], FontSize = "1.5rem", FontWeight = "700" },
            H6 = new H6Typography { FontFamily = ["Manrope", "system-ui", "sans-serif"], FontSize = "1rem", FontWeight = "700" },
            Body1 = new Body1Typography { FontFamily = ["DM Sans", "system-ui", "sans-serif"], FontSize = "0.8125rem" },
            Body2 = new Body2Typography { FontFamily = ["DM Sans", "system-ui", "sans-serif"], FontSize = "0.75rem" },
            Button = new ButtonTypography { FontFamily = ["DM Sans", "system-ui", "sans-serif"], FontSize = "0.75rem", TextTransform = "none" },
        },
    };

    // Reuse the existing parser during migration so legacy external color formats
    // remain accepted. Normalize before placing a value in a CSS declaration.
    public static string NormalizeColor(string value, string fallback) =>
        MudColor.TryParse(value, out var color)
            ? color.ToString(MudColorOutputFormats.HexA)
            : new MudColor(fallback).ToString(MudColorOutputFormats.HexA);

    public static string BuildCss(ThemePalette palette, bool isDark)
    {
        var fallback = isDark ? ThemePalette.QuasarDark() : ThemePalette.QuasarLight();
        var css = new StringBuilder();
        css.Append("--quasar-palette-primary:").Append(NormalizeColor(palette.Primary, fallback.Primary)).Append(';');
        css.Append("--quasar-palette-primary-contrast-text:").Append(NormalizeColor(palette.PrimaryContrastText, fallback.PrimaryContrastText)).Append(';');
        css.Append("--quasar-palette-secondary:").Append(NormalizeColor(palette.Secondary, fallback.Secondary)).Append(';');
        css.Append("--quasar-palette-secondary-contrast-text:").Append(NormalizeColor(palette.SecondaryContrastText, fallback.SecondaryContrastText)).Append(';');
        css.Append("--quasar-palette-hover-background:").Append(NormalizeColor(palette.HoverBackground, fallback.HoverBackground)).Append(';');
        css.Append("--quasar-palette-hover-contrast-text:").Append(NormalizeColor(palette.HoverContrastText, fallback.HoverContrastText)).Append(';');
        css.Append("--quasar-palette-background:").Append(NormalizeColor(palette.Background, fallback.Background)).Append(';');
        css.Append("--quasar-palette-background-gray:").Append(NormalizeColor(palette.BackgroundGray, fallback.BackgroundGray)).Append(';');
        css.Append("--quasar-palette-surface:").Append(NormalizeColor(palette.Surface, fallback.Surface)).Append(';');
        css.Append("--quasar-palette-drawer-background:").Append(NormalizeColor(palette.DrawerBackground, fallback.DrawerBackground)).Append(';');
        css.Append("--quasar-palette-drawer-text:").Append(NormalizeColor(palette.DrawerText, fallback.DrawerText)).Append(';');
        css.Append("--quasar-palette-drawer-icon:").Append(NormalizeColor(palette.DrawerIcon, fallback.DrawerIcon)).Append(';');
        css.Append("--quasar-palette-appbar-background:").Append(NormalizeColor(palette.AppbarBackground, fallback.AppbarBackground)).Append(';');
        css.Append("--quasar-palette-appbar-text:").Append(NormalizeColor(palette.AppbarText, fallback.AppbarText)).Append(';');
        css.Append("--quasar-palette-text-primary:").Append(NormalizeColor(palette.TextPrimary, fallback.TextPrimary)).Append(';');
        css.Append("--quasar-palette-text-secondary:").Append(NormalizeColor(palette.TextSecondary, fallback.TextSecondary)).Append(';');
        css.Append("--quasar-palette-lines-default:").Append(NormalizeColor(palette.LinesDefault, fallback.LinesDefault)).Append(';');
        css.Append("--quasar-palette-lines-inputs:").Append(NormalizeColor(palette.LinesInputs, fallback.LinesInputs)).Append(';');
        css.Append("--quasar-palette-table-lines:").Append(NormalizeColor(palette.TableLines, fallback.TableLines)).Append(';');
        css.Append("--quasar-palette-divider:").Append(NormalizeColor(palette.Divider, fallback.Divider)).Append(';');
        css.Append("--quasar-palette-divider-light:").Append(NormalizeColor(palette.DividerLight, fallback.DividerLight)).Append(';');
        css.Append("--quasar-palette-info:").Append(NormalizeColor(palette.Info, fallback.Info)).Append(';');
        css.Append("--quasar-palette-info-contrast-text:").Append(NormalizeColor(palette.InfoContrastText, fallback.InfoContrastText)).Append(';');
        css.Append("--quasar-palette-success:").Append(NormalizeColor(palette.Success, fallback.Success)).Append(';');
        css.Append("--quasar-palette-success-contrast-text:").Append(NormalizeColor(palette.SuccessContrastText, fallback.SuccessContrastText)).Append(';');
        css.Append("--quasar-palette-warning:").Append(NormalizeColor(palette.Warning, fallback.Warning)).Append(';');
        css.Append("--quasar-palette-warning-contrast-text:").Append(NormalizeColor(palette.WarningContrastText, fallback.WarningContrastText)).Append(';');
        css.Append("--quasar-palette-error:").Append(NormalizeColor(palette.Error, fallback.Error)).Append(';');
        css.Append("--quasar-palette-error-contrast-text:").Append(NormalizeColor(palette.ErrorContrastText, fallback.ErrorContrastText)).Append(';');
        css.Append("--quasar-hover-list-background:").Append(NormalizeColor(palette.HoverBackground, fallback.HoverBackground)).Append(';');
        css.Append("--quasar-hover-list-text:").Append(NormalizeColor(palette.HoverContrastText, fallback.HoverContrastText)).Append(';');
        return css.ToString();
    }

    public static PaletteLight ToMudPaletteLight(this ThemePalette palette) => new()
    {
        Primary = NormalizeColor(palette.Primary, ThemePalette.QuasarLight().Primary),
        PrimaryContrastText = NormalizeColor(palette.PrimaryContrastText, ThemePalette.QuasarLight().PrimaryContrastText),
        Secondary = NormalizeColor(palette.Secondary, ThemePalette.QuasarLight().Secondary),
        SecondaryContrastText = NormalizeColor(palette.SecondaryContrastText, ThemePalette.QuasarLight().SecondaryContrastText),
        Background = NormalizeColor(palette.Background, ThemePalette.QuasarLight().Background),
        BackgroundGray = NormalizeColor(palette.BackgroundGray, ThemePalette.QuasarLight().BackgroundGray),
        Surface = NormalizeColor(palette.Surface, ThemePalette.QuasarLight().Surface),
        DrawerBackground = NormalizeColor(palette.DrawerBackground, ThemePalette.QuasarLight().DrawerBackground),
        DrawerText = NormalizeColor(palette.DrawerText, ThemePalette.QuasarLight().DrawerText),
        DrawerIcon = NormalizeColor(palette.DrawerIcon, ThemePalette.QuasarLight().DrawerIcon),
        AppbarBackground = NormalizeColor(palette.AppbarBackground, ThemePalette.QuasarLight().AppbarBackground),
        AppbarText = NormalizeColor(palette.AppbarText, ThemePalette.QuasarLight().AppbarText),
        TextPrimary = NormalizeColor(palette.TextPrimary, ThemePalette.QuasarLight().TextPrimary),
        TextSecondary = NormalizeColor(palette.TextSecondary, ThemePalette.QuasarLight().TextSecondary),
        LinesDefault = NormalizeColor(palette.LinesDefault, ThemePalette.QuasarLight().LinesDefault),
        LinesInputs = NormalizeColor(palette.LinesInputs, ThemePalette.QuasarLight().LinesInputs),
        TableLines = NormalizeColor(palette.TableLines, ThemePalette.QuasarLight().TableLines),
        Divider = NormalizeColor(palette.Divider, ThemePalette.QuasarLight().Divider),
        DividerLight = NormalizeColor(palette.DividerLight, ThemePalette.QuasarLight().DividerLight),
        Info = NormalizeColor(palette.Info, ThemePalette.QuasarLight().Info),
        InfoContrastText = NormalizeColor(palette.InfoContrastText, ThemePalette.QuasarLight().InfoContrastText),
        Success = NormalizeColor(palette.Success, ThemePalette.QuasarLight().Success),
        SuccessContrastText = NormalizeColor(palette.SuccessContrastText, ThemePalette.QuasarLight().SuccessContrastText),
        Warning = NormalizeColor(palette.Warning, ThemePalette.QuasarLight().Warning),
        WarningContrastText = NormalizeColor(palette.WarningContrastText, ThemePalette.QuasarLight().WarningContrastText),
        Error = NormalizeColor(palette.Error, ThemePalette.QuasarLight().Error),
        ErrorContrastText = NormalizeColor(palette.ErrorContrastText, ThemePalette.QuasarLight().ErrorContrastText),
    };

    public static PaletteDark ToMudPaletteDark(this ThemePalette palette) => new()
    {
        Primary = NormalizeColor(palette.Primary, ThemePalette.QuasarDark().Primary),
        PrimaryContrastText = NormalizeColor(palette.PrimaryContrastText, ThemePalette.QuasarDark().PrimaryContrastText),
        Secondary = NormalizeColor(palette.Secondary, ThemePalette.QuasarDark().Secondary),
        SecondaryContrastText = NormalizeColor(palette.SecondaryContrastText, ThemePalette.QuasarDark().SecondaryContrastText),
        Background = NormalizeColor(palette.Background, ThemePalette.QuasarDark().Background),
        BackgroundGray = NormalizeColor(palette.BackgroundGray, ThemePalette.QuasarDark().BackgroundGray),
        Surface = NormalizeColor(palette.Surface, ThemePalette.QuasarDark().Surface),
        DrawerBackground = NormalizeColor(palette.DrawerBackground, ThemePalette.QuasarDark().DrawerBackground),
        DrawerText = NormalizeColor(palette.DrawerText, ThemePalette.QuasarDark().DrawerText),
        DrawerIcon = NormalizeColor(palette.DrawerIcon, ThemePalette.QuasarDark().DrawerIcon),
        AppbarBackground = NormalizeColor(palette.AppbarBackground, ThemePalette.QuasarDark().AppbarBackground),
        AppbarText = NormalizeColor(palette.AppbarText, ThemePalette.QuasarDark().AppbarText),
        TextPrimary = NormalizeColor(palette.TextPrimary, ThemePalette.QuasarDark().TextPrimary),
        TextSecondary = NormalizeColor(palette.TextSecondary, ThemePalette.QuasarDark().TextSecondary),
        LinesDefault = NormalizeColor(palette.LinesDefault, ThemePalette.QuasarDark().LinesDefault),
        LinesInputs = NormalizeColor(palette.LinesInputs, ThemePalette.QuasarDark().LinesInputs),
        TableLines = NormalizeColor(palette.TableLines, ThemePalette.QuasarDark().TableLines),
        Divider = NormalizeColor(palette.Divider, ThemePalette.QuasarDark().Divider),
        DividerLight = NormalizeColor(palette.DividerLight, ThemePalette.QuasarDark().DividerLight),
        Info = NormalizeColor(palette.Info, ThemePalette.QuasarDark().Info),
        InfoContrastText = NormalizeColor(palette.InfoContrastText, ThemePalette.QuasarDark().InfoContrastText),
        Success = NormalizeColor(palette.Success, ThemePalette.QuasarDark().Success),
        SuccessContrastText = NormalizeColor(palette.SuccessContrastText, ThemePalette.QuasarDark().SuccessContrastText),
        Warning = NormalizeColor(palette.Warning, ThemePalette.QuasarDark().Warning),
        WarningContrastText = NormalizeColor(palette.WarningContrastText, ThemePalette.QuasarDark().WarningContrastText),
        Error = NormalizeColor(palette.Error, ThemePalette.QuasarDark().Error),
        ErrorContrastText = NormalizeColor(palette.ErrorContrastText, ThemePalette.QuasarDark().ErrorContrastText),
    };

}
