using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using CometWorks.ClusterGateway.AdminContract.V1;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Quasar.Components.Pages;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.PluginSdk;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterConsoleDialogTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsoleEnforcesClusterScopeAndKeepsEventsDuringOutage(bool excluded)
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = new PrincipalState(excluded);
        var mapper = new QuasarRoleMapper(new QuasarAuthOptions(), null!);
        context.Services.AddSingleton<AuthenticationStateProvider>(auth);
        context.Services.AddSingleton(mapper);
        context.Services.AddSingleton(new QuasarPermissionService(auth, new QueryPermission(), mapper));
        using var handler = new GatewayHandler();
        using var http = new HttpClient(handler);
        var gateway = new ClusterGatewayClient(http);
        var logs = new PluginLogStream();
        context.Services.AddSingleton(gateway);
        context.Services.AddSingleton(logs);
        context.Services.AddSingleton(new ClusterFleetService(gateway, new AgentRegistry(null!, null!, null!, null!), logs));
        var dialog = context.Render<MudDialogProvider>();
        await dialog.InvokeAsync(async () => await context.Services.GetRequiredService<IDialogService>()
            .ShowAsync<ClusterConsoleDialog>("Console", new DialogParameters<ClusterConsoleDialog>
            { { c => c.Cluster, new ClusterDefinition { UniqueName = "test", GatewayUrl = "http://gateway.test" } } }));
        if (excluded)
        {
            Assert.Equal(0, handler.Calls);
            Assert.Contains("Cluster access denied", dialog.Markup);
            return;
        }
        Assert.Contains("world-saved", dialog.Markup);
        Assert.DoesNotContain("<script>", dialog.Markup);
        handler.Unavailable = true;
        await dialog.WaitForAssertionAsync(() => Assert.Contains("Retained entries may be stale", dialog.Markup), TimeSpan.FromSeconds(10));
        Assert.Contains("world-saved", dialog.Markup);
    }

    private sealed class PrincipalState(bool excluded) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal),
                new Claim(QuasarClaimTypes.Cluster, excluded ? "another-cluster" : "test"),
            ], "test"))));
    }
    private sealed class QueryPermission : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Success());
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(policyName == QuasarPolicyNames.ClusterQuery ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }
    private sealed class GatewayHandler : HttpMessageHandler
    {
        internal int Calls;
        internal volatile bool Unavailable;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (Unavailable) throw new HttpRequestException("offline");
            object data = request.RequestUri!.AbsolutePath.EndsWith("/events")
                ? new AdminEventPage([new(1, DateTimeOffset.UtcNow, "world-saved", "<script>", null, "test", null)], 1, 1)
                : JsonSerializer.Deserialize<ClusterStatus>("""{"clusterId":"test","nodes":[]} """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AdminEnvelope<object>(1, DateTimeOffset.UtcNow, data)) };
            response.Headers.Add("X-Cluster-Gateway-Protocol", "1");
            return Task.FromResult(response);
        }
    }
}
