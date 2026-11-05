using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Channels;
using Bunit;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Runtime;
using Magnetar.Protocol.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Components.Pages;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Backup;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class WorldBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quasar-world-backup-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? previousRoot;
    private readonly DedicatedServerCatalog servers;
    private readonly KnownPlayerCatalog players;
    private readonly AgentRegistry registry;
    private readonly ServerRestoreCoordinator operations = new();
    private readonly DedicatedServerSupervisor supervisor;
    private readonly QuasarBackupService backups;
    private readonly WebServiceOptions options;
    private readonly QuasarBackupSettingsService settings;
    private readonly AutomaticBackupService queue;
    private readonly Channel<ServerCommandEnvelope> commands = Channel.CreateUnbounded<ServerCommandEnvelope>();

    public WorldBackupTests()
    {
        previousRoot = cache.GetValue(null);
        cache.SetValue(null, root);
        Directory.CreateDirectory(root);
        servers = new(NullLogger<DedicatedServerCatalog>.Instance);
        players = new(NullLogger<KnownPlayerCatalog>.Instance);
        registry = new(players, null!, null!, null!);
        options = new() { BackupDirectory = Path.Combine(root, "backups"), Version = "1.4.0" };
        supervisor = new(servers, registry, null!, null!, null!, options, operations, NullLogger<DedicatedServerSupervisor>.Instance);
        servers.Changed += typeof(DedicatedServerSupervisor)
            .GetMethod("SyncDefinitions", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Action>(supervisor);
        backups = new(NullLogger<QuasarBackupService>.Instance, options, players, null!, servers, supervisor, operations);
        settings = new(NullLogger<QuasarBackupSettingsService>.Instance, options);
        queue = new(backups, settings, servers, NullLogger<AutomaticBackupService>.Instance);
        registry.UpsertHello(new AgentHello { AgentId = "agent", UniqueName = "test", ServerId = "test" }, "connection",
            (message, _) => { commands.Writer.TryWrite(message.Command!); return Task.CompletedTask; });
    }

    private async Task<string> CreateWorld(bool running = false)
    {
        await servers.UpsertAsync(new DedicatedServerDefinition
        {
            UniqueName = "test", DisplayName = "Test", WorldSaveName = "World",
            GoalState = running ? DedicatedServerGoalState.On : DedicatedServerGoalState.Off,
        });
        typeof(DedicatedServerSupervisor).GetMethod("SyncDefinitions", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(supervisor, null);
        if (running)
            SetProcessState(DedicatedServerProcessState.Running);
        var world = servers.GetServer("test")!.GetWorldSavePath();
        Directory.CreateDirectory(world);
        await File.WriteAllTextAsync(Path.Combine(world, "Sandbox.sbc"), "latest save");
        return world;
    }

    private void SetProcessState(DedicatedServerProcessState state, Process? process = null)
    {
        var states = (IDictionary)typeof(DedicatedServerSupervisor).GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(supervisor)!;
        var managed = states["test"]!;
        managed.GetType().GetProperty("State")!.SetValue(managed, state);
        managed.GetType().GetProperty("Process")!.SetValue(managed, process);
    }

    private void CompleteSave(ServerCommandEnvelope command, bool success = true) =>
        registry.UpdateCommandResult(new ServerCommandResult
        {
            AgentId = command.AgentId, CommandId = command.CommandId, UniqueName = "test", ServerId = "test",
            Success = success, Message = success ? "World saved." : "Save failed.",
        }, "connection");

    [Theory]
    [InlineData(WorldBackupAction.SaveAndBackUp)]
    [InlineData(WorldBackupAction.StopAndBackUp)]
    public async Task FreshBackupWaitsForSaveAndDoesNotUseAnOlderSnapshot(WorldBackupAction action)
    {
        var world = await CreateWorld(running: true);
        var old = Path.Combine(world, "Backup", "old");
        Directory.CreateDirectory(old);
        await File.WriteAllTextAsync(Path.Combine(old, "Sandbox.sbc"), "old save");
        File.SetLastWriteTimeUtc(Path.Combine(old, "Sandbox.sbc"), DateTime.UtcNow.AddDays(-1));
        await File.WriteAllTextAsync(Path.Combine(world, "Sandbox_config.sbc"), "keep config");
        Directory.CreateDirectory(Path.Combine(world, ".new"));
        await File.WriteAllTextAsync(Path.Combine(world, ".new", "partial.sbc"), "partial");

        var backup = backups.WriteWorldBackupFileAsync("test", DateTimeOffset.Now, action: action);
        var save = await commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ServerCommandType.SaveWorld, save.CommandType);
        Assert.False(backup.IsCompleted);
        Assert.False(Directory.Exists(options.BackupDirectory));
        Assert.False(operations.TryBeginRestore("test", out _));
        await File.WriteAllTextAsync(Path.Combine(world, "Sandbox.sbc"), "fresh save");
        CompleteSave(save);

        var path = await backup.WaitAsync(TimeSpan.FromSeconds(5));
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("world/Sandbox.sbc")!.Open());
        Assert.Equal("fresh save", await reader.ReadToEndAsync());
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.StartsWith("world/Backup/") || entry.FullName.StartsWith("world/.new/"));
        Assert.Null(zip.GetEntry("world/Sandbox_config.sbc"));
        Assert.False(operations.IsFileOperationInProgress("test"));
        Assert.Equal(action == WorldBackupAction.StopAndBackUp ? DedicatedServerGoalState.Off : DedicatedServerGoalState.On,
            servers.GetServer("test")!.GoalState);
        if (action == WorldBackupAction.StopAndBackUp)
            Assert.False(servers.GetServer("test")!.AutoStart);
    }

    [Theory]
    [InlineData(WorldBackupAction.SaveAndBackUp)]
    [InlineData(WorldBackupAction.StopAndBackUp)]
    public async Task FailedSaveDoesNotCreateBackupOrStopServer(WorldBackupAction action)
    {
        await CreateWorld(running: true);
        var backup = backups.WriteWorldBackupFileAsync("test", DateTimeOffset.Now, action: action);
        CompleteSave(await commands.Reader.ReadAsync(), success: false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => backup);
        Assert.Contains("Save failed", error.Message);
        Assert.False(Directory.Exists(options.BackupDirectory));
        Assert.False(operations.IsFileOperationInProgress("test"));
        Assert.Equal(DedicatedServerGoalState.On, servers.GetServer("test")!.GoalState);
    }

    [Fact]
    public async Task CancelledSaveReleasesReservationWithoutCreatingBackup()
    {
        await CreateWorld(running: true);
        using var cancel = new CancellationTokenSource();
        var backup = backups.WriteWorldBackupFileAsync("test", DateTimeOffset.Now,
            cancellationToken: cancel.Token, action: WorldBackupAction.SaveAndBackUp);
        await commands.Reader.ReadAsync();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backup);
        Assert.False(operations.IsFileOperationInProgress("test"));
        Assert.False(Directory.Exists(options.BackupDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopBackupWaitsForProcessExitAndCancellationDoesNotPublishAZip(bool cancelStop)
    {
        await CreateWorld(running: true);
        using var process = Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul")
            : new ProcessStartInfo("sleep", "60"))!;
        SetProcessState(DedicatedServerProcessState.Running, process);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var backup = backups.WriteWorldBackupFileAsync("test", DateTimeOffset.Now,
                cancellationToken: cancellation.Token, action: WorldBackupAction.StopAndBackUp);
            CompleteSave(await commands.Reader.ReadAsync());
            // The existing graceful-stop path saves once more before sending StopServer.
            CompleteSave(await commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            var stop = await commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ServerCommandType.StopServer, stop.CommandType);
            Assert.False(backup.IsCompleted);
            Assert.False(Directory.Exists(options.BackupDirectory));
            Assert.Equal(DedicatedServerGoalState.Off, servers.GetServer("test")!.GoalState);
            Assert.False(servers.GetServer("test")!.AutoStart);
            await (Task)typeof(DedicatedServerSupervisor).GetMethod("ReconcileAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(supervisor, [CancellationToken.None])!;
            Assert.False(commands.Reader.TryRead(out _));
            if (cancelStop)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backup);
                Assert.False(Directory.Exists(options.BackupDirectory));
                Assert.False(process.HasExited);
            }
            else
            {
                process.Kill(entireProcessTree: true);
                Assert.True(File.Exists(await backup.WaitAsync(TimeSpan.FromSeconds(5))));
                Assert.Equal(DedicatedServerProcessState.Stopped, Assert.Single(supervisor.GetSnapshots()).State);
            }
            Assert.False(operations.IsFileOperationInProgress("test"));
        }
        finally
        {
            // The successful stop disposes the supervisor's process handle.
            if (DedicatedServerSupervisor.IsProcessActive(process))
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task WorldBackupReservationBlocksStartsAndRestores()
    {
        await CreateWorld();
        Assert.True(operations.TryBeginBackup("test", out var reservation));
        using (reservation)
        {
            Assert.False(operations.TryBeginRestore("test", out _));
            await supervisor.StartServerAsync("test");
            Assert.Contains("world backup is in progress", Assert.Single(supervisor.GetSnapshots()).LastMessage);
        }
        Assert.True(operations.TryBeginRestore("test", out var restore));
        using (restore)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => backups.WriteWorldBackupFileAsync("test", DateTimeOffset.Now));
        }
    }

    [Fact]
    public async Task DisconnectedAgentFailsFreshBackupWithoutCreatingZip()
    {
        await CreateWorld(running: true);
        registry.MarkDisconnected("connection");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => backups.WriteWorldBackupFileAsync(
            "test", DateTimeOffset.Now, action: WorldBackupAction.SaveAndBackUp));
        Assert.Contains("No connected agent", error.Message);
        Assert.False(Directory.Exists(options.BackupDirectory));
        Assert.False(operations.IsFileOperationInProgress("test"));
    }

    [Fact]
    public async Task QueueExecutesTheSelectedSaveActionBeforePublishingSuccess()
    {
        await CreateWorld(running: true);
        var completed = new TaskCompletionSource<QueuedBackupJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.QueuedBackupCompleted += result => completed.TrySetResult(result);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            var id = queue.QueueWorldBackup("test", WorldBackupAction.SaveAndBackUp);
            var save = await commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(completed.Task.IsCompleted);
            CompleteSave(save);
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(id, result.Id);
            Assert.True(result.Success);
            Assert.Equal(1, result.CreatedCount);
            Assert.Single(backups.ListBackups());
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(DedicatedServerProcessState.Running, "Save and back up", WorldBackupAction.SaveAndBackUp)]
    [InlineData(DedicatedServerProcessState.Running, "Stop and back up", WorldBackupAction.StopAndBackUp)]
    [InlineData(DedicatedServerProcessState.Running, "Cancel", null)]
    [InlineData(DedicatedServerProcessState.Stopped, null, WorldBackupAction.LatestSave)]
    [InlineData(DedicatedServerProcessState.Starting, null, null)]
    [InlineData(DedicatedServerProcessState.Stopping, null, null)]
    [InlineData(DedicatedServerProcessState.Restarting, null, null)]
    public async Task ManualActionShowsChoicesOnlyWhenRunningAndQueuesChosenAction(
        DedicatedServerProcessState state, string? choice, WorldBackupAction? expected)
    {
        await CreateWorld(running: state != DedicatedServerProcessState.Stopped);
        SetProcessState(state);
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var provider = context.Render<MudDialogProvider>();
        // Exercise the real page handler without rendering unrelated backup schedule controls.
        var page = new Backup();
        var operations = new ServiceCollection().AddSingleton(servers).AddSingleton(supervisor).AddSingleton(queue)
            .AddDirectCoreOperations().BuildServiceProvider();
        foreach (var service in new object[] { supervisor, queue, context.Services.GetRequiredService<IDialogService>(), context.Services.GetRequiredService<ISnackbar>(), operations })
        {
            var property = typeof(Backup).GetProperties(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(p => p.PropertyType.IsInstanceOfType(service));
            property.SetValue(page, service);
        }
        var action = provider.InvokeAsync(() => (Task)typeof(Backup).GetMethod("MakeWorldBackupNowAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page, ["test"])!);
        if (state == DedicatedServerProcessState.Running)
        {
            await provider.WaitForAssertionAsync(() => Assert.Contains("Back up world now?", provider.Markup));
            var labels = provider.FindAll("button").Select(b => b.TextContent.Trim()).ToArray();
            Assert.Equal(new[] { "Cancel", "Stop and back up", "Save and back up" }, labels);
            await provider.FindAll("button").Single(b => b.TextContent.Trim() == choice).ClickAsync();
        }
        await action.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = typeof(AutomaticBackupService).GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(queue)!;
        var reader = pending.GetType().GetProperty("Reader")!.GetValue(pending)!;
        var arguments = new object?[] { null };
        Assert.Equal(expected.HasValue, (bool)reader.GetType().GetMethod("TryRead")!.Invoke(reader, arguments)!);
        if (expected.HasValue)
            Assert.Equal(expected.Value, arguments[0]!.GetType().GetProperty("WorldAction")!.GetValue(arguments[0]));
        Assert.Empty(provider.FindAll(".mud-dialog"));
        Assert.False(commands.Reader.TryRead(out _));
    }

    public void Dispose()
    {
        queue.Dispose();
        settings.Dispose();
        supervisor.Dispose();
        servers.Dispose();
        cache.SetValue(null, previousRoot);
        Directory.Delete(root, recursive: true);
    }
}
