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
    public void HostBinaryDefaultsToTheReleaseLayoutAndHonoursTheDevelopmentOverride()
    {
        Assert.Equal(Path.Combine(root, "Host", "Quasar.Host"), ClusterHostInstaller.ResolveBinary(null, root));
        Assert.Equal(Path.Combine(root, "Host", "Quasar.Host"), ClusterHostInstaller.ResolveBinary(" ", root));
        Assert.Equal(Path.Combine(root, "dev", "Quasar.Host"), ClusterHostInstaller.ResolveBinary(Path.Combine(root, "dev", "Quasar.Host"), "/elsewhere"));
    }

    [Fact]
    public async Task CorruptHostRegistrationDoesNotBreakOtherHosts()
    {
        var healthy = await hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        await hosts.RegisterAsync("two", "Two", "10.0.0.2", 18400, default);
        File.WriteAllBytes(Path.Combine(root, "hosts", "two", "host.json"), []);

        Assert.Equal([healthy], hosts.GetAll());
        Assert.True(hosts.Authenticate(healthy.Id, "Bearer " + credentials.Resolve(healthy.CredentialReference)));
        Assert.False(hosts.Authenticate("two", "Bearer anything"));
        // The broken registration can be replaced by enrolling the Host again.
        Assert.Equal("two", (await hosts.RegisterAsync("two", "Two", "10.0.0.2", 18400, default)).Id);
    }

    [Fact]
    public void CorruptCredentialFileIsSetAsideInsteadOfAbortingStartup()
    {
        string path = Path.Combine(root, "torn-credentials.json");
        File.WriteAllText(path, "{\"QSR_MANAGED_");

        var store = new ClusterCredentialStore(new EphemeralDataProtectionProvider(), path);

        Assert.Single(Directory.GetFiles(root, "torn-credentials.json.corrupt-*"));
        string reference = store.Create("cluster:demo", "admin");
        Assert.NotNull(store.Resolve(reference));
        Assert.Single(Directory.GetFiles(root, "torn-credentials.json.corrupt-*"));
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
        Assert.Equal(1, Regex.Matches(script, "cat <<'QSR_BINARY'").Count);
        Assert.Contains(Regex.Matches(script, "printf '%s' '([^']+)' ").Select(match =>
            Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value))),
            value => value.Contains("http://127.0.0.1:18400"));
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
    public async Task ExistingHostUpgradeKeepsStateAndRollsBackFailedRestart()
    {
        if (!OperatingSystem.IsLinux()) return;
        var host = await hosts.RegisterAsync("one", "One", "127.0.0.1", 18400, default);
        string binary = Path.Combine(root, "Quasar.Host");
        string home = Path.Combine(root, "home");
        string fakeBin = Path.Combine(root, "fake-bin");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(fakeBin);
        string systemctl = Path.Combine(fakeBin, "systemctl");
        File.WriteAllText(systemctl, """
            #!/bin/sh
            echo "$*" >> "$HOME/systemctl.log"
            if [ "$2" = restart ] && [ -e "$HOME/fail-restart" ]; then
                rm "$HOME/fail-restart"
                exit 1
            fi
            exit 0
            """ + "\n");
        File.SetUnixFileMode(systemctl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var installer = new ClusterHostInstaller(hosts, credentials, binary, TimeProvider.System);
        string installed = Path.Combine(home, ".local/share/Quasar/Hosts/one");

        async Task<int> Install(string version, string origin)
        {
            File.WriteAllText(binary, version);
            byte[] script = installer.BuildScript(host, new Uri(origin));
            var start = new System.Diagnostics.ProcessStartInfo("bash")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.Environment["HOME"] = home;
            start.Environment["PATH"] = fakeBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.StandardInput.BaseStream.WriteAsync(script);
            process.StandardInput.Close();
            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode;
        }

        Assert.Equal(0, await Install("old", "http://localhost:8080"));
        string configPath = Path.Combine(installed, "host.json");
        string config = File.ReadAllText(configPath).Replace("\"attachments\":[]", "\"attachments\":[{\"name\":\"existing\"}]");
        File.WriteAllText(configPath, config);
        string credentialPath = Path.Combine(installed, "state/credentials.json");
        string secret = File.ReadAllText(credentialPath);
        File.WriteAllText(Path.Combine(installed, "state/keep"), "running cluster state");

        Assert.Equal(0, await Install("new", "http://127.0.0.1:8080"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(installed, "Quasar.Host")));
        Assert.Equal(config, File.ReadAllText(configPath));
        Assert.Equal(secret, File.ReadAllText(credentialPath));
        Assert.Equal("running cluster state", File.ReadAllText(Path.Combine(installed, "state/keep")));
        Assert.Equal(1, File.ReadAllLines(Path.Combine(home, "systemctl.log")).Count(line => line.Contains(" restart ")));

        Assert.Equal(0, await Install("new", "http://127.0.0.1:8080"));
        Assert.Equal(1, File.ReadAllLines(Path.Combine(home, "systemctl.log")).Count(line => line.Contains(" restart ")));

        File.WriteAllText(Path.Combine(home, "fail-restart"), "fail once");
        Assert.NotEqual(0, await Install("broken", "http://127.0.0.1:8080"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(installed, "Quasar.Host")));

        File.WriteAllText(credentialPath, "different enrollment");
        Assert.NotEqual(0, await Install("foreign", "http://127.0.0.1:8080"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(installed, "Quasar.Host")));
    }

    [Fact]
    public async Task ConnectedHostDoesNotHoldGracefulShutdown()
    {
        var host = await hosts.RegisterAsync("one", "One", "10.0.0.1", 18400, default);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(hosts); builder.Services.AddSingleton<ClusterHostTunnels>();
        builder.Services.AddSingleton(new ClusterHostInstaller(hosts, credentials));
        await using var app = builder.Build();
        app.UseWebSockets(); app.MapClusterHostEnrollmentApi();
        await app.StartAsync();
        using var control = new ClientWebSocket();
        control.Options.SetRequestHeader("Authorization", "Bearer " + credentials.Resolve(host.CredentialReference));
        var url = new UriBuilder(new Uri(new Uri(app.Urls.Single()), "/api/v1/hosts/one/connect")) { Scheme = "ws" }.Uri;
        await control.ConnectAsync(url, default);
        Assert.True(app.Services.GetRequiredService<ClusterHostTunnels>().IsConnected("one"));

        // The Host keeps its control channel open for as long as it runs; shutdown must not wait for it.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"StopAsync took {stopwatch.Elapsed}");
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
