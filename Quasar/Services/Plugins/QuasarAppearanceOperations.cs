using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

internal static class QuasarAppearanceOperations
{
    internal sealed record AssetInput(string Kind, string Extension);

    internal static void AddQuasarAppearance(this IServiceCollection services)
    {
        const string plugin = QuasarCoreOperations.PluginId;
        string[] manage = [QuasarPolicyNames.CanManageAppearance];
        var write = new QuasarOperationMetadata { AgentAccess = QuasarAgentAccess.Denied, RequireDefaultPolicies = true };
        services.AddQuasarOperation<QuasarCoreOperations.EmptyRequest, BrandingSettings>(plugin, "appearance.read", [QuasarPolicyNames.CanView],
            (caller, _, _) => Task.FromResult(caller.Services.GetRequiredService<BrandingService>().GetSettings()),
            write with { Description = "Returns the active panel branding and palette.", Effect = QuasarOperationEffect.Read });
        services.AddQuasarOperation<BrandingSettings, BrandingSettings>(plugin, "appearance.save", manage, async (caller, input, token) =>
        {
            var branding = caller.Services.GetRequiredService<BrandingService>();
            var assets = branding.GetSettings();
            input.LogoDarkPath = assets.LogoDarkPath; input.LogoLightPath = assets.LogoLightPath; input.FaviconPath = assets.FaviconPath;
            await branding.SaveAsync(input, token);
            return branding.GetSettings();
        }, write with { Description = "Saves panel branding and palette preferences." });
        services.AddQuasarOperation<QuasarCoreOperations.EmptyRequest, BrandingSettings>(plugin, "appearance.reset", manage, async (caller, _, token) =>
        {
            var branding = caller.Services.GetRequiredService<BrandingService>();
            await branding.ResetToDefaultAsync(token);
            return branding.GetSettings();
        }, write with { Description = "Restores the default panel branding." });
        services.AddQuasarUpload<AssetInput, BrandingSettings>(plugin, "appearance.upload", manage, 10 * 1024 * 1024,
            (caller, input, stream, _) =>
            {
                var ext = input.Extension?.ToLowerInvariant();
                if (!(input.Kind switch
                {
                    "light" or "dark" => ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".svg",
                    "favicon" => ext is ".ico" or ".png",
                    _ => false,
                })) throw new QuasarOperationException(400, "Unsupported branding asset format.");
                var branding = caller.Services.GetRequiredService<BrandingService>();
                return Task.FromResult(new QuasarUpload<BrandingSettings>(async token =>
                {
                    if (input.Kind == "favicon") await branding.SaveFaviconAsync(stream, ext!, token);
                    else await branding.SaveLogoAsync(input.Kind == "dark", stream, ext!, token);
                    return branding.GetSettings();
                }));
            }, write with { Description = "Stores the uploaded branding asset." });
    }
}
