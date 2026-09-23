using System.Reflection;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

[CollectionDefinition("Exact server creation", DisableParallelization = true)]
public sealed class ExactServerCreationCollection;

[Collection("Exact server creation")]
public sealed class DedicatedServerExactCreationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quasar-exact-create-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previousRoot;
    private readonly DedicatedServerCatalog _catalog;

    public DedicatedServerExactCreationTests()
    {
        _previousRoot = _cache.GetValue(null);
        _cache.SetValue(null, _root);
        _catalog = new(NullLogger<DedicatedServerCatalog>.Instance);
    }

    private static DedicatedServerDefinition Definition() => new()
    { UniqueName = "target", WorldSaveName = "World", GoalState = DedicatedServerGoalState.On, AutoStart = true };

    private static Task Prepare(DedicatedServerDefinition definition, CancellationToken token)
    {
        var paths = DedicatedServerPathResolver.Resolve(definition);
        Directory.CreateDirectory(paths.WorldSavePath);
        return File.WriteAllTextAsync(Path.Combine(paths.WorldSavePath, "Sandbox.sbc"), "world", token);
    }

    [Fact]
    public async Task PublishesOnlyPreparedOffServerAtExactName()
    {
        var created = await _catalog.CreateExactAsync(Definition(), async (definition, token) =>
        {
            Assert.Null(_catalog.GetServer("target"));
            Assert.False(File.Exists(MagnetarPaths.GetQuasarServerDefinitionPath("target")));
            Assert.Equal(DedicatedServerGoalState.Off, definition.GoalState);
            Assert.False(definition.AutoStart);
            await Prepare(definition, token);
            definition.UniqueName = "cannot-change-published-identity";
        });
        Assert.Equal("target", created.UniqueName);
        Assert.Equal(DedicatedServerGoalState.Off, _catalog.GetServer("target")!.GoalState);
        Assert.True(File.Exists(Path.Combine(created.GetWorldSavePath(), "Sandbox.sbc")));
        Assert.False(Directory.Exists(MagnetarPaths.GetQuasarServerDirectory("cannot-change-published-identity")));
    }

    [Fact]
    public async Task ExistingDirectoryIsNeverTouchedOrRenamed()
    {
        string root = MagnetarPaths.GetQuasarServerDirectory("target");
        Directory.CreateDirectory(root);
        string preserved = Path.Combine(root, "source.txt");
        await File.WriteAllTextAsync(preserved, "preserve");
        bool prepared = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _catalog.CreateExactAsync(Definition(), (_, _) =>
        { prepared = true; return Task.CompletedTask; }));
        Assert.False(prepared);
        Assert.Equal("preserve", await File.ReadAllTextAsync(preserved));
        Assert.Empty(_catalog.GetServers());
    }

    [Fact]
    public async Task PreparationFailureRemovesOnlyOwnedDirectory()
    {
        await _catalog.UpsertAsync(new() { UniqueName = "source", WorldSaveName = "World" });
        await Assert.ThrowsAsync<IOException>(() => _catalog.CreateExactAsync(Definition(), async (definition, token) =>
        { await Prepare(definition, token); throw new IOException("copy failed"); }));
        Assert.Null(_catalog.GetServer("target"));
        Assert.False(Directory.Exists(MagnetarPaths.GetQuasarServerDirectory("target")));
        Assert.Equal("source", Assert.Single(_catalog.GetServers()).UniqueName);
    }

    [Fact]
    public async Task ConcurrentUpsertWaitsForPublicationThenAvoidsCollision()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var create = _catalog.CreateExactAsync(Definition(), async (definition, token) =>
        { entered.SetResult(); await release.Task; await Prepare(definition, token); });
        await entered.Task;
        var upsert = _catalog.UpsertAsync(Definition());
        Assert.False(upsert.IsCompleted);
        release.SetResult();
        await Task.WhenAll(create, upsert);
        Assert.Equal(2, _catalog.GetServers().Count);
        Assert.Equal(DedicatedServerGoalState.Off, _catalog.GetServer("target")!.GoalState);
    }

    [Fact]
    public async Task ExternalSourcePathsAreRefusedBeforePreparation()
    {
        var definition = Definition();
        definition.WorldPath = Path.GetTempPath();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _catalog.CreateExactAsync(definition, Prepare));
        Assert.False(Directory.Exists(MagnetarPaths.GetQuasarServerDirectory("target")));
    }

    public void Dispose()
    {
        _catalog.Dispose();
        _cache.SetValue(null, _previousRoot);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
