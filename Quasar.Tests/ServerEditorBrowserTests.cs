using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using Quasar.Client.Components;
using Quasar.Client.Fleet;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Backup;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class ServerEditorBrowserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "server-editor-browser-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previous;
    public ServerEditorBrowserTests() { _previous = _cache.GetValue(null); _cache.SetValue(null, _root); }

    [Fact]
    public async Task StandaloneServerNamesAreSuggestedAndExplicitIdsStayFixed()
    {
        await using var context = await ContextAsync();
        context.Render<MudPopoverProvider>();
        var provider = context.Render<MudDialogProvider>();
        var dialogs = context.Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => dialogs.ShowAsync<FleetServerEditor>("Create Server"));
        var page = provider.FindComponent<FleetServerEditor>();
        var name = page.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Server name");
        var id = page.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Server ID");
        Assert.Matches("^server-[a-f0-9]{12}$", name.Instance.GetState(c => c.Value));
        Assert.Equal(name.Instance.GetState(c => c.Value), id.Instance.GetState(c => c.Value));
        await page.InvokeAsync(() => id.Instance.ValueChanged.InvokeAsync("chosen-server"));
        await page.InvokeAsync(() => name.Instance.ValueChanged.InvokeAsync(""));
        await page.InvokeAsync(() => name.Instance.OnBlur.InvokeAsync(new()));
        Assert.Matches("^server-[a-f0-9]{12}$", name.Instance.GetState(c => c.Value));
        Assert.Equal("chosen-server", id.Instance.GetState(c => c.Value));
        await provider.InvokeAsync(() => dialogs.ShowAsync<FleetServerEditor>("Edit Server", new DialogParameters
        {
            [nameof(FleetServerEditor.UniqueName)] = "source",
        }));
        var edit = provider.FindComponents<FleetServerEditor>().Single(c => c.Instance.UniqueName == "source");
        Assert.Equal("Original server", edit.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Server name").Instance.GetState(c => c.Value));
        Assert.Equal("source", edit.FindComponents<MudTextField<string>>().Single(c => c.Instance.Label == "Server ID").Instance.GetState(c => c.Value));
    }

    [Fact]
    public async Task BrowserEditorLoadsHostSettingsAndCurrentSave()
    {
        await using var context = await ContextAsync();
        context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        await dialogs.InvokeAsync(() => context.Services.GetRequiredService<IDialogService>().ShowAsync<FleetServerEditor>("Edit server",
            new DialogParameters<FleetServerEditor> { { p => p.UniqueName, "source" } }));
        await dialogs.WaitForAssertionAsync(() => Assert.Contains("Edit Server", dialogs.Markup));
        Assert.Contains("Main", dialogs.Markup);
    }

    [Fact]
    public async Task EditChecksRevisionAndKeepsUndoAndGoal()
    {
        await using var context = await ContextAsync();
        var catalog = context.Services.GetRequiredService<DedicatedServerCatalog>();
        var server = catalog.GetServer("source")!;
        server.DisplayName = "After edit";
        var commit = new ServerEditorCommit(new("source"), server.UpdatedAtUtc, server, false, null);
        var undo = await Save(context, "fleet.editor.update", commit);
        Assert.Equal("source", undo.Server);
        Assert.True(undo.HasBlobs);
        var saved = catalog.GetServer("source")!;
        Assert.Equal("After edit", saved.DisplayName);
        Assert.Equal(server.GoalState, saved.GoalState);
        var stale = await Assert.ThrowsAsync<QuasarOperationException>(() => Save(context, "fleet.editor.update", commit));
        Assert.Contains("changed", stale.Message);
    }

    [Fact]
    public async Task RenameKeepsExistingManagedWorldAndNewServerRefusesReusingThePort()
    {
        await using var context = await ContextAsync();
        var catalog = context.Services.GetRequiredService<DedicatedServerCatalog>();
        var source = catalog.GetServer("source")!;
        var moved = source.Clone();
        moved.UniqueName = "renamed";
        var rename = new ServerEditorCommit(new("source"), source.UpdatedAtUtc, moved, false, null);
        await Save(context, "fleet.editor.save", rename);
        Assert.Null(catalog.GetServer("source"));
        Assert.Equal("original", File.ReadAllText(Path.Combine(catalog.GetServer("renamed")!.GetWorldSavePath(), "marker")));

        var fresh = moved.Clone();
        fresh.UniqueName = "fresh";
        string world = fresh.GetWorldSavePath();
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        var create = new ServerEditorCommit(new(), null, fresh, false, null);
        var collision = await Assert.ThrowsAsync<QuasarOperationException>(() => Save(context, "fleet.editor.save", create));
        Assert.Contains("port", collision.Message);
        Assert.Null(catalog.GetServer("fresh"));
        fresh.ServerPort++;
        await Save(context, "fleet.editor.save", create);
        Assert.Equal("Main", catalog.GetServer("fresh")!.WorldSaveName);
    }

    [Fact]
    public async Task CloneCopiesFromSourceAndRequiresExplicitExistingSaveConfirmation()
    {
        await using var context = await ContextAsync();
        var catalog = context.Services.GetRequiredService<DedicatedServerCatalog>();
        var source = catalog.GetServer("source")!;
        var draft = source.Clone();
        draft.UniqueName = "copy"; draft.ServerPort += 1;
        string destination = draft.GetWorldSavePath();
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        File.WriteAllText(Path.Combine(destination, "prior"), "preserve");
        var commit = new ServerEditorCommit(new("source", true), source.UpdatedAtUtc, draft, true, null);
        await Assert.ThrowsAsync<QuasarOperationException>(() => Save(context, "fleet.editor.save", commit));
        Assert.True(File.Exists(Path.Combine(destination, "prior")));
        Assert.Null(catalog.GetServer("copy"));
        await Save(context, "fleet.editor.save", commit with { ReplaceWorld = destination });
        Assert.Equal("copy", catalog.GetServer("copy")!.UniqueName);
        Assert.Equal("original", File.ReadAllText(Path.Combine(destination, "marker")));
        Assert.True(File.Exists(Path.Combine(source.GetWorldSavePath(), "marker")));
        Assert.False(File.Exists(Path.Combine(destination, "prior")));
    }

    private async Task<OperationsBrowserContext> ContextAsync()
    {
        var context = new OperationsBrowserContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        var auth = context.AddAuthorization();
        auth.SetAuthorized("editor"); auth.SetPolicies(QuasarPolicyNames.CanEditServers, QuasarPolicyNames.CanEditConfigs);
        var maintenance = new ServerRestoreCoordinator();
        var catalog = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance, maintenance);
        var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        var supervisor = new DedicatedServerSupervisor(catalog, new AgentRegistry(null!, null!, null!, null!), null!, null!, null!, new(),
            maintenance, NullLogger<DedicatedServerSupervisor>.Instance);
        context.Services.AddSingleton(maintenance).AddSingleton(catalog).AddSingleton(profiles).AddSingleton(supervisor)
            .AddSingleton(new WebServiceOptions()).AddSingleton(new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance));
        await profiles.UpsertAsync(new() { ConfigProfileId = "profile", Name = "Current profile" });
        await catalog.UpsertAsync(new() { UniqueName = "source", DisplayName = "Original server", WorldSaveName = "Main", ConfigProfileId = "profile" });
        var world = catalog.GetServer("source")!.GetWorldSavePath();
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        File.WriteAllText(Path.Combine(world, "marker"), "original");
        return context;
    }
    private async Task<QuasarUndoRecorder> Save(OperationsBrowserContext context, string id, ServerEditorCommit commit)
    {
        var operation = context.Services.GetServices<QuasarOperation>().Single(o => o.OperationId == id);
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(commit));
        var undo = new QuasarUndoRecorder(Path.Combine(_root, "undo", Guid.NewGuid().ToString("N")));
        var caller = new QuasarOperationContext(new ClaimsPrincipal(new ClaimsIdentity("editor")), context.Services)
        { Undo = undo, Upload = stream, VerifyAccess = _ => Task.CompletedTask };
        await operation.InvokeAsync(caller, JsonSerializer.SerializeToElement(new { }), default);
        return undo;
    }
    public void Dispose() { _cache.SetValue(null, _previous); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
