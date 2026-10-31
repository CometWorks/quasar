using Microsoft.AspNetCore.Components;

namespace Quasar.Services;

public static class PanelUrls
{
    // Application links may be root-relative; external links remain external.
    public static string? PanelUrl(this NavigationManager navigation, string? value) => value is not null
        && value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal)
        ? new Uri(new Uri(navigation.BaseUri), value.TrimStart('/')).AbsoluteUri : value;
}
