using System.Reflection;
using Bunit;
using Bunit.TestDoubles;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Components.Pages;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class NewServerDialogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quasar-new-server-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previousRoot;

    public NewServerDialogTests()
    {
        _previousRoot = _cache.GetValue(null);
        _cache.SetValue(null, _root);
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData("Single Magnetar")]
    [InlineData("Cluster")]
    [InlineData("Cancel")]
    public async Task NewServerRoutesToTheSelectedFlow(string choice)
    {
        await using var context = CreateContext();
        context.ComponentFactories.AddStub<ServerEditorDialog>();
        using var catalog = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        var dialogs = context.Services.GetRequiredService<IDialogService>();
        var references = new List<IDialogReference>();
        dialogs.DialogInstanceAddedAsync += reference => { references.Add(reference); return Task.CompletedTask; };
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var actions = new ServerManagementActions(catalog, null!, new(), null!, null!,
            context.Services.GetRequiredService<QuasarPermissionService>(), dialogs,
            context.Services.GetRequiredService<ISnackbar>(), navigation);
        var provider = context.Render<MudDialogProvider>();
        var action = provider.InvokeAsync(() => actions.OpenNewServerDialogAsync());
        await provider.WaitForAssertionAsync(() => Assert.Single(provider.FindComponents<NewServerDialog>()));
        await provider.FindAll("button").Single(b => b.TextContent.Trim() == choice).ClickAsync();

        if (choice == "Single Magnetar")
        {
            await provider.WaitForAssertionAsync(() => Assert.Single(provider.FindComponents<Stub<ServerEditorDialog>>()));
            Assert.Equal(2, references.Count);
            await provider.InvokeAsync(() => dialogs.Close(references[^1], DialogResult.Cancel()));
        }

        await action;
        Assert.Equal(choice == "Cluster" ? "http://localhost/clusters/new" : "http://localhost/", navigation.Uri);
        Assert.Equal(choice == "Single Magnetar" ? 2 : 1, references.Count);
        Assert.Empty(catalog.GetServers());
    }

    [Theory]
    [InlineData(QuasarPolicyNames.CanEditServers, "Single Magnetar", "Cluster")]
    [InlineData(QuasarPolicyNames.ClusterManage, "Cluster", "Single Magnetar")]
    public async Task ChoicesRespectTheirOwnPermissions(string policy, string visible, string hidden)
    {
        await using var context = CreateContext(policy);
        var provider = context.Render<MudDialogProvider>();
        await provider.InvokeAsync(() => context.Services.GetRequiredService<IDialogService>().ShowAsync<NewServerDialog>("New server"));
        var choices = provider.FindAll("button").Select(b => b.TextContent.Trim()).ToArray();
        Assert.Contains(visible, choices);
        Assert.DoesNotContain(hidden, choices);
    }

    private static BunitContext CreateContext(params string[] policies)
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("operator");
        auth.SetPolicies(policies.Length == 0 ? [QuasarPolicyNames.CanEditServers, QuasarPolicyNames.ClusterManage] : policies);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        context.Services.AddScoped<QuasarPermissionService>();
        return context;
    }

    public void Dispose()
    {
        _cache.SetValue(null, _previousRoot);
        Directory.Delete(_root, true);
    }
}
