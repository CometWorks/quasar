using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Services;

namespace Quasar.Tests;

/// <summary>A bUnit context with the scoped services every Quasar component may inject.</summary>
public class QuasarBunitContext : BunitContext
{
    public QuasarBunitContext() => Services.AddScoped<ViewerTimeZone>();
}
