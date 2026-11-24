using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Quasar.Models;
using Quasar.Services;

namespace Quasar.Client.Administration;

internal static class BrowserBranding
{
    internal static ValueTask ApplyAsync(this BrandingSettings settings, IJSRuntime js, NavigationManager navigation) => js.InvokeVoidAsync("quasarSdk.applyBranding",
        new { settings, settings.AppName, settings.AppSubtitle, darkLogo = navigation.PanelUrl(settings.LogoDarkPath ?? "Quasar.png"),
            lightLogo = navigation.PanelUrl(settings.LogoLightPath ?? "Quasar-inverted-transparent.png"),
            favicon = navigation.PanelUrl(settings.FaviconPath ?? "Quasar.ico"),
            darkPalette = QuasarTheme.BuildCss(settings.DarkPalette, true), lightPalette = QuasarTheme.BuildCss(settings.LightPalette, false) });
}
