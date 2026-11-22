using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Navigation;
using Quasar.Plugin.Abstractions.Extensions;

public sealed class WorkerAdapter : IQuasarPlugin
{
    public string Id => "quasar.fixture.worker";
    public string DisplayName => "Worker-only fixture";
    public void ConfigureServices(IServiceCollection services, QuasarPluginContext context) { }
    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context) { }
    public IEnumerable<Assembly> GetRazorAssemblies() => [Assembly.GetEntryAssembly()!];
    public IEnumerable<QuasarExtensionContribution> GetExtensions() => [];
    public IEnumerable<QuasarNavItem> GetNavItems() => [];
}
