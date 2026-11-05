using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quasar.Updates;

if (args.FirstOrDefault() == "check-stopped") return InstallationProcesses.Check(args);
if (args.FirstOrDefault() == "recover") return await RecoverInstallation.RunAsync(args);
if (args.FirstOrDefault() == "configure") return ConfigureInstallation.Run(args.Skip(1).ToArray());
var index = Array.IndexOf(args, "--installation");
if (index < 0 || index + 1 >= args.Length) { Console.Error.WriteLine("Quasar.Updater --installation <protected service-installation.json>"); return 2; }
var installation = QuasarServiceInstallation.Load(args[index + 1], applyEnvironment: true) ?? throw new InvalidDataException("Service installation is missing.");
var builder = Host.CreateApplicationBuilder();
builder.Services.AddSingleton(installation);
if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options => options.ServiceName = installation.UpdaterService);
    builder.Services.Configure<Microsoft.Extensions.Logging.EventLog.EventLogSettings>(options => { if (OperatingSystem.IsWindows()) options.SourceName = installation.UpdaterService; });
}
builder.Services.AddHostedService<QuasarUpdater>();
var host = builder.Build();
NativeServiceFailures.Observe(host);
await host.RunAsync();
return Environment.ExitCode;
