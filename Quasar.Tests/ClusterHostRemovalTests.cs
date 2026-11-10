using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterHostRemovalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "host-removal-" + Guid.NewGuid().ToString("N"));
    private readonly EphemeralDataProtectionProvider protection = new();
    private readonly ClusterCredentialStore credentials;
    private readonly ClusterHostCatalog hosts;
    private readonly ClusterCatalog clusters;
    private readonly ClusterHostTunnels tunnels = new();
    private readonly ClusterHostInstaller installer;

    public ClusterHostRemovalTests()
    {
        Directory.CreateDirectory(root);
        credentials = new(protection, Path.Combine(root, "credentials.json"));
        hosts = new(credentials, Path.Combine(root, "hosts"));
        clusters = CreateClusterCatalog();
        string binary = Path.Combine(root, "Quasar.Host");
        File.WriteAllText(binary, "test Host binary");
        installer = new(hosts, credentials, binary, TimeProvider.System);
    }

    [Fact]
    public async Task RemovalRevokesPersistedCredentialAndTicketsEvenAfterReenrollment()
    {
        var host = await hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        var other = await hosts.RegisterAsync("two", "Two", "10.0.0.2", 18400, default);
        string oldCredential = credentials.Resolve(host.CredentialReference)!;
        var ticket = installer.Issue(host.Id, "https://quasar.example.com");
        var ticketId = Guid.Parse(Regex.Match(ticket.Command, "host-enrollment/([a-f0-9]+)").Groups[1].Value);
        string ticketAuth = "Bearer " + Regex.Match(ticket.Command, "Bearer ([A-F0-9]+)").Groups[1].Value;
        string retained = Path.Combine(root, "hosts", host.Id, "keep.txt");
        File.WriteAllText(retained, "retained data");

        await hosts.RemoveAsync(host.Id, clusters, tunnels, default);

        Assert.Null(hosts.Get(host.Id));
        Assert.Equal(other, Assert.Single(hosts.GetAll()));
        Assert.Null(new ClusterCredentialStore(protection, Path.Combine(root, "credentials.json")).Resolve(host.CredentialReference));
        Assert.False(hosts.Authenticate(host.Id, "Bearer " + oldCredential));
        Assert.Equal("retained data", File.ReadAllText(retained));
        Assert.Null(installer.Redeem(ticketId, ticketAuth));

        // A second outstanding command must also fail after the same ID has a fresh credential.
        await hosts.RegisterAsync(host.Id, host.Name, host.Address, host.CommandPort, default);
        ticket = installer.Issue(host.Id, "https://quasar.example.com");
        ticketId = Guid.Parse(Regex.Match(ticket.Command, "host-enrollment/([a-f0-9]+)").Groups[1].Value);
        ticketAuth = "Bearer " + Regex.Match(ticket.Command, "Bearer ([A-F0-9]+)").Groups[1].Value;
        await hosts.RemoveAsync(host.Id, clusters, tunnels, default);
        var replacement = await hosts.RegisterAsync(host.Id, host.Name, host.Address, host.CommandPort, default);
        Assert.NotEqual(oldCredential, credentials.Resolve(replacement.CredentialReference));
        Assert.False(hosts.Authenticate(host.Id, "Bearer " + oldCredential));
        Assert.Null(installer.Redeem(ticketId, ticketAuth));
        Assert.NotNull(credentials.Resolve(other.CredentialReference));
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("active")]
    [InlineData("previous")]
    [InlineData("preparation")]
    [InlineData("prepared")]
    [InlineData("update")]
    [InlineData("rollback")]
    public async Task RemovalRefusesClusterReferences(string kind)
    {
        var host = await hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        var cluster = await clusters.CreateAsync(new("fleet", "Fleet", "http://127.0.0.1:28016", "ADMIN"), default);
        var active = new ClusterActiveRevision("r", [new(host.Id, host.CommandUrl, host.CredentialReference, null!)], DateTimeOffset.UtcNow);
        var deployment = new ClusterDeploymentRequest(null, "r", [new(host.Id, host.CommandUrl, host.CredentialReference, null!)]);
        switch (kind)
        {
            case "gateway": cluster.HostCommandUrl = host.CommandUrl; cluster.HostCommandTokenEnvironmentVariable = host.CredentialReference; break;
            case "active": cluster.ActiveDeployment = active; break;
            case "previous": cluster.PreviousDeployment = active; break;
            case "preparation": cluster.Preparation = new("hash", "{}", "http://127.0.0.1:28016",
                [new(host.Id, host.CommandUrl, host.CredentialReference, "EXECUTOR", "installation", "world", "config")]); break;
            case "prepared": cluster.PreparedDeployment = deployment; break;
            case "update": cluster.Update = new(Guid.NewGuid(), deployment, new("old", [], DateTimeOffset.UtcNow), false,
                ClusterUpdatePhase.Stopping, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow); break;
            case "rollback": cluster.Update = new(Guid.NewGuid(), new(null, "r", []), active, true,
                ClusterUpdatePhase.Reverting, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow); break;
        }
        File.WriteAllText(Path.Combine(root, "clusters", "fleet", "cluster.json"),
            JsonSerializer.Serialize(cluster, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var loaded = CreateClusterCatalog();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => hosts.RemoveAsync(host.Id, loaded, tunnels, default));
        Assert.Contains("fleet", error.Message);
        Assert.Equal(host, hosts.Get(host.Id));
        Assert.NotNull(credentials.Resolve(host.CredentialReference));
    }

    [Fact]
    public async Task RemovalRefusesLocalExecutorAndInFlightSetupButAllowsUnusedHost()
    {
        var local = await hosts.RegisterAsync("local", "Local", "127.0.0.1", 18401, default);
        var host = await hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => hosts.RemoveAsync(local.Id, clusters, tunnels, default));
        using (await hosts.UseAsync([host.Id], default))
            await Assert.ThrowsAsync<InvalidOperationException>(() => hosts.RemoveAsync(host.Id, clusters, tunnels, default));
        await hosts.RemoveAsync(host.Id, clusters, tunnels, default);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => hosts.RemoveAsync(host.Id, clusters, tunnels, default));
        await Assert.ThrowsAsync<ArgumentException>(() => hosts.RemoveAsync("../one", clusters, tunnels, default));
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData(QuasarRoles.Viewer, 403)]
    [InlineData(QuasarRoles.Editor, 403)]
    [InlineData(QuasarRoles.Admin, 204)]
    public async Task ApiRequiresSecurityAdministration(string? role, int expectedStatus)
    {
        var host = await hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        await using var app = await StartApiAsync(role);
        using var client = app.GetTestClient();
        Assert.Equal(expectedStatus, (int)(await client.DeleteAsync("/api/v1/host-enrollment/one")).StatusCode);
        Assert.Equal(expectedStatus == 204, hosts.Get(host.Id) is null);
        if (expectedStatus == 204)
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/v1/host-enrollment/one")).StatusCode);
    }

    [Fact]
    public async Task RemovalDisconnectsExistingControlAndDataTunnelsAndRejectsReconnect()
    {
        var host = await hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        string credential = credentials.Resolve(host.CredentialReference)!;
        await using var app = await StartApiAsync(QuasarRoles.Admin);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var sockets = app.GetTestServer().CreateWebSocketClient();
        sockets.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + credential;
        using var control = await sockets.ConnectAsync(new("ws://localhost/api/v1/hosts/one/connect"), deadline.Token);
        while (!tunnels.IsConnected(host.Id)) await Task.Delay(10, deadline.Token);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = tunnels.OpenAsync, UseProxy = false });
        var response = http.GetAsync(host.CommandUrl + "/host/v1/status", deadline.Token);
        byte[] signal = new byte[64];
        var message = await control.ReceiveAsync(signal.AsMemory(), deadline.Token);
        string id = System.Text.Encoding.UTF8.GetString(signal, 0, message.Count);
        using var data = await sockets.ConnectAsync(new("ws://localhost/api/v1/hosts/one/tunnels/" + id), deadline.Token);
        await data.ReceiveAsync(new byte[4096].AsMemory(), deadline.Token);

        using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/host-enrollment/one", deadline.Token)).StatusCode);
        Assert.False(tunnels.IsConnected(host.Id));
        await Assert.ThrowsAsync<HttpRequestException>(() => response);
        await Assert.ThrowsAnyAsync<Exception>(() => sockets.ConnectAsync(new("ws://localhost/api/v1/hosts/one/connect"), deadline.Token));
        client.DefaultRequestHeaders.Authorization = new("Bearer", credential);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/hosts/one/update", deadline.Token)).StatusCode);
    }

    private async Task<WebApplication> StartApiAsync(string? role)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(hosts);
        builder.Services.AddSingleton(clusters);
        builder.Services.AddSingleton(tunnels);
        builder.Services.AddSingleton(installer);
        builder.Services.AddAuthentication(QuasarAuthSchemes.Cookie).AddCookie(QuasarAuthSchemes.Cookie, options =>
        {
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization(Quasar.Program.ConfigureAuthorizationPolicies);
        var app = builder.Build();
        app.UseAuthentication();
        app.Use((context, next) =>
        {
            if (role is not null) context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], QuasarAuthSchemes.Cookie));
            return next(context);
        });
        app.UseAuthorization();
        app.UseWebSockets();
        app.MapClusterHostEnrollmentApi();
        await app.StartAsync();
        return app;
    }

    private ClusterCatalog CreateClusterCatalog() => new(NullLogger<ClusterCatalog>.Instance,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Quasar:ClusterCatalogPath"] = Path.Combine(root, "clusters") }).Build());

    public void Dispose()
    {
        clusters.Dispose();
        Directory.Delete(root, true);
    }
}
