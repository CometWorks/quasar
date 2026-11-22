using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Navigation;
using Quasar.Plugin.Abstractions.Extensions;

public sealed class RouteAdapter : IQuasarPlugin
{
    public string Id => "quasar.fixture.routes";
    public string DisplayName => "Browser route fixture";
    public void ConfigureServices(IServiceCollection services, QuasarPluginContext context) { }
    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context) { }
    public IEnumerable<Assembly> GetRazorAssemblies() => [typeof(ViewerFixture.ViewerAction).Assembly, typeof(RouteAdapter).Assembly, Assembly.GetEntryAssembly()!];
    public IEnumerable<QuasarExtensionContribution> GetExtensions() => [];
    public IEnumerable<QuasarNavItem> GetNavItems() => [];
}


// A plugin cannot replace either a stock browser route or a worker compatibility route.
[Microsoft.AspNetCore.Components.Route("/players")]
[Microsoft.AspNetCore.Components.Route("/compat/entities")]
public sealed class ConflictingPage : Microsoft.AspNetCore.Components.ComponentBase { }
