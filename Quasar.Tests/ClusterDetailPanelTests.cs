using System.Net;
using System.Net.Http.Json;
using Bunit;
using CometWorks.ClusterGateway.AdminContract.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Quasar.Components.Dashboard;
using Quasar.Models;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterDetailPanelTests
{
    [Fact]
    public async Task PollUpdatesVisibleStatusWithoutParentRender()
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        using var handler = new ChangingGateway();
        using var http = new HttpClient(handler);
        var gateway = new ClusterGatewayClient(http);
        var host = new ClusterHostClient(http);
        context.Services.AddSingleton(gateway);
        context.Services.AddSingleton(host);
        context.Services.AddSingleton(new ClusterReconciler(null!, gateway, host, null!, NullLogger<ClusterReconciler>.Instance));
        context.Services.AddSingleton(new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance));
        context.Services.AddSingleton(new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance));
        var panel = context.Render<ClusterDetailPanel>(parameters => parameters
            .Add(p => p.Cluster, new ClusterDefinition { UniqueName = "test", GatewayUrl = "http://gateway.test" })
            .Add(p => p.ShowConfigurationLinks, false));
        Assert.Contains("First observation", panel.Markup);
        handler.Message = "Later observation";
        await panel.WaitForAssertionAsync(() => Assert.Contains("Later observation", panel.Markup), TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("First observation", panel.Markup);
    }

    private sealed class ChangingGateway : HttpMessageHandler
    {
        internal volatile string Message = "First observation";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = JsonContent.Create(new AdminErrorEnvelope(1, DateTimeOffset.UtcNow, new AdminError("not_ready", Message))),
            };
            response.Headers.Add("X-Cluster-Gateway-Protocol", "1");
            return Task.FromResult(response);
        }
    }
}
