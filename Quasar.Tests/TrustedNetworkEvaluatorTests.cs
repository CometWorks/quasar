using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

// In-memory request objects only: no host, listener, HTTP client or endpoint calls.
public sealed class TrustedNetworkEvaluatorTests
{
    [Theory]
    [InlineData("127.0.0.1:8080", null, null, true)]
    [InlineData("localhost:8080", "http://localhost:8080", "same-origin", true)]
    [InlineData("[::1]:8080", "http://[::1]:8080", "same-origin", true)]
    [InlineData("localhost:8080", null, "none", true)]
    [InlineData("unconfigured.test:8080", null, null, false)]
    [InlineData("localhost:8081", null, null, false)]
    [InlineData("localhost:8080", "http://other.test", null, false)]
    [InlineData("localhost:8080", "null", null, false)]
    [InlineData("localhost:8080", "http://localhost:8080/path", null, false)]
    [InlineData("localhost:8080", "http://localhost:8080/path/..", null, false)]
    [InlineData("localhost:8080", " http://localhost:8080", null, false)]
    [InlineData("localhost:8080", "https://localhost:8080", null, false)]
    [InlineData("localhost:8080", "http://localhost:8080", "cross-site", false)]
    [InlineData("localhost:8080", null, "same-site", false)]
    [InlineData("localhost:8080", "", null, false)]
    public async Task DefaultLoopbackRequiresIntendedDestinationAndBrowserContext(
        string host, string? origin, string? site, bool expected)
    {
        var context = Request(host);
        if (origin is not null) context.Request.Headers.Origin = origin;
        if (site is not null) context.Request.Headers["Sec-Fetch-Site"] = site;
        Assert.Equal(expected, await Evaluate(context));
    }

    [Fact]
    public async Task PublicBaseAndConcreteListenerKeepTheirAuthorities()
    {
        var web = new WebServiceOptions { BaseUrl = "https://quasar.example/control", ListenUrl = "http://quasar.lan:8080" };
        var context = Request("quasar.example");
        context.Request.Scheme = "https";
        context.Request.Headers.Origin = "https://quasar.example:443";
        context.Request.Headers["Sec-Fetch-Site"] = "same-origin"; // Viewer iframe/fetch.
        Assert.True(await Evaluate(context, web));
        Assert.True(await Evaluate(Request("quasar.lan:8080"), web));
        Assert.True(await Evaluate(Request("192.0.2.10:8080"), web)); // Actual socket destination.
        Assert.False(await Evaluate(Request("192.0.2.11:8080"), web));
    }

    [Theory]
    [InlineData("http://localhost:8080/ui", true)]
    [InlineData("https://other.test/ui", false)]
    [InlineData("invalid", false)]
    public async Task LegacyBrowserRefererMustBeSameOrigin(string referer, bool expected)
    {
        var context = Request("localhost:8080");
        context.Request.Headers.Referer = referer;
        Assert.Equal(expected, await Evaluate(context));
    }

    [Fact]
    public async Task AddressOptionsRemainNecessary()
    {
        var context = Request("localhost:8080");
        Assert.False(await Evaluate(context, auth: new() { TrustedNetworkBypass = new() { AllowLoopback = false } }));
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");
        Assert.False(await Evaluate(context)); // A public address is not on the trusted LAN.
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:127.0.0.1");
        Assert.True(await Evaluate(context));
    }

    [Theory]
    [InlineData("192.168.1.80", "192.168.1.10", true)]
    [InlineData("192.168.1.10", "192.168.1.80", true)]
    [InlineData("192.168.255.254", "192.168.1.80", true)]
    [InlineData("10.255.255.254", "192.168.1.80", true)]
    [InlineData("172.16.0.1", "192.168.1.80", true)]
    [InlineData("172.31.255.254", "192.168.1.80", true)]
    [InlineData("::ffff:192.168.1.10", "192.168.1.80", true)]
    [InlineData("fc00::1", "fd12::80", true)]
    [InlineData("fdff::10", "fd12::80", true)]
    [InlineData("192.167.255.254", "192.168.1.80", false)]
    [InlineData("192.169.0.1", "192.168.1.80", false)]
    [InlineData("172.15.255.254", "192.168.1.80", false)]
    [InlineData("172.32.0.1", "192.168.1.80", false)]
    [InlineData("100.64.0.1", "192.168.1.80", false)]
    [InlineData("2001:db8::10", "fd12::80", false)]
    public async Task PrivateLanHttpAccessIsEnabledByDefault(string client, string server, bool expected)
    {
        var address = IPAddress.Parse(server);
        var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{server}]:8080" : $"{server}:8080";
        var context = Request(host);
        context.Connection.RemoteIpAddress = IPAddress.Parse(client);
        context.Connection.LocalIpAddress = address;
        context.Request.Headers.Origin = $"http://{host}";
        context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        Assert.Equal(expected, await Evaluate(context));
    }

    [Fact]
    public async Task PrivateLanCanBeDisabledAndStillRequiresDestinationOriginAndProxyChecks()
    {
        DefaultHttpContext LanRequest()
        {
            var context = Request("192.168.1.80:8080");
            context.Connection.LocalIpAddress = IPAddress.Parse("192.168.1.80");
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.10");
            return context;
        }
        Assert.False(await Evaluate(LanRequest(), auth: new() { TrustedNetworkBypass = new() { AllowSameSubnet = false } }));
        var context = LanRequest();
        context.Request.Host = new HostString("unconfigured.test:8080");
        Assert.False(await Evaluate(context));
        context = LanRequest();
        context.Request.Headers.Origin = "http://other.test";
        Assert.False(await Evaluate(context));
        context = LanRequest();
        context.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        Assert.False(await Evaluate(context));
        context = LanRequest();
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        Assert.False(await Evaluate(context));
    }

    [Theory]
    [InlineData("127.0.0.1", "localhost:8080", "quasar.example", "127.0.0.1", true)]
    [InlineData("127.0.0.1", "localhost:8080", "quasar.example", "127.0.0.1:54321", true)]
    [InlineData("192.0.2.20", "localhost:8080", "quasar.example", "127.0.0.1", true)]
    [InlineData("192.0.2.21", "localhost:8080", "quasar.example", "127.0.0.1", false)]
    [InlineData("127.0.0.1", "unconfigured.test:8080", "quasar.example", "127.0.0.1", false)]
    [InlineData("127.0.0.1", "localhost:8080", "unconfigured.test", "127.0.0.1", false)]
    [InlineData("127.0.0.1", "localhost:8080", "quasar.example", "203.0.113.5", false)]
    public async Task ProxyRequiresOriginalDestinationKnownPeerAndEffectiveClient(
        string peer, string originalHost, string forwardedHost, string client, bool expected)
    {
        var context = Request(originalHost);
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Headers["X-Forwarded-For"] = client;
        context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers.Origin = "https://quasar.example";
        var forwarding = Forwarding();
        forwarding.KnownProxies.Add(IPAddress.Parse("192.0.2.20"));
        Assert.Equal(expected, await Evaluate(context, new() { BaseUrl = "https://quasar.example" }, forwarding: forwarding));
    }

    [Theory]
    [InlineData("X-Original-For", "127.0.0.1")]
    [InlineData("X-Forwarded-Host", "localhost:8080")]
    [InlineData("Forwarded", "for=127.0.0.1")]
    [InlineData("X-Real-IP", "127.0.0.1")]
    public async Task ForwardingEvidenceComesFromMiddlewareNotClientOriginalHeaders(string header, string value)
    {
        var context = Request("localhost:8080");
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");
        context.Request.Headers[header] = value;
        context.Request.Headers["X-Original-For"] = "127.0.0.1";
        Assert.False(await Evaluate(context));
    }

    [Fact]
    public async Task AmbiguousOrUnconsumedForwardingAndMultipleOriginsFailClosed()
    {
        var context = Request("localhost:8080");
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1, 127.0.0.1";
        Assert.False(await Evaluate(context));
        context = Request("localhost:8080");
        context.Request.Headers.Origin = new[] { "http://localhost:8080", "http://localhost:8080" };
        Assert.False(await Evaluate(context));
        var evaluator = new TrustedNetworkEvaluator(new(), new());
        Assert.False(evaluator.IsTrusted(Request("localhost:8080"))); // Missing original capture.
    }

    private static DefaultHttpContext Request(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString(host);
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalIpAddress = IPAddress.Parse("192.0.2.10");
        context.Connection.LocalPort = 8080;
        return context;
    }

    private static ForwardedHeadersOptions Forwarding()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
        return options;
    }

    private static async Task<bool> Evaluate(DefaultHttpContext context, WebServiceOptions? web = null,
        QuasarAuthOptions? auth = null, ForwardedHeadersOptions? forwarding = null)
    {
        var evaluator = new TrustedNetworkEvaluator(auth ?? new(), web ?? new());
        forwarding ??= Forwarding();
        evaluator.CaptureOriginalRequest(context, forwarding);
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask,
            NullLoggerFactory.Instance, Options.Create(forwarding));
        await middleware.Invoke(context);
        return evaluator.IsTrusted(context);
    }
}
