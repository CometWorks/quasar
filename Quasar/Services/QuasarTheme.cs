using MudBlazor;

namespace Quasar.Services;

public static class QuasarTheme
{
    public static readonly MudTheme Default = new()
    {
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "6px",
        },
        PaletteLight = new PaletteLight
        {
            Primary = "#111111",
            PrimaryContrastText = "#f5f5f5",
            Secondary = "#6b7280",
            SecondaryContrastText = "#ffffff",
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
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#f5f5f5",
            PrimaryContrastText = "#111111",
            Secondary = "#9ca3af",
            SecondaryContrastText = "#111111",
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
        },
    };
}
