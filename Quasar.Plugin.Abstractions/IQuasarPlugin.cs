using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Extensions;
using Quasar.Plugin.Abstractions.Navigation;

namespace Quasar.Plugin.Abstractions;

public interface IQuasarPlugin
{
    string Id { get; }

    string DisplayName { get; }

    void ConfigureServices(IServiceCollection services, QuasarPluginContext context);

    void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context);

    IEnumerable<Assembly> GetRazorAssemblies();

    IEnumerable<QuasarNavItem> GetNavItems();

    IEnumerable<QuasarExtensionContribution> GetExtensions();
}
