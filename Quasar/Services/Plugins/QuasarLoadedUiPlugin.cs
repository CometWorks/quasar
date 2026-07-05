using Quasar.Plugin.Abstractions;

namespace Quasar.Services.Plugins;

public sealed record QuasarLoadedUiPlugin(
    IQuasarPlugin Plugin,
    QuasarPluginContext Context,
    string EntryAssemblyPath,
    string LoadedAssemblyPath,
    string ShadowCopyDirectory,
    string ShadowCopyHash,
    string? SourceStaticAssetsDirectory)
{
    public string Id => Plugin.Id;

    public string DisplayName => Plugin.DisplayName;
}
