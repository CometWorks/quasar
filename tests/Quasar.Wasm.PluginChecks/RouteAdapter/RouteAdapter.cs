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
    public IEnumerable<Assembly> GetRazorAssemblies() => [typeof(ViewerFixture.ViewerAction).Assembly];
    public IEnumerable<QuasarExtensionContribution> GetExtensions() => [];
    public IEnumerable<QuasarNavItem> GetNavItems() => [];
}
