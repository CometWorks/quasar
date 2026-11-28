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
using Quasar.Client.Components;
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
        using var catalog = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        var dialogs = context.Services.GetRequiredService<IDialogService>();
        var references = new List<IDialogReference>();
        dialogs.DialogInstanceAddedAsync += reference => { references.Add(reference); return Task.CompletedTask; };
        var provider = context.Render<MudDialogProvider>();
        await provider.InvokeAsync(() => dialogs.ShowAsync<NewServerDialog>("New server", new DialogParameters<NewServerDialog>
        {
            { p => p.CanCreate, true }, { p => p.CanManageCluster, true }, { p => p.WorkerLinux, OperatingSystem.IsLinux() }
        }));
        bool unavailableCluster = choice == "Cluster" && !OperatingSystem.IsLinux();
        var selected = provider.FindAll("button").Single(b => b.TextContent.Trim() == choice);
        Assert.Equal(unavailableCluster, selected.HasAttribute("disabled"));
        if (unavailableCluster)
        {
            var clusterButton = provider.FindComponents<MudButton>().Single(b => b.Find("button").TextContent.Trim() == "Cluster");
            await provider.InvokeAsync(() => clusterButton.Instance.OnClick.InvokeAsync(new()));
            Assert.Single(provider.FindComponents<NewServerDialog>());
            await provider.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").ClickAsync();
        }
        else await selected.ClickAsync();

        var result = await Assert.Single(references).Result;
        if (unavailableCluster || choice == "Cancel")
            Assert.True(result!.Canceled);
        else
            Assert.Equal(choice == "Cluster" ? NewServerDialog.ServerKind.Cluster : NewServerDialog.ServerKind.SingleMagnetar, result!.Data);
        Assert.Empty(catalog.GetServers());
    }

    [Theory]
    [InlineData(QuasarPolicyNames.CanEditServers, "Single Magnetar", "Cluster")]
    [InlineData(QuasarPolicyNames.ClusterManage, "Cluster", "Single Magnetar")]
    public async Task ChoicesRespectTheirOwnPermissions(string policy, string visible, string hidden)
    {
        await using var context = CreateContext(policy);
        var provider = context.Render<MudDialogProvider>();
        await provider.InvokeAsync(() => context.Services.GetRequiredService<IDialogService>().ShowAsync<NewServerDialog>("New server", new DialogParameters<NewServerDialog> {
            { p => p.CanCreate, policy == QuasarPolicyNames.CanEditServers },
            { p => p.CanManageCluster, policy == QuasarPolicyNames.ClusterManage },
            { p => p.WorkerLinux, OperatingSystem.IsLinux() }
        }));
        var choices = provider.FindAll("button").Select(b => b.TextContent.Trim()).ToArray();
        Assert.Contains(visible, choices);
        Assert.DoesNotContain(hidden, choices);
        if (visible == "Cluster")
            Assert.Equal(!OperatingSystem.IsLinux(), provider.FindAll("button").Single(b => b.TextContent.Trim() == "Cluster").HasAttribute("disabled"));
    }

    private static BunitContext CreateContext(params string[] policies)
    {
        var context = new QuasarBunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization();
        auth.SetAuthorized("operator");
        auth.SetPolicies(policies.Length == 0 ? [QuasarPolicyNames.CanEditServers, QuasarPolicyNames.ClusterManage] : policies);
        context.Services.AddSingleton(new QuasarRoleMapper(new QuasarAuthOptions(), null!));
        return context;
    }

    public void Dispose()
    {
        _cache.SetValue(null, _previousRoot);
        Directory.Delete(_root, true);
    }
}
