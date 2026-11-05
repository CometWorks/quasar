using System.Reflection;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Backup;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class ServerWorldImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quasar-world-import-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previous;
    private readonly DedicatedServerCatalog _servers;
    private readonly QuasarConfigProfileCatalog _profiles;
    private readonly ServerRestoreCoordinator _maintenance = new();
    private readonly DedicatedServerSupervisor _supervisor;
    private readonly ServerWorldImportService _imports;

    public ServerWorldImportTests()
    {
        _previous = _cache.GetValue(null);
        _cache.SetValue(null, _root);
        _servers = new(NullLogger<DedicatedServerCatalog>.Instance, _maintenance);
        _profiles = new(NullLogger<QuasarConfigProfileCatalog>.Instance);
        _supervisor = new(_servers, new AgentRegistry(null!, null!, null!, null!), null!, null!, null!,
            new WebServiceOptions(), _maintenance, NullLogger<DedicatedServerSupervisor>.Instance);
        _imports = new(_servers, _profiles, _supervisor, _maintenance, NullLogger<ServerWorldImportService>.Instance);
    }

    private async Task<DedicatedServerDefinition> Create()
    {
        await _profiles.UpsertAsync(new() { ConfigProfileId = "original", Name = "Shared", Plugins = [new() { PluginId = "preserved" }] });
        await _servers.UpsertAsync(new() { UniqueName = "target", WorldSaveName = "Old", WorldTemplateId = "old-template",
            ConfigProfileId = "original", ServerPort = 27050, WorldPath = Path.Combine(_root, "custom-saves") });
        var server = _servers.GetServer("target")!;
        Directory.CreateDirectory(server.GetWorldSavePath());
        File.WriteAllText(Path.Combine(server.GetWorldSavePath(), "keep.txt"), "previous world");
        return server;
    }

    private async Task<StagedWorld> Stage(DedicatedServerDefinition server)
    {
        string source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        File.WriteAllText(Path.Combine(source, "Sandbox_config.sbc"),
            "<MyObjectBuilder_WorldConfiguration><Settings><OnlineMode>Offline</OnlineMode><MaxPlayers>8</MaxPlayers></Settings><Mods><ModItem><PublishedFileId>123</PublishedFileId></ModItem></Mods></MyObjectBuilder_WorldConfiguration>");
        return await WorldSaveFiles.FromFolderAsync(source, DedicatedServerPathResolver.Resolve(server).SavesPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportSelectsCompleteWorldPreservesPreviousSaveAndDoesNotCreateTemplate(bool settings)
    {
        var server = await Create();
        using var stage = await Stage(server);
        var updated = await _imports.ImportAsync(server, stage, "New", settings);
        Assert.Equal("New", _servers.GetServer("target")!.WorldSaveName);
        Assert.Equal("", updated.WorldTemplateId);
        Assert.Equal(27050, updated.ServerPort);
        Assert.Equal(DedicatedServerGoalState.Off, updated.GoalState);
        Assert.False(updated.AutoStart);
        Assert.Equal("previous world", File.ReadAllText(Path.Combine(server.GetWorldSavePath(), "keep.txt")));
        Assert.True(File.Exists(Path.Combine(updated.GetWorldSavePath(), "Sandbox.sbc")));
        Assert.False(Directory.Exists(MagnetarPaths.GetQuasarWorldTemplatesDirectory()));
        Assert.Empty(_profiles.GetProfile("original")!.Mods);
        if (settings)
        {
            Assert.NotEqual("original", updated.ConfigProfileId);
            var profile = _profiles.GetProfile(updated.ConfigProfileId)!;
            Assert.Equal("preserved", Assert.Single(profile.Plugins).PluginId);
            Assert.Equal(123, Assert.Single(profile.Mods).WorkshopId);
            Assert.Equal(8, profile.SessionSettings.MaxPlayers);
            Assert.Equal(_profiles.GetProfile("original")!.SessionSettings.OnlineMode, profile.SessionSettings.OnlineMode);
        }
        else Assert.Equal("original", updated.ConfigProfileId);
    }

    [Fact]
    public async Task StaleDefinitionAndExistingSaveAreRejectedWithoutLosingData()
    {
        var server = await Create();
        using var stage = await Stage(server);
        var changed = server.Clone();
        changed.DisplayName = "Edited";
        await _servers.UpsertAsync(changed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _imports.ImportAsync(server, stage, "New", true));
        Assert.False(Directory.Exists(Path.Combine(DedicatedServerPathResolver.Resolve(server).SavesPath, "New")));
        server = _servers.GetServer("target")!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _imports.ImportAsync(server, stage, "Old", false));
        Assert.Equal("previous world", File.ReadAllText(Path.Combine(server.GetWorldSavePath(), "keep.txt")));
        Assert.Single(_profiles.GetProfiles());
    }

    [Fact]
    public async Task MaintenanceExcludesRestoreStartEditsRenameAndDelete()
    {
        var server = await Create();
        Assert.True(_maintenance.TryBeginWorldImport("target", out var scope));
        using (scope)
        {
            Assert.False(_maintenance.TryBeginRestore("target", out _));
            Assert.False(_maintenance.TryBeginBackup("target", out _));
            Assert.False(_maintenance.TryBeginWorldImport("target", out _));
            Assert.True(_maintenance.IsFileOperationInProgress("target"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => _servers.SetGoalStateAsync("target", DedicatedServerGoalState.On));
            var rename = server.Clone(); rename.UniqueName = "renamed";
            await Assert.ThrowsAsync<InvalidOperationException>(() => _servers.UpsertAsync(rename));
            await Assert.ThrowsAsync<InvalidOperationException>(() => _servers.DeleteAsync("target"));
            typeof(DedicatedServerSupervisor).GetMethod("SyncDefinitions", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_supervisor, null);
            await _supervisor.StartServerAsync("target");
            Assert.False(_supervisor.IsServerProcessActive("target"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => _supervisor.RestartServerAsync("target"));
            Assert.Null(Assert.Single(_supervisor.GetSnapshots()).LastRestart);
        }
        Assert.False(_maintenance.IsFileOperationInProgress("target"));
        Assert.True(_maintenance.TryBeginBackup("target", out var backupScope));
        using (backupScope)
        {
            Assert.False(_maintenance.TryBeginWorldImport("target", out _));
            Assert.False(_imports.CanImport(server));
        }
    }

    [Fact]
    public async Task ManagedSourceCopyExcludesStartsAndRefusesOnGoals()
    {
        var server = await Create();
        File.WriteAllText(Path.Combine(server.GetWorldSavePath(), "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        bool observed = false;
        var progress = new ObserveProgress(value =>
        {
            observed = true;
            Assert.True(_maintenance.IsWorldImportInProgress("target"));
            Assert.False(_maintenance.TryBeginRestore("target", out _));
        });
        using (await _imports.StageFolderAsync(server.GetWorldSavePath(), Path.Combine(_root, "staging"), progress)) { }
        Assert.True(observed);
        Assert.False(_maintenance.IsWorldImportInProgress("target"));
        await _servers.SetGoalStateAsync("target", DedicatedServerGoalState.On);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _imports.StageFolderAsync(server.GetWorldSavePath(), Path.Combine(_root, "staging")));
        Assert.False(_maintenance.IsWorldImportInProgress("target"));
    }

    private sealed class ObserveProgress(Action<WorldImportProgress> observe) : IProgress<WorldImportProgress>
    { public void Report(WorldImportProgress value) => observe(value); }

    [Fact]
    public async Task FailedCatalogCommitRollsBackPublishedFilesAndNewProfile()
    {
        var server = await Create();
        using var stage = await Stage(server);
        // An external edit not yet seen by the catalog must still invalidate activation.
        string path = MagnetarPaths.GetQuasarServerDefinitionPath("target");
        File.AppendAllText(path, " invalid JSON");
        await Assert.ThrowsAnyAsync<Exception>(() => _imports.ImportAsync(server, stage, "New", true));
        Assert.False(Directory.Exists(Path.Combine(DedicatedServerPathResolver.Resolve(server).SavesPath, "New")));
        Assert.Single(_profiles.GetProfiles());
        Assert.Equal("Old", _servers.GetServer("target")!.WorldSaveName);
    }

    public void Dispose()
    {
        _supervisor.Dispose(); _servers.Dispose(); _profiles.Dispose();
        _cache.SetValue(null, _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
