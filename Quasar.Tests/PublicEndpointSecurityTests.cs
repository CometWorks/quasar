using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Magnetar.Protocol.Discovery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed class PublicEndpointSecurityTests
{
    [Fact]
    public void PrivateLanHttpModeIsTheDefault()
    {
        var options = new QuasarAuthOptions();

        Assert.False(options.RequireHttpsForPublicAccess);
        Assert.True(options.TrustedNetworkBypass.AllowSameSubnet);
    }

    [Fact]
    public void ActiveSameSubnetAddressIsTrustedByDefault()
    {
        var localAddress = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                network.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .FirstOrDefault(address => address.Address.AddressFamily == AddressFamily.InterNetwork &&
                address.IPv4Mask is not null)?.Address;

        Assert.NotNull(localAddress);
        Assert.True(TrustedNetworkEvaluator.IsTrustedAddress(localAddress, new TrustedNetworkBypassOptions()));
    }

    [Theory]
    [InlineData("127.0.0.1", null, true)]
    [InlineData("::1", null, true)]
    [InlineData("localhost", null, true)]
    [InlineData("0.0.0.0", null, false)]
    [InlineData("quasar.example", null, false)]
    [InlineData("0.0.0.0", "http://127.0.0.1:8080;https://[::1]:8443", true)]
    [InlineData("127.0.0.1", "http://localhost:8080;http://0.0.0.0:8081", false)]
    [InlineData("127.0.0.1", "https://quasar.example:8443", false)]
    public void DisabledAuthenticationRequiresLoopbackOnlyListeners(
        string host,
        string? aspNetCoreUrls,
        bool accepted)
    {
        var validate = () => PublicAccessSecurity.ValidateStartup(
            new QuasarAuthOptions { Enabled = false },
            new WebServiceOptions { Host = host },
            aspNetCoreUrls);

        if (accepted)
            validate();
        else
            Assert.Throws<InvalidOperationException>(validate);
    }

    [Fact]
    public void EnabledAuthenticationAllowsPublicListener() =>
        PublicAccessSecurity.ValidateStartup(
            new QuasarAuthOptions { Enabled = true },
            new WebServiceOptions { Host = "0.0.0.0" },
            "http://+:8080");

    [Theory]
    [InlineData("203.0.113.10", "http", null, null, "/", true, 400)]
    [InlineData("127.0.0.1", "http", null, null, "/", true, 204)]
    [InlineData("::ffff:127.0.0.1", "http", null, null, "/", true, 204)]
    [InlineData("203.0.113.10", "https", null, null, "/", true, 204)]
    [InlineData("203.0.113.10", "http", null, null, "/", false, 204)]
    [InlineData("203.0.113.10", "http", null, null, "/ws/agent", true, 204)]
    [InlineData("192.0.2.20", "http", "203.0.113.10", "https", "/", true, 204)]
    [InlineData("203.0.113.10", "http", "127.0.0.1", "https", "/", true, 400)]
    public async Task PublicHttpRequiresEffectiveHttpsAfterTrustedForwarding(
        string peer,
        string scheme,
        string? forwardedFor,
        string? forwardedProto,
        string path,
        bool requireHttps,
        int expectedStatus)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        var forwarding = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };
        forwarding.KnownIPNetworks.Clear();
        forwarding.KnownProxies.Clear();
        forwarding.KnownProxies.Add(IPAddress.Parse("192.0.2.20"));
        app.UseForwardedHeaders(forwarding);
        app.Use((context, next) => PublicAccessSecurity.InvokeAsync(
            context,
            next,
            new QuasarAuthOptions { RequireHttpsForPublicAccess = requireHttps }));
        app.MapGet("/{**path}", () => Results.NoContent());
        await app.StartAsync();

        var result = await app.GetTestServer().SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Request.Method = "GET";
            context.Request.Scheme = scheme;
            context.Request.Path = path;
            context.Request.Host = new HostString("quasar.example");
            if (forwardedFor is not null)
                context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            if (forwardedProto is not null)
                context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(expectedStatus, result.Response.StatusCode);
    }

    [Fact]
    public async Task PublicProbesReturnOnlyStatus()
    {
        string root = Path.Combine(Path.GetTempPath(), "public-probes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(new ClusterOperationStore(root));
            builder.Services.AddSingleton(new WebServiceState(
                new WebServiceOptions(), null!, null!, null!)
            {
                CurrentManifest = new WebServiceDiscoveryManifest
                {
                    WorkerId = "secret-worker",
                    MachineName = "secret-host",
                    ProcessId = 123,
                },
            });
            await using var app = builder.Build();
            app.MapPublicProbes();
            await app.StartAsync();
            using var client = app.GetTestClient();

            foreach (string path in new[] { "/api/health", "/api/ready" })
            {
                using var response = await client.GetAsync(path).WaitAsync(TimeSpan.FromSeconds(5));
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(["status"], payload.RootElement.EnumerateObject().Select(property => property.Name));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LogoutRequiresPostWithAntiforgeryToken()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = QuasarAuthSchemes.Cookie;
                options.DefaultSignOutScheme = QuasarAuthSchemes.Cookie;
            })
            .AddCookie(QuasarAuthSchemes.Cookie);
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        Quasar.Program.MapAuthenticationEndpoints(app, new QuasarAuthOptions());
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var getLogout = await client.GetAsync("/logout").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getLogout.StatusCode);

        using var missingToken = await client.PostAsync("/logout", new FormUrlEncodedContent([]))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);

        using var accessDenied = await client.GetAsync("/access-denied").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Forbidden, accessDenied.StatusCode);
        string html = await accessDenied.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"(?<name>__RequestVerificationToken)\" value=\"(?<token>[^\"]+)\"");
        Assert.True(token.Success);
        string cookie = accessDenied.Headers.GetValues("Set-Cookie")
            .Single(value => value.Contains("Antiforgery", StringComparison.Ordinal))
            .Split(';', 2)[0];

        using var validPost = new HttpRequestMessage(HttpMethod.Post, "/logout")
        {
            Content = new FormUrlEncodedContent([
                new(token.Groups["name"].Value, WebUtility.HtmlDecode(token.Groups["token"].Value)),
            ]),
        };
        validPost.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var signedOut = await client.SendAsync(validPost).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/", signedOut.Headers.Location?.OriginalString);
    }
}
