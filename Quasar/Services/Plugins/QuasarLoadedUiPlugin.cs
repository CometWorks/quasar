using Quasar.Plugin.Abstractions;

namespace Quasar.Services.Plugins;

public sealed record QuasarLoadedUiPlugin(
    IQuasarPlugin Plugin,
    QuasarPluginContext Context)
{
    public string Id => Plugin.Id;

    public string DisplayName => Plugin.DisplayName;
}
