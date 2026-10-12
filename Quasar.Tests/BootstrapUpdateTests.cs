extern alias Bootstrap;

using System.Reflection;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Xunit;
using Coordinator = Bootstrap::Quasar.Bootstrap.LauncherCoordinator;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class BootstrapUpdateTests
{
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

    private static void WithRoot(Action action)
    {
        var cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = cache.GetValue(null);
        var directory = Path.Combine(Path.GetTempPath(), "quasar-bootstrap-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); cache.SetValue(null, directory);
        try { action(); }
        finally { cache.SetValue(null, previous); Directory.Delete(directory, true); }
    }
}
