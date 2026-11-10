extern alias Bootstrap;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Quasar.Hosting;
using Quasar.Services;
using Quasar.Services.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Connector = Bootstrap::Quasar.Bootstrap.HostedPanelConnector;
using ConnectorState = Bootstrap::Quasar.Hosting.HostedPanelState;

namespace Quasar.Tests;

public sealed partial class HostedPanelTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task InactiveEnrollmentRevokesHostedAccessAndCanReactivateWithoutRelinking(bool? completed)
    {
        var state = State with { EnrollmentId = "bound-enrollment", OwnerSteamId = "76561198000000001",
            BrowserUrl = HostedPanelState.Origin + "/account", SetupCompleted = completed };
        await HostedPanelState.SaveAsync(state);
        await new HostedConnectorHealth(state.PanelId, true, true, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).SaveAsync(default);
        using var rbac = new RbacConfigCatalog(NullLogger<RbacConfigCatalog>.Instance);
        using var replies = new AccessSnapshotHandler(state);
        using var service = new HostedPanelService(replies, NullLogger<HostedPanelService>.Instance, rbac);
        var changes = 0;
        service.Changed += () => changes++;
        await service.RefreshAsync();
        Assert.True(service.IsReady);

        replies.Active = false;
        replies.Administration = HttpStatusCode.ServiceUnavailable;
        await service.RefreshAsync(); // Advisory administration failure cannot hide known revocation.
        var inactive = HostedPanelState.Read()!;
        Assert.Null(inactive.Credential);
        Assert.Equal(state with { Credential = null, SetupCompleted = true }, inactive);
        Assert.False(service.AccessActive);
        Assert.False(service.IsReady);
        Assert.False(HostedConnectorHealth.Read()!.Ready);
        Assert.Contains("Hosted access inactive", service.Status);
        Assert.True(changes >= 2);
        var forwarded = Request(state);
        await HostedPanelRequest.InvokeAsync(forwarded, _ => throw new Exception("Revoked hosted request passed"), new());
        Assert.Equal(403, forwarded.Response.StatusCode);
        var localReached = false;
        await HostedPanelRequest.InvokeAsync(new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            _ => { localReached = true; return Task.CompletedTask; }, new());
        Assert.True(localReached);

        replies.Active = true;
        await service.RefreshAsync();
        Assert.Equal(state with { SetupCompleted = true }, HostedPanelState.Read());
        Assert.True(service.AccessActive);
        Assert.False(service.IsReady); // Payment alone cannot revive old tunnel health.
        await using var connector = new Connector(8080, NullLogger.Instance);
        using var local = HealthyConnectorClient();
        using var remote = new HttpClient(new ReplyHandler(_ => new(HttpStatusCode.NoContent)));
        Assert.True(await connector.ReportHealthAsync(ConnectorState.Read()!, local, remote, default));
        await service.RefreshAsync();
        Assert.True(service.IsReady);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    public async Task AuthoritativeEnrollmentDenialClearsCredentialAndReadiness(HttpStatusCode status, bool retainEnrollment)
    {
        var state = State with { EnrollmentId = "bound-enrollment", BrowserUrl = HostedPanelState.Origin + "/account" };
        await HostedPanelState.SaveAsync(state);
        await new HostedConnectorHealth(state.PanelId, true, true, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).SaveAsync(default);
        using var replies = new AccessSnapshotHandler(state) { Status = status };
        using var service = new HostedPanelService(replies, NullLogger<HostedPanelService>.Instance);
        await service.RefreshAsync();
        var current = HostedPanelState.Read()!;
        Assert.Null(current.Credential);
        Assert.Equal(retainEnrollment ? state.EnrollmentId : null, current.EnrollmentId);
        Assert.Equal(state.InstallationSecret, current.InstallationSecret);
        Assert.True(current.IsSetupComplete);
        Assert.False(service.AccessActive);
        Assert.False(HostedConnectorHealth.Read()!.Ready);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task UnavailableEnrollmentDoesNotDestroyReactivationProof(HttpStatusCode status)
    {
        var state = State with { EnrollmentId = "bound-enrollment" };
        await HostedPanelState.SaveAsync(state);
        using var replies = new AccessSnapshotHandler(state) { Status = status };
        using var service = new HostedPanelService(replies, NullLogger<HostedPanelService>.Instance);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.RefreshAsync());
        Assert.Equal(state, HostedPanelState.Read());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task HeartbeatDenialStopsOwnedProcessAndBlocksRestartBeforeAnyDownload(HttpStatusCode status)
    {
        await HostedPanelState.SaveAsync(State);
        await using var connector = new Connector(8080, NullLogger.Instance);
        var owned = Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul") { CreateNoWindow = true }
            : new ProcessStartInfo("sleep", "60"))!;
        var pid = owned.Id;
        typeof(Connector).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connector, owned);
        using var local = HealthyConnectorClient();
        var calls = 0;
        using var remote = new HttpClient(new ReplyHandler(request =>
        {
            Assert.Equal(HostedPanelState.Origin + "/api/v1/connector/heartbeat", request.RequestUri!.AbsoluteUri);
            Assert.Equal(State.PanelId, ConnectorState.Read()!.PanelId);
            Assert.Equal(ConnectorState.Read()!.Credential, request.Headers.Authorization!.Parameter);
            calls++;
            return new(status);
        }));
        await connector.RefreshAsync(local, remote, default);
        await connector.RefreshAsync(local, remote, default);
        Assert.Equal(2, calls);
        Assert.Null(typeof(Connector).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connector));
        try { using var exited = Process.GetProcessById(pid); Assert.True(exited.HasExited); } catch (ArgumentException) { }
        Assert.False(HostedConnectorHealth.Read()!.Ready);
        Assert.NotNull(HostedPanelState.Read()!.Credential); // Bootstrap never writes worker enrollment.
        Assert.False(File.Exists(Path.Combine(HostedPanelState.DirectoryPath, "frpc.toml")));
        Assert.False(Directory.Exists(Path.Combine(HostedPanelState.DirectoryPath, "frp-0.71.1")));
        using var allowed = new HttpClient(new ReplyHandler(_ => new(HttpStatusCode.NoContent)));
        Assert.True(await connector.ReportHealthAsync(ConnectorState.Read()!, local, allowed, default));
    }

    [Fact]
    public async Task TemporaryHeartbeatFailureDoesNotKillAnExistingConnectorOrClearProof()
    {
        var state = State;
        await HostedPanelState.SaveAsync(state);
        await using var connector = new Connector(8080, NullLogger.Instance);
        var owned = Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul") { CreateNoWindow = true }
            : new ProcessStartInfo("sleep", "60"))!;
        typeof(Connector).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connector, owned);
        using var local = HealthyConnectorClient();
        using var remote = new HttpClient(new ReplyHandler(_ => new(HttpStatusCode.ServiceUnavailable)));
        await Assert.ThrowsAsync<HttpRequestException>(() => connector.RefreshAsync(local, remote, default));
        Assert.False(owned.HasExited);
        Assert.Equal(state, HostedPanelState.Read());
    }

    [Fact]
    public async Task LatePositiveHeartbeatCannotRestoreRevokedCredentialOrReadiness()
    {
        await HostedPanelState.SaveAsync(State);
        var original = ConnectorState.Read()!;
        await using var connector = new Connector(8080, NullLogger.Instance);
        using var local = HealthyConnectorClient();
        using var remote = new HttpClient(new ReplyHandler(_ =>
        {
            // The enrollment poll revoked access while the heartbeat was in flight.
            HostedPanelState.SaveAsync(HostedPanelState.Read()! with { Credential = null }).GetAwaiter().GetResult();
            return new(HttpStatusCode.NoContent);
        }));
        Assert.False(await connector.ReportHealthAsync(original, local, remote, default));
        Assert.Null(HostedPanelState.Read()!.Credential);
        Assert.False(HostedConnectorHealth.Read()!.Ready);
    }

    [Fact]
    public async Task LateHeartbeatDenialDoesNotEraseNewActivationProof()
    {
        var state = State;
        await HostedPanelState.SaveAsync(state);
        var original = ConnectorState.Read()!;
        var reactivated = state with { Credential = state.PanelId + "." + new string('b', 64) };
        await using var connector = new Connector(8080, NullLogger.Instance);
        using var local = HealthyConnectorClient();
        using var remote = new HttpClient(new ReplyHandler(_ =>
        {
            HostedPanelState.SaveAsync(reactivated).GetAwaiter().GetResult();
            return new(HttpStatusCode.Forbidden);
        }));
        Assert.False(await connector.ReportHealthAsync(original, local, remote, default));
        Assert.Equal(reactivated, HostedPanelState.Read());
        using var allowed = new HttpClient(new ReplyHandler(_ => new(HttpStatusCode.NoContent)));
        Assert.True(await connector.ReportHealthAsync(ConnectorState.Read()!, local, allowed, default));
    }

    private static HttpClient HealthyConnectorClient() => new(new ReplyHandler(request => request.RequestUri!.AbsolutePath == "/api/status"
        ? new(HttpStatusCode.OK) { Content = JsonContent.Create(new { http = new[] { new { name = "quasar", status = "running" } } }) }
        : new(HttpStatusCode.OK)));

    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }

    private sealed class AccessSnapshotHandler(HostedPanelState state) : HttpMessageHandler, IHttpClientFactory
    {
        public bool Active = true;
        public HttpStatusCode Status = HttpStatusCode.OK, Administration = HttpStatusCode.OK;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(state.InstallationSecret, request.Headers.Authorization!.Parameter);
            Assert.StartsWith(HostedPanelState.Origin + "/api/v1/enrollments/" + state.EnrollmentId, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(request.Method == HttpMethod.Post
                ? new HttpResponseMessage(Administration) { Content = JsonContent.Create(new { notices = Array.Empty<object>() }) }
                : new HttpResponseMessage(Status) { Content = JsonContent.Create(new { panelId = state.PanelId, ownerSteamId = state.OwnerSteamId,
                    active = Active, expired = false, credential = Active ? state.Credential : null }) });
        }
    }
}
