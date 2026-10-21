using System.Reflection;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

// SE1-0195: definition files are watched at their own depth only, and a lost event batch
// still reloads.
public sealed class DefinitionFileWatcherTests : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"quasar-watch-{Guid.NewGuid():N}"
    );

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void FindFilesReturnsOnlyFilesOneLevelDown()
    {
        Write("b/server.json");
        Write("a/server.json");
        Write("server.json");
        Write("a/Magnetar/server.json");
        Write("c/other.json");

        Assert.Equal(
            [Path.Combine(_root, "a", "server.json"), Path.Combine(_root, "b", "server.json")],
            ChildFileWatcher.FindFiles(_root, "server.json")
        );
        Assert.Empty(ChildFileWatcher.FindFiles(Path.Combine(_root, "missing"), "server.json"));
    }

    [Fact]
    public async Task ChildFileWatcherSeesEditsAndNewDefinitions()
    {
        Write("a/server.json");
        var changes = new SemaphoreSlim(0);
        using var watcher = new ChildFileWatcher(_root, "server.json", () => changes.Release());

        Write("a/server.json", "{ \"edited\": true }");
        Assert.True(await changes.WaitAsync(Timeout));

        Write("b/server.json");
        Assert.True(await changes.WaitAsync(Timeout));

        File.Delete(Path.Combine(_root, "a", "server.json"));
        Assert.True(await changes.WaitAsync(Timeout));
    }

    [Fact]
    public async Task ChildFileWatcherIgnoresTheRestOfTheTree()
    {
        Write("a/server.json");
        var changes = new SemaphoreSlim(0);
        using var watcher = new ChildFileWatcher(_root, "server.json", () => changes.Release());

        Write("a/Magnetar/server.json");
        Write("a/DedicatedServer/Saves/world/Sandbox.sbc");
        Write("a/other.json");

        Assert.False(await changes.WaitAsync(Quiet));
    }

    [Fact]
    public async Task DebouncedFileWatcherReloadsAfterAnError()
    {
        var path = Path.Combine(_root, "discord.json");
        var changes = new SemaphoreSlim(0);
        using var watcher = DebouncedFileWatcher.WatchFile(path, () => changes.Release())!;
        var fileSystemWatcher = (FileSystemWatcher)
            typeof(DebouncedFileWatcher)
                .GetField("_watcher", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(watcher)!;

        typeof(FileSystemWatcher)
            .GetMethod("OnError", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(fileSystemWatcher, [new ErrorEventArgs(new InternalBufferOverflowException())]);

        Assert.True(await changes.WaitAsync(Timeout));
    }

    private void Write(string relativePath, string content = "{}")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
