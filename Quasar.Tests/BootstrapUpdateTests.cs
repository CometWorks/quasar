extern alias Bootstrap;

using System.Reflection;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using BootstrapOptions = Bootstrap::Quasar.Bootstrap.BootstrapOptions;
using ForegroundOptions = Bootstrap::Quasar.Bootstrap.LauncherForegroundOptions;
using Coordinator = Bootstrap::Quasar.Bootstrap.LauncherCoordinator;
using BootstrapProgram = Bootstrap::Quasar.Bootstrap.Program;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class BootstrapUpdateTests
{
    [Theory]
    [InlineData("--rollback")]
    [InlineData("rollback")]
    public async Task RollbackWithoutRetainedReleaseFailsWithoutStartingProcesses(string argument)
    {
        await WithRootAsync(async () =>
        {
            Assert.Null(BootstrapProgram.FindInvalidLauncherArgument([argument]));
            Assert.Equal(1, await BootstrapProgram.Main([argument, "--quiet"]));
            Assert.False(File.Exists(MagnetarPaths.GetQuasarActiveReleasePath()));
        });
    }

    [Fact]
    public void HealthyCutoverRetainsOnePreviousReleaseAndSupportsRollbackWithoutDownload()
    {
        WithRoot(() =>
        {
            var first = CreateRelease("1.2.0");
            var previous = CreateRelease("1.3.0");
            var active = CreateRelease("1.4.0");
            Coordinator.RetainPreviousRelease(previous, first);
            Coordinator.RetainPreviousRelease(active, previous);
            Coordinator.RetainPreviousRelease(active, active); // Ordinary restarts must not erase history.
            Coordinator.RetainPreviousRelease(active, null); // A cold start has no in-memory predecessor.
            Publish(active);
            Coordinator.CleanupInactiveManagedWebReleases(active.WorkingDirectory);
            Assert.False(Directory.Exists(first.WorkingDirectory));
            Assert.True(File.Exists(previous.FileName));
            Assert.True(File.Exists(active.FileName));
            Coordinator.RequestRollback();
            Assert.Equal(previous.Version, ReadPointer().Version);
            Assert.Throws<InvalidOperationException>(Coordinator.RequestRollback);
            Coordinator.RetainPreviousRelease(previous, active); // Successful rollback can be reversed too.
            Coordinator.CleanupInactiveManagedWebReleases(previous.WorkingDirectory);
            Coordinator.RequestRollback();
            Assert.Equal(active.Version, ReadPointer().Version);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(MagnetarPaths.GetQuasarActiveReleasePath())!, "*.tmp"));
        });
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("deleted-worker")]
    public void UnusableRollbackLeavesActiveReleaseUntouched(string failure)
    {
        WithRoot(() =>
        {
            var active = CreateRelease("1.4.0");
            Publish(active);
            var path = MagnetarPaths.GetQuasarActiveReleasePath() + ".rollback";
            if (failure == "corrupt") File.WriteAllText(path, "{");
            if (failure == "deleted-worker")
            {
                var previous = CreateRelease("1.3.0");
                Coordinator.RetainPreviousRelease(active, previous);
                File.Delete(previous.FileName);
            }
            Assert.Throws<InvalidOperationException>(Coordinator.RequestRollback);
            Assert.Equal(active.Version, ReadPointer().Version);
        });
    }

    [Fact]
    public void CleanupKeepsStartupRecoveryAndLegacyStagedRollbackTargets()
    {
        WithRoot(() =>
        {
            var active = CreateRelease("1.4.0");
            var previous = CreateRelease("1.3.0");
            Publish(previous, MagnetarPaths.GetQuasarActiveReleasePath() + ".previous");
            Publish(active);
            Coordinator.CleanupInactiveManagedWebReleases(active.WorkingDirectory);
            Assert.True(File.Exists(previous.FileName));
            Coordinator.RequestRollback();
            Assert.Equal(previous.Version, ReadPointer().Version);

            using var coordinator = new Coordinator(new BootstrapOptions { Version = "1.4.0" },
                new ForegroundOptions(true), NullLogger<Coordinator>.Instance);
            var staged = Path.Combine(MagnetarPaths.GetQuasarStagingDirectory(), "1.2.0", "WebService");
            Directory.CreateDirectory(staged);
            previous.WorkingDirectory = staged;
            Coordinator.RetainPreviousRelease(active, previous);
            coordinator.CleanupCompletedStagedUpdates(active);
            Assert.True(Directory.Exists(staged));
        });
    }

    private static QuasarActiveReleasePointer CreateRelease(string version)
    {
        var directory = MagnetarPaths.GetQuasarManagedWebReleaseDirectory(version);
        Directory.CreateDirectory(directory);
        var worker = Path.Combine(directory, QuasarWebReleaseLayout.WorkerExecutableFileName);
        File.WriteAllText(worker, "worker placeholder, never executed");
        return new() { Version = version, WorkingDirectory = directory, FileName = worker };
    }

    private static QuasarActiveReleasePointer ReadPointer() => JsonSerializer.Deserialize<QuasarActiveReleasePointer>(
        File.ReadAllText(MagnetarPaths.GetQuasarActiveReleasePath()), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static void Publish(QuasarActiveReleasePointer pointer, string? path = null) =>
        typeof(Coordinator).GetMethod("WriteActiveReleasePointer", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [pointer, path]);

    [Theory]
    [InlineData("{", false)]
    [InlineData("null", false)]
    [InlineData("{}", false)]
    [InlineData("{\"version\":\"1.4.0\",\"webRelease\":{\"version\":\"1.4.0\",\"workingDirectory\":\"prepared\"}}", true)]
    public void LauncherOnlyConsumesVersionedRequests(string json, bool expected)
    {
        WithRoot(() =>
        {
            var path = MagnetarPaths.GetQuasarBootstrapUpdateRequestPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            var arguments = new object?[] { null };
            var consume = typeof(Coordinator).GetMethod("TryConsumeBootstrapUpdateRequest", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.Equal(expected, consume.Invoke(null, arguments));
            Assert.False(File.Exists(path));
            Assert.Equal(false, consume.Invoke(null, new object?[] { null }));
        });
    }

    [Fact]
    public void LauncherPublishesPointerAndRollbackCopyIndependently()
    {
        WithRoot(() =>
        {
            var write = typeof(Coordinator).GetMethod("WriteActiveReleasePointer", BindingFlags.Static | BindingFlags.NonPublic)!;
            var path = MagnetarPaths.GetQuasarActiveReleasePath();
            var old = new QuasarActiveReleasePointer { Version = "1.3.0", WorkingDirectory = "old" };
            var next = new QuasarActiveReleasePointer { Version = "1.4.0", WorkingDirectory = "new" };
            write.Invoke(null, [old, null]);
            write.Invoke(null, [old, path + ".previous"]);
            write.Invoke(null, [next, null]);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            Assert.Equal("1.4.0", JsonSerializer.Deserialize<QuasarActiveReleasePointer>(File.ReadAllText(path), options)!.Version);
            var previous = JsonSerializer.Deserialize<QuasarActiveReleasePointer>(File.ReadAllText(path + ".previous"), options)!;
            Assert.Equal("1.3.0", previous.Version);
            write.Invoke(null, [previous, null]);
            Assert.Equal("1.3.0", JsonSerializer.Deserialize<QuasarActiveReleasePointer>(File.ReadAllText(path), options)!.Version);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        });
    }

    [Fact]
    public void BootstrapInstallerPreservesPackagedWorkerForRollback()
    {
        WithRoot(() =>
        {
            var root = MagnetarPaths.GetQuasarDirectory();
            var source = Path.Combine(root, "payload");
            var install = Path.Combine(root, "install");
            Directory.CreateDirectory(Path.Combine(source, "WebService"));
            Directory.CreateDirectory(Path.Combine(install, "WebService"));
            File.WriteAllText(Path.Combine(source, "WebService", "worker"), "new worker");
            File.WriteAllText(Path.Combine(install, "WebService", "worker"), "old worker");
            File.WriteAllText(Path.Combine(source, "appsettings.json"), "new defaults");
            File.WriteAllText(Path.Combine(install, "appsettings.json"), "local settings");
            File.WriteAllText(Path.Combine(source, "launcher-component"), "new launcher");
            typeof(Coordinator).GetMethod("ApplyBootstrapUpdate", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [source, install]);
            Assert.Equal("old worker", File.ReadAllText(Path.Combine(install, "WebService", "worker")));
            Assert.Equal("local settings", File.ReadAllText(Path.Combine(install, "appsettings.json")));
            Assert.Equal("new launcher", File.ReadAllText(Path.Combine(install, "launcher-component")));
        });
    }

    [Fact]
    public void BootstrapRollbackKeepsTheReplacedLauncherForRecovery()
    {
        WithRoot(() =>
        {
            var root = MagnetarPaths.GetQuasarDirectory();
            var source = Path.Combine(root, "payload");
            var install = Path.Combine(root, "install");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(install);
            var launcher = OperatingSystem.IsWindows() ? "Quasar.exe" : "Quasar";
            File.WriteAllText(Path.Combine(source, launcher), "older launcher");
            File.WriteAllText(Path.Combine(install, launcher), "current launcher");
            typeof(Coordinator).GetMethod("ApplyBootstrapUpdate", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [source, install]);
            Assert.Equal("older launcher", File.ReadAllText(Path.Combine(install, launcher)));
            Assert.Equal("current launcher", File.ReadAllText(Path.Combine(install, launcher + ".previous")));
        });
    }

    [Theory]
    [InlineData("1.4.0", "1.4.0", "1.4.0", false)]
    [InlineData("1.4.0", "1.4.0", "Bootstrap-1.4.0", false)]
    [InlineData("1.4.0", "1.4.0", "1.3.1.69", false)]
    [InlineData("1.4.0", "1.4.0", "Bootstrap-1.3.1.69", false)]
    [InlineData("1.4.0", "1.4.0", "1.4.0-pr.299", false)]
    [InlineData("1.4.0-pr.10", "1.4.0-pr.10", "1.4.0-pr.9", false)]
    [InlineData("1.4.0-pr.9", "1.4.0-pr.9", "1.4.0-pr.10", true)]
    [InlineData("1.4.0", "1.4.0", "1.5.0", true)]
    [InlineData("1.4.0", "1.4.0", "Bootstrap-1.5.0", true)]
    [InlineData("1.4.0", "1.3.0", "Bootstrap-1.4.0", true)]
    [InlineData("1.3.0", "1.4.0", "1.4.0", true)]
    [InlineData("1.4.0", "1.4.0", "unrelated", true)]
    [InlineData("unknown", "unknown", "1.3.0", true)]
    public void HealthyWorkerCleanupRemovesOnlyCompletedComponentVersions(
        string webVersion, string bootstrapVersion, string stagedName, bool retained)
    {
        WithRoot(() =>
        {
            using var coordinator = new Coordinator(new BootstrapOptions { Version = bootstrapVersion },
                new ForegroundOptions(true), NullLogger<Coordinator>.Instance);
            var payload = Path.Combine(MagnetarPaths.GetQuasarStagingDirectory(), stagedName);
            Directory.CreateDirectory(Path.Combine(payload, "nested"));
            File.WriteAllText(Path.Combine(payload, "nested", "payload"), "extracted update");
            var active = new QuasarActiveReleasePointer
            {
                Version = webVersion,
                WorkingDirectory = MagnetarPaths.GetQuasarManagedWebReleaseDirectory(webVersion),
            };
            coordinator.CleanupCompletedStagedUpdates(active);
            coordinator.CleanupCompletedStagedUpdates(active); // Safe to retry on each worker start.
            Assert.Equal(retained, Directory.Exists(payload));
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("WebService")]
    public void HealthyWorkerCleanupPreservesLegacyWorkerRunningFromStaging(string subdirectory)
    {
        WithRoot(() =>
        {
            using var coordinator = new Coordinator(new BootstrapOptions { Version = "1.4.0" },
                new ForegroundOptions(true), NullLogger<Coordinator>.Instance);
            var payload = Path.Combine(MagnetarPaths.GetQuasarStagingDirectory(), "1.3.0");
            var activeDirectory = Path.Combine(payload, subdirectory);
            Directory.CreateDirectory(activeDirectory);
            var worker = Path.Combine(activeDirectory, "worker");
            File.WriteAllText(worker, "running worker");
            coordinator.CleanupCompletedStagedUpdates(new QuasarActiveReleasePointer
            {
                Version = "1.3.0", WorkingDirectory = activeDirectory,
            });
            Assert.True(File.Exists(worker));
        });
    }

    private static void WithRoot(Action action)
        => WithRootAsync(() => { action(); return Task.CompletedTask; }).GetAwaiter().GetResult();

    private static async Task WithRootAsync(Func<Task> action)
    {
        var installDirectory = Environment.GetEnvironmentVariable("QUASAR_INSTALL_DIR");
        var cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = cache.GetValue(null);
        var directory = Path.Combine(Path.GetTempPath(), "quasar-bootstrap-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); cache.SetValue(null, directory);
        try { await action(); }
        finally
        {
            Environment.SetEnvironmentVariable("QUASAR_INSTALL_DIR", installDirectory);
            cache.SetValue(null, previous);
            Directory.Delete(directory, true);
        }
    }
}
