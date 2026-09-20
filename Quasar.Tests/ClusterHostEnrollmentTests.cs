using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterHostEnrollmentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "host-enrollment-" + Guid.NewGuid().ToString("N"));
    private readonly ClusterCredentialStore credentials;
    private readonly ClusterHostCatalog hosts;
    public ClusterHostEnrollmentTests()
    {
        Directory.CreateDirectory(root);
        credentials = new(new EphemeralDataProtectionProvider(), Path.Combine(root, "credentials.json"));
        hosts = new(credentials, Path.Combine(root, "hosts"));
    }
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("http://remote.example.com")]
    [InlineData("https://user:pass@example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com?token=secret")]
    public void EnrollmentRejectsUnsafeOrigins(string origin) =>
        Assert.Throws<ArgumentException>(() => ClusterHostInstaller.ValidateOrigin(origin));

    [Fact]
    public async Task CredentialsPersistEncryptedAndAuthenticateOnlyTheirHost()
    {
        var host = await hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        var other = await hosts.RegisterAsync("two", "Two", "10.0.0.2", 18400, default);
        string secret = credentials.Resolve(host.CredentialReference)!;
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(root, "credentials.json")));
        Assert.True(hosts.Authenticate(host.Id, "Bearer " + secret));
        Assert.False(hosts.Authenticate(other.Id, "Bearer " + secret));
        Assert.False(hosts.Authenticate(host.Id, null));
        Assert.False(hosts.Authenticate(host.Id, "Bearer " + secret[..^1]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hosts.RegisterAsync("one", "Other", "10.0.0.2", 18400, default));
        Assert.Equal(host, new ClusterHostCatalog(credentials, Path.Combine(root, "hosts")).Get(host.Id));
    }

    [Fact]
    public async Task InstallTicketsExpireAndRejectReplayAndReplacedTickets()
    {
        var host = await hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        string binary = Path.Combine(root, "Quasar.Host"); File.WriteAllText(binary, "test executable");
        var clock = new TestClock();
        var installer = new ClusterHostInstaller(hosts, credentials, binary, clock);
        (Guid Id, string Auth) Decode(HostInstallTicket ticket) =>
            (Guid.Parse(Regex.Match(ticket.Command, "host-enrollment/([a-f0-9]+)").Groups[1].Value),
             "Bearer " + Regex.Match(ticket.Command, "Bearer ([A-F0-9]+)").Groups[1].Value);
        var first = Decode(installer.Issue(host.Id, "https://quasar.example.com"));
        Assert.Null(installer.Redeem(first.Id, "Bearer wrong"));
        var script = Encoding.UTF8.GetString(installer.Redeem(first.Id, first.Auth)!);
        Assert.Contains("systemctl --user enable --now", script);
        Assert.Contains("http://127.0.0.1:18400", Encoding.UTF8.GetString(Convert.FromBase64String(
            Regex.Match(script, "printf '%s' '([^']+)' ").Groups[1].Value)));
        Assert.Null(installer.Redeem(first.Id, first.Auth));
        var second = Decode(installer.Issue(host.Id, "https://quasar.example.com"));
        var third = Decode(installer.Issue(host.Id, "https://quasar.example.com"));
        Assert.Null(installer.Redeem(second.Id, second.Auth));
        clock.Now += TimeSpan.FromMinutes(15);
        Assert.Null(installer.Redeem(third.Id, third.Auth));
        await Assert.ThrowsAsync<ArgumentException>(() => installer.InstallSshAsync(host.Id, "http://localhost:8080", new("10.0.0.1", "user"), default));
        string file = Path.Combine(root, "install.sh"); File.WriteAllText(file, script);
        using var syntax = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("bash")
        { ArgumentList = { "-n", file }, UseShellExecute = false })!;
        await syntax.WaitForExitAsync(); Assert.Equal(0, syntax.ExitCode);
    }

    [Fact]
    public async Task HttpCommandsStreamThroughAuthenticatedHostBoundTunnel()
    {
        var host = await hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        var other = await hosts.RegisterAsync("two", "Two", "10.0.0.2", 18400, default);
        var tunnels = new ClusterHostTunnels();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(hosts); builder.Services.AddSingleton(tunnels);
        builder.Services.AddSingleton(new ClusterHostInstaller(hosts, credentials));
        await using var app = builder.Build();
        app.UseWebSockets(); app.MapClusterHostEnrollmentApi();
        await app.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var origin = new Uri(app.Urls.Single());
        Uri Url(string path) => new UriBuilder(new Uri(origin, path)) { Scheme = "ws" }.Uri;
        using var unauthenticated = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() => unauthenticated.ConnectAsync(Url("/api/v1/hosts/one/connect"), deadline.Token));
        using var control = new ClientWebSocket();
        control.Options.SetRequestHeader("Authorization", "Bearer " + credentials.Resolve(host.CredentialReference));
        await control.ConnectAsync(Url("/api/v1/hosts/one/connect"), deadline.Token);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = tunnels.OpenAsync, UseProxy = false });
        var request = new HttpRequestMessage(HttpMethod.Get, host.CommandUrl + "/host/v1/status");
        request.Headers.Host = "127.0.0.1:18400";
        var responseTask = http.SendAsync(request, deadline.Token);
        byte[] signal = new byte[64]; var message = await control.ReceiveAsync(signal.AsMemory(), deadline.Token);
        string id = Encoding.UTF8.GetString(signal, 0, message.Count);
        using var wrongHost = new ClientWebSocket();
        wrongHost.Options.SetRequestHeader("Authorization", "Bearer " + credentials.Resolve(other.CredentialReference));
        await Assert.ThrowsAsync<WebSocketException>(() => wrongHost.ConnectAsync(Url("/api/v1/hosts/two/tunnels/" + id), deadline.Token));
        using var tunnel = new ClientWebSocket();
        tunnel.Options.SetRequestHeader("Authorization", "Bearer " + credentials.Resolve(host.CredentialReference));
        await tunnel.ConnectAsync(Url("/api/v1/hosts/one/tunnels/" + id), deadline.Token);
        byte[] requestBytes = new byte[4096]; var received = await tunnel.ReceiveAsync(requestBytes.AsMemory(), deadline.Token);
        Assert.Contains("Host: 127.0.0.1:18400", Encoding.ASCII.GetString(requestBytes, 0, received.Count));
        string body = new('x', 180000);
        await tunnel.SendAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), WebSocketMessageType.Binary, true, deadline.Token);
        foreach (var offset in Enumerable.Range(0, 6))
            await tunnel.SendAsync(Encoding.ASCII.GetBytes(body.Substring(offset * 30000, 30000)), WebSocketMessageType.Binary, true, deadline.Token);
        using var response = await responseTask;
        Assert.Equal(body, await response.Content.ReadAsStringAsync(deadline.Token));
        await control.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
        while (tunnels.IsConnected(host.Id)) await Task.Delay(10, deadline.Token);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(host.CommandUrl + "/host/v1/status", deadline.Token));
        await app.StopAsync(deadline.Token);
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
