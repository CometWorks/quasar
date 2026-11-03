using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using System.Net;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quasar.Updates;

internal static class NativeFixture
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args[0] == "check-upgrade-path")
        {
            var package = args[Array.IndexOf(args, "--package")+1];
            var dataPath = args[Array.IndexOf(args, "--data-dir")+1];
            QuasarUpgradePath.EnsureAllowed(QuasarUpgradePath.PackageVersion(package), dataPath);
            return 0;
        }
        if (args[0] == "configure") return ConfigureInstallation.Run(args.Skip(1).ToArray());
        var metadata = args[0] == "--installation" ? args[1] : Path.Combine(Environment.GetEnvironmentVariable("QUASAR_INSTALL_DIR") ?? AppContext.BaseDirectory, QuasarServiceInstallation.FileName);
        var installation = QuasarServiceInstallation.Load(metadata, true)!;
        var data = installation.DataDirectory;
        if (args[0] == "token-create")
        {
            var provider = DataProtectionProvider.Create(new DirectoryInfo(MagnetarPaths.GetQuasarDataProtectionKeyringDirectory()), options => options.SetApplicationName("Quasar"));
            var token = provider.CreateProtector(GitHubUpdateTokenReader.DataProtectionPurpose).Protect("update-fixture-token");
            QuasarUpdateJournal.WriteJson(MagnetarPaths.GetQuasarGitHubUpdateCredentialsPath(), new { protectedToken = token });
            return 0;
        }
        if (args[0] == "request")
        {
            using var lease = await QuasarMaintenanceLease.AcquireAsync(CancellationToken.None);
            var previous = QuasarUpdateJournal.Read();
            if (previous?.Stage is UpdateStage.Ready or UpdateStage.Restarting) { Console.WriteLine(previous.Id); return 0; }
            var id = Guid.NewGuid();
            var settings = Path.Combine(data, "Updates", "Staged", "Package-" + id.ToString("N"), "Settings", "appsettings.json");
            QuasarUpdateJournal.WriteText(settings, File.ReadAllText(Path.Combine(data, "appsettings.json")));
            var operation = new QuasarUpdateOperation { Id = id, Version = File.ReadAllText(Path.Combine(args[1], "fixture-release.txt")).Trim(),
                PackageDirectory = args[1], PreviousVersion = File.ReadAllText(Path.Combine(installation.InstallDirectory, "fixture-release.txt")).Trim(), Stage = UpdateStage.Ready, ResolvedSettings = settings };
            QuasarUpdateJournal.Write(operation); Console.WriteLine(operation.Id); return 0;
        }
        if (args[0] == "--installation")
        {
            if (OperatingSystem.IsWindows()) VerifyWindowsAccess(installation, updater: true);
            var builder = Host.CreateApplicationBuilder();
            if (OperatingSystem.IsWindows())
            {
                builder.Services.AddWindowsService(options => options.ServiceName = installation.UpdaterService);
                builder.Services.Configure<Microsoft.Extensions.Logging.EventLog.EventLogSettings>(options => { if (OperatingSystem.IsWindows()) options.SourceName = installation.UpdaterService; });
            }
            builder.Services.AddSingleton(provider => new QuasarUpdater(installation, provider.GetRequiredService<ILogger<QuasarUpdater>>(),
                async (_, operation, token) => { await VerifiedUpdatePackage.VerifyAsync(operation.PackageDirectory, token); return operation.PackageDirectory; }));
            builder.Services.AddHostedService(provider => provider.GetRequiredService<QuasarUpdater>());
            if (File.Exists(MagnetarPaths.GetQuasarGitHubUpdateCredentialsPath()))
            {
                var saved = GitHubUpdateTokenReader.ReadSavedToken(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                if (saved != "update-fixture-token") throw new InvalidOperationException("Saved GitHub token/keyring was not readable by the updater identity.");
                File.WriteAllText(Path.Combine(data, "token-reader-ok"), "ok");
            }
            var host = builder.Build(); NativeServiceFailures.Observe(host);
            await host.RunAsync(); return Environment.ExitCode;
        }
        if (OperatingSystem.IsWindows()) VerifyWindowsAccess(installation, updater: false);
        var version = File.ReadAllText(Path.Combine(installation.InstallDirectory, "fixture-release.txt")).Trim();
        var appBuilder = WebApplication.CreateBuilder();
        appBuilder.WebHost.UseUrls(installation.HealthUrl);
        if (OperatingSystem.IsWindows())
        {
            appBuilder.Services.AddWindowsService(options => options.ServiceName = installation.MainService);
            appBuilder.Services.Configure<Microsoft.Extensions.Logging.EventLog.EventLogSettings>(options => { if (OperatingSystem.IsWindows()) options.SourceName = installation.MainService; });
        }
        appBuilder.Services.AddSingleton(installation);
        appBuilder.Services.AddHostedService<BackgroundFailureFixture>();
        var app = appBuilder.Build();
        NativeServiceFailures.Observe(app);
        app.MapGet("/api/health", () => Results.Ok());
        app.MapGet("/api/internal/update-health", (HttpContext context) => context.Request.Headers["X-Quasar-Launcher-Token"] != installation.HealthToken
            ? Results.Unauthorized() : File.Exists(Path.Combine(data, "hold-health")) && File.ReadAllText(Path.Combine(data, "hold-health")) == version
                ? Results.StatusCode(503) : Results.Json(new { pid = Environment.ProcessId, version, started = Process.GetCurrentProcess().StartTime.ToUniversalTime() }));
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            File.AppendAllText(Path.Combine(data, "starts.txt"), version + ":" + Environment.ProcessId + "\n");
            QuasarUpdateJournal.WriteJson(Path.Combine(data, "service-manifest.json"), new { processId = Environment.ProcessId, startedAtUtc = DateTimeOffset.UtcNow,
                baseUrl = installation.HealthUrl, workerId = Environment.ProcessId.ToString() });
            var sentinelFile = Path.Combine(data, "server.pid");
            var exists = false;
            if (File.Exists(sentinelFile)) { try { using var old = Process.GetProcessById(int.Parse(File.ReadAllText(sentinelFile))); exists = !old.HasExited; } catch (ArgumentException) { } }
            if (!exists)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                start.ArgumentList.Add("sentinel");
                using var sentinel = Process.Start(start)!; File.WriteAllText(sentinelFile, sentinel.Id.ToString());
            }
        });
        _ = Task.Run(async () =>
        {
            var exitFile = Path.Combine(data, "exit");
            while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
            {
                if (File.Exists(exitFile))
                {
                    var code = int.Parse(File.ReadAllText(exitFile)); File.Delete(exitFile);
                    if (code != 0) Environment.Exit(code);
                    app.Lifetime.StopApplication(); return;
                }
                await Task.Delay(100);
            }
        });
        await app.RunAsync(); return Environment.ExitCode;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void VerifyWindowsAccess(QuasarServiceInstallation installation, bool updater)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var expected = updater ? installation.UpdaterAccount : installation.Account;
        if (!string.Equals(identity.Name, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"ACL fixture must run as {expected}, actually {identity.Name} ({identity.User}).");
        void Access(string path, FileAccess access, bool allowed)
        {
            try
            {
                using var stream = File.Open(path, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete);
                if (!allowed) throw new InvalidOperationException($"{identity.Name} unexpectedly has {access} access to {path}.");
            }
            catch (UnauthorizedAccessException) when (!allowed) { }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"ACL probe failed: {identity.Name} ({identity.User}), {path}, {access}, expected allowed={allowed}.", exception);
            }
        }
        var root = installation.InstallDirectory;
        foreach (var file in new[] { "release.json", "fixture-release.txt", "Quasar.exe" })
            Access(Path.Combine(root, file), FileAccess.Read, true);
        var coLocated = string.Equals(root, installation.DataDirectory, StringComparison.OrdinalIgnoreCase);
        Access(Path.Combine(root, "fixture-release.txt"), FileAccess.Write, updater || coLocated);
        Access(Path.Combine(installation.DataDirectory, "appsettings.json"), FileAccess.ReadWrite, true);
        foreach (var metadata in new[] { installation.MetadataPath, Path.Combine(root, QuasarServiceInstallation.FileName) })
        {
            Access(metadata, FileAccess.Read, true);
            Access(metadata, FileAccess.Write, false);
        }
        // Unmapped probe files distinguish denied ACL access from image-lock sharing violations.
        var updaterProbe = Path.Combine(root, "Updater", "acl-probe.txt");
        Access(updaterProbe, FileAccess.Read, updater);
        Access(updaterProbe, FileAccess.Write, false);
        Access(Path.Combine(root, ".update-cache", "acl-probe.txt"), FileAccess.ReadWrite, updater);
        var scratch = Path.Combine(installation.DataDirectory, "acl-" + (updater ? "updater" : "main") + ".tmp");
        File.WriteAllText(scratch, "service data access");
        File.Delete(scratch);
        QuasarUpdateJournal.WriteJson(Path.Combine(installation.DataDirectory, "acl-" + (updater ? "updater" : "main") + ".json"),
            new { account = identity.Name, sid = identity.User!.Value, pid = Environment.ProcessId, coLocated });
    }
}

internal sealed class BackgroundFailureFixture(QuasarServiceInstallation installation) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var trigger = Path.Combine(installation.DataDirectory, "fatal-background");
        while (!token.IsCancellationRequested)
        {
            if (File.Exists(trigger)) { File.Delete(trigger); throw new InvalidOperationException("Injected background-service failure."); }
            await Task.Delay(100, token);
        }
    }
}
