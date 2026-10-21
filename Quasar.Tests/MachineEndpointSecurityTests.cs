using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Quasar.Networking;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class MachineEndpointSecurityTests
{
    private static readonly MachineEndpointPolicy Policy = new(
        ["127.0.0.1", "::1", "10.42.0.0/24", "192.168.60.8", "fd12:3456:789a::/64"],
        ["quasar.vpn:8080", "127.0.0.1:8080", "[::1]:8080"]);

    [Fact]
    public void DefaultConfigurationIsRestrictiveAndExplicitListsReplaceIt()
    {
        var defaults = MachineEndpointSecurity.Create(new ConfigurationBuilder().Build(), new WebServiceOptions());
        Assert.True(defaults.Allows(IPAddress.Loopback, false, "127.0.0.1:8080"));
        Assert.False(defaults.Allows(IPAddress.Parse("10.42.0.12"), false, "127.0.0.1:8080"));
        Assert.False(defaults.Allows(IPAddress.Loopback, false, "attacker.test:8080"));
        Assert.False(defaults.Allows(null, false, "127.0.0.1:8080"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Quasar:MachineEndpoints:AllowedSources:0"] = "10.42.0.12",
            ["Quasar:MachineEndpoints:AllowedHosts:0"] = "quasar.vpn:8080",
        }).Build();
        var configured = MachineEndpointSecurity.Create(config, new WebServiceOptions());
        Assert.True(configured.Allows(IPAddress.Parse("10.42.0.12"), false, "quasar.vpn:8080"));
        Assert.False(configured.Allows(IPAddress.Loopback, false, "quasar.vpn:8080"));
        Assert.Throws<ArgumentException>(() => MachineEndpointSecurity.Create(config,
            new WebServiceOptions { BaseUrl = "https://user:secret@quasar.vpn" }));
        Assert.Throws<ArgumentException>(() => new MachineEndpointPolicy([], ["quasar.vpn"]));
        Assert.Throws<ArgumentException>(() => new MachineEndpointPolicy(["127.0.0.1"], []));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::ffff:10.42.0.12", true)]
    [InlineData("10.42.0.254", true)]
    [InlineData("192.168.60.8", true)]
    [InlineData("fd12:3456:789a::123", true)]
    [InlineData("10.43.0.12", false)]
    [InlineData("192.168.60.9", false)]
    [InlineData("fd12:3456:789b::123", false)]
    [InlineData("203.0.113.2", false)]
    public void MultipleExplicitSourcesUseSocketAddress(string source, bool allowed) =>
        Assert.Equal(allowed, Policy.Allows(IPAddress.Parse(source), false, "quasar.vpn:8080"));

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("10.0.0.0/8")]
    [InlineData("192.0.0.0/8")]
    [InlineData("fc00::/7")]
    [InlineData("8.8.8.8")]
    [InlineData("*")]
    [InlineData("10.0.0.0/33")]
    [InlineData("")]
    public void InsecureOrInvalidSourceFailsConfiguration(string source) =>
        Assert.Throws<ArgumentException>(() => new MachineEndpointPolicy([source], ["quasar.vpn:8080"]));

    [Theory]
    [InlineData("*")]
    [InlineData("*.vpn")]
    [InlineData("0.0.0.0:8080")]
    [InlineData("[::]:8080")]
    [InlineData("user:secret@quasar.vpn")]
    [InlineData("quasar.vpn/path")]
    [InlineData("")]
    public void InsecureAuthorityFailsConfiguration(string host) =>
        Assert.Throws<ArgumentException>(() => new MachineEndpointPolicy(["127.0.0.1"], [host]));

    [Theory]
    [InlineData("evil.example:8080")]
    [InlineData("quasar.vpn")]
    [InlineData("quasar.vpn:8081")]
    [InlineData("quasar.vpn.:8080")]
    [InlineData("quasar.vpn:8080,evil.example")]
    public void HostMustMatchConfiguredAuthority(string host) =>
        Assert.False(Policy.Allows(IPAddress.Loopback, false, host));

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("http://quasar.vpn:8080")]
    public async Task AnyOriginHeaderIsRefused(string origin)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Path = "/ws/agent";
        context.Request.Host = new("quasar.vpn:8080");
        context.Request.Headers["Origin"] = origin;
        await MachineEndpointSecurity.InvokeAsync(context, _ => throw new Exception("Reached endpoint"), Policy);
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/ws/agent", true)]
    [InlineData("/WS/AGENT/", true)]
    [InlineData("/api/internal/drain", true)]
    [InlineData("/api/v1/hosts/one/connect", true)]
    [InlineData("/api/v1/hosts/one/tunnels/30f6736c675c430ba37a03375e64fb8c", true)]
    [InlineData("/api/v1/hosts/one/update/binary", true)]
    [InlineData("/api/v1/host-enrollment/30f6736c675c430ba37a03375e64fb8c", true)]
    [InlineData("/api/v1/host-enrollment", false)]
    [InlineData("/api/v1/host-enrollment/one/manual", false)]
    [InlineData("/api/v1/clusters/fleet/status", false)]
    [InlineData("/_blazor", false)]
    [InlineData("/login", false)]
    public void MachineRoutesAreSeparateFromBrowserFlows(string path, bool machine) =>
        Assert.Equal(machine, MachineEndpointSecurity.IsMachinePath(path));

    [Theory]
    [InlineData("203.0.113.2", "quasar.vpn:8080", "127.0.0.1", "quasar.vpn:8080", 403)]
    [InlineData("127.0.0.1", "evil.example:8080", "127.0.0.1", "quasar.vpn:8080", 403)]
    [InlineData("127.0.0.1", "quasar.vpn:8080", "203.0.113.2", "evil.example:8080", 204)]
    public async Task ReverseProxyPolicyUsesOriginalSocketPeerAndHost(string peer, string host,
        string forwardedPeer, string forwardedHost, int expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            return MachineEndpointSecurity.InvokeAsync(context, next, Policy);
        });
        app.UseForwardedHeaders(new ForwardedHeadersOptions
            { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost });
        app.MapGet("/api/v1/hosts/one/update", () => Results.NoContent());
        await app.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/hosts/one/update");
        request.Headers.Host = host;
        request.Headers.Add("X-Forwarded-For", forwardedPeer);
        request.Headers.Add("X-Forwarded-Host", forwardedHost);
        request.Headers.Add("Forwarded", "for=127.0.0.1;host=quasar.vpn:8080");
        request.Headers.Add("X-Original-For", "127.0.0.1");
        request.Headers.Add("X-Real-IP", "127.0.0.1");
        using var response = await app.GetTestClient().SendAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, (int)response.StatusCode);
    }

    [Fact]
    public async Task AutomaticForwardingCannotRunBeforeMachinePolicy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["ForwardedHeaders_Enabled"] = "true";
        MachineEndpointSecurity.DisableAutomaticForwarding(builder.Configuration);
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.Use((context, next) => MachineEndpointSecurity.InvokeAsync(context, next, Policy));
        app.MapGet("/api/internal/probe", () => Results.NoContent());
        await app.StartAsync();
        foreach (var (peer, forwarded, status) in new[]
            { ("203.0.113.2", "127.0.0.1", 403), ("127.0.0.1", "203.0.113.2", 204) })
        {
            var result = await app.GetTestServer().SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                context.Request.Method = "GET";
                context.Request.Path = "/api/internal/probe";
                context.Request.Host = new("quasar.vpn:8080");
                context.Request.Headers["X-Forwarded-For"] = forwarded;
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(status, result.Response.StatusCode);
        }
    }

    [Fact]
    public async Task EnrolledMachineRoutesRequireBothTransportPolicyAndIdentityCredential()
    {
        string root = Path.Combine(Path.GetTempPath(), "machine-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ClusterCredentialStore(new EphemeralDataProtectionProvider(), Path.Combine(root, "credentials.json"));
            var hosts = new ClusterHostCatalog(store, Path.Combine(root, "hosts"));
            var host = await hosts.RegisterAsync("one", "One", "10.42.0.12", 18400, default);
            var other = await hosts.RegisterAsync("two", "Two", "10.42.0.13", 18400, default);
            string binary = Path.Combine(root, "Quasar.Host");
            await File.WriteAllTextAsync(binary, "fake release bytes");
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton(hosts);
            builder.Services.AddSingleton<ClusterHostTunnels>();
            builder.Services.AddSingleton(new ClusterHostInstaller(hosts, store, binary, TimeProvider.System));
            await using var app = builder.Build();
            app.Use((context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return MachineEndpointSecurity.InvokeAsync(context, next, Policy);
            });
            app.UseWebSockets();
            app.MapClusterHostEnrollmentApi();
            await app.StartAsync();
            using var client = app.GetTestClient();
            client.DefaultRequestHeaders.Host = "quasar.vpn:8080";
            foreach (string route in new[] { "update", "update/binary", "connect", "tunnels/" + Guid.NewGuid() })
            {
                string path = "/api/v1/hosts/one/" + route;
                foreach (string? credential in new[] { null, "wrong", store.Resolve(other.CredentialReference) })
                {
                    client.DefaultRequestHeaders.Authorization = credential is null ? null : new("Bearer", credential);
                    using var refused = await client.GetAsync(path).WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
                }
                client.DefaultRequestHeaders.Authorization = new("Bearer", store.Resolve(host.CredentialReference));
                var browser = await app.GetTestServer().SendAsync(context =>
                {
                    context.Request.Method = "GET";
                    context.Request.Path = path;
                    context.Request.Host = new("quasar.vpn:8080");
                    context.Request.Headers.Authorization = "Bearer " + store.Resolve(host.CredentialReference);
                    context.Request.Headers["Origin"] = "";
                }).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(StatusCodes.Status403Forbidden, browser.Response.StatusCode);
            }
            using var valid = await client.GetAsync("/api/v1/hosts/one/update").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        }
        finally { Directory.Delete(root, true); }
    }
}
