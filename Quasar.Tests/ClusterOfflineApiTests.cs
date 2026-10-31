using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterOfflineApiTests
{
    [Theory]
    [InlineData(QuasarRoles.Viewer, false)]
    [InlineData(QuasarRoles.Editor, false)]
    [InlineData(QuasarRoles.Admin, true)]
    public async Task ApiEnforcesAdministratorForProfileCacheAndDeliberatePrediction(string role, bool allowed)
    {
        using var fixture = new OfflineGatewayFixture();
        await using var app = App(fixture, role);
        await app.StartAsync(); // In-memory TestServer only. No ports or Quasar worker process.
        using var http = app.GetTestClient();
        foreach (var command in new[]
        {
            ClusterAdminCommand.Create("trigger", new ClusterMaintenanceScope("node-1", 7), "profile"),
            ClusterAdminCommand.Create("offline-cache-clear", new CometWorks.ClusterGateway.AdminContract.V1.OfflineCacheClearRequest("rate/key?x=1", 4)),
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/clusters/demo/commands") { Content = JsonContent.Create(command, options: OfflineGatewayFixture.Json) };
            request.Headers.Add("Idempotency-Key", command.Action);
            using var response = await http.SendAsync(request);
            Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        }
        using var predict = await http.PostAsJsonAsync("/api/v1/clusters/demo/offline/prediction", new ClusterOfflinePredictionRequest("holder-1", 17, 1));
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, predict.StatusCode);
        if (!allowed) Assert.Empty(fixture.Requests);
        int mutations = fixture.Requests.Count(r => r.Method != "GET");
        using var details = await http.GetAsync("/api/v1/clusters/demo/offline/details?node=holder-1&partition=17");
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        Assert.Equal(mutations, fixture.Requests.Count(r => r.Method != "GET"));
    }

    [Fact]
    public async Task ApiPreservesUnsupportedAndTargetConflictErrorsAndValidatesPrediction()
    {
        using var fixture = new OfflineGatewayFixture { Supported = false };
        await using var app = App(fixture, QuasarRoles.Admin);
        await app.StartAsync();
        using var http = app.GetTestClient();
        using var unsupported = await http.GetAsync("/api/v1/clusters/demo/offline/cache");
        Assert.Equal(HttpStatusCode.NotImplemented, unsupported.StatusCode);
        Assert.Contains("offline_unsupported", await unsupported.Content.ReadAsStringAsync());
        using var invalid = await http.PostAsJsonAsync("/api/v1/clusters/demo/offline/prediction", new ClusterOfflinePredictionRequest("holder-1", 17, 169));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.DoesNotContain(fixture.Requests, r => r.Uri.AbsolutePath.EndsWith("/prediction"));
    }

    private static WebApplication App(OfflineGatewayFixture fixture, string role)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuth>("fixture", _ => { });
        builder.Services.AddAuthorization(Program.ConfigureAuthorizationPolicies);
        builder.Services.AddSingleton(fixture.Catalog);
        builder.Services.AddSingleton(fixture.Gateway);
        builder.Services.AddSingleton(fixture.Commands);
        builder.Services.AddSingleton(fixture.Store);
        builder.Services.AddSingleton<ClusterHostClient>(_ => null!);
        builder.Services.AddSingleton<ClusterReconciler>(_ => null!);
        builder.Services.AddSingleton<ClusterUpdatePreparationService>(_ => null!);
        var app = builder.Build();
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            context.User = new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "fixture"));
            await next(context);
        });
        app.UseAuthorization();
        app.MapClusterApi(new QuasarAuthOptions { Enabled = true });
        return app;
    }

    private sealed class FixtureAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
