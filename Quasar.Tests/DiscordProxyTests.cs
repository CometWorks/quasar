using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Quasar.Services.Discord;
using Xunit;

namespace Quasar.Tests;

// HttpClient.DefaultProxy is process-wide; do not overlap these tests with other collections.
[CollectionDefinition("Discord proxy", DisableParallelization = true)]
public sealed class DiscordProxyCollection;

[Collection("Discord proxy")]
public sealed class DiscordProxyTests
{
    [Fact]
    public void ExistingSettingsEnableProxyAndExplicitOptOutSurvivesRoundTripAndClone()
    {
        Assert.True(DiscordOptions.Normalize(JsonSerializer.Deserialize<DiscordOptions>("{}")).UseProxy);
        Assert.True(DiscordOptions.Normalize(null).UseProxy);
        var options = new DiscordOptions { UseProxy = false };
        var restored = DiscordOptions.Normalize(JsonSerializer.Deserialize<DiscordOptions>(JsonSerializer.Serialize(options)));
        Assert.False(restored.UseProxy);
        Assert.False(restored.Clone().UseProxy);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task BothTransportsHonorProxyOptOutAndBypassRules(bool useProxy, bool bypass)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        origin.Start();
        var proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var previousProxy = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy($"http://127.0.0.1:{proxyPort}")
        {
            BypassList = bypass ? ["127\\.0\\.0\\.1"] : [],
        };

        try
        {
            var config = DiscordBotService.CreateSocketConfig(new() { UseProxy = useProxy });
            var proxied = useProxy && !bypass;
            var listener = proxied ? proxy : origin;
            var host = proxied ? "discord-proxy.invalid:443" : $"127.0.0.1:{originPort}";
            var restUrl = $"{(proxied ? "https" : "http")}://{host}/";
            var restRequest = RespondAsync(listener, proxied ? "403 Forbidden" : "200 OK", timeout.Token);
            using (var rest = config.RestClientProvider(restUrl))
            {
                rest.SetCancelToken(timeout.Token);
                if (proxied)
                    await Assert.ThrowsAsync<HttpRequestException>(() => rest.SendAsync("GET", "api", timeout.Token, headerOnly: true));
                else
                {
                    var response = await rest.SendAsync("GET", "api", timeout.Token, headerOnly: true);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                }
            }
            Assert.Equal(proxied ? $"CONNECT {host} HTTP/1.1" : "GET /api HTTP/1.1", await restRequest);

            var gatewayRequest = RespondAsync(listener, "403 Forbidden", timeout.Token);
            using (var socket = config.WebSocketProvider())
            {
                socket.SetCancelToken(timeout.Token);
                await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync($"{(proxied ? "wss" : "ws")}://{host}/gateway"));
            }
            Assert.Equal(proxied ? $"CONNECT {host} HTTP/1.1" : "GET /gateway HTTP/1.1", await gatewayRequest);
            Assert.False((proxied ? origin : proxy).Pending());
        }
        finally
        {
            HttpClient.DefaultProxy = previousProxy;
        }
    }

    private static async Task<string> RespondAsync(TcpListener listener, string status, CancellationToken token)
    {
        using var connection = await listener.AcceptTcpClientAsync(token);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(token);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token);
        return requestLine!;
    }
}
