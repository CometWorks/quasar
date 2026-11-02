using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Quasar.Updates;

internal static class NativeServiceFailures
{
    internal static void Observe(IHost host) => host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
    {
        // Generic Host otherwise turns a failed BackgroundService into a graceful
        // exit 0, defeating both systemd and SCM failure recovery.
        if (!host.Services.GetServices<IHostedService>().OfType<BackgroundService>().Any(service => service.ExecuteTask?.IsFaulted == true)) return;
        Environment.ExitCode = 1;
        if (OperatingSystem.IsWindows() && host.Services.GetService<IHostLifetime>() is WindowsServiceLifetime serviceLifetime)
            serviceLifetime.ExitCode = 1;
    });
}
