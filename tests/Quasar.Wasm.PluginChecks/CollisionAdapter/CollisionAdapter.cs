using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Navigation;
using Quasar.Plugin.Abstractions.Extensions;

public sealed class CollisionAdapter : IQuasarPlugin
{
    public string Id => "quasar.fixture.collision";
    public string DisplayName => "Conflicting route fixture";
    public void ConfigureServices(IServiceCollection services, QuasarPluginContext context) { }
    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context) { }
    public IEnumerable<Assembly> GetRazorAssemblies() => [typeof(ConflictingPage).Assembly];
    public IEnumerable<QuasarExtensionContribution> GetExtensions() => [];
    public IEnumerable<QuasarNavItem> GetNavItems() => [];
}
