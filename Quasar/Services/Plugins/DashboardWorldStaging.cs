using Quasar.Client.Fleet;

namespace Quasar.Services.Plugins;

public sealed class DashboardWorldStaging() : DashboardImportStaging<WorldTarget, DashboardWorldStage>(target => target.Id);

public sealed class DashboardWorldStage : IDisposable
{
    internal StagedWorld? Files;
    internal string Path = "";
    internal string? TemplateId;
    public void Dispose() => Files?.Dispose();
}
