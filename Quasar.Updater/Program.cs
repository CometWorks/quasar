using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quasar.Updates;

if (args.FirstOrDefault() == "check-upgrade-path")
{
    try
    {
        string Option(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException("Missing " + name); }
        QuasarUpgradePath.EnsureAllowed(QuasarUpgradePath.PackageVersion(Option("--package")), Option("--data-dir"));
        return 0;
    }
    catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 3; }
}
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
