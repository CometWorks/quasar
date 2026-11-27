using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Magnetar.Protocol.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quasar.Hosting;
using Quasar.Services.Plugins;
using Xunit;

namespace Quasar.Tests;

// The worker opens the hosted direct port in the real native peer and checks it
// through a discovery service that behaves like Nebula's STUN probe.
[Collection("Exact server creation")]
public sealed class QuasarDirectPortTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quasar-direct-port-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? previous;

    public QuasarDirectPortTests()
    {
        previous = cache.GetValue(null);
        cache.SetValue(null, root);
    }

    [Fact]
    public async Task OnlyHostedPanelsAreAskedAndPortsAreValidated()
    {
        Assert.False(QuasarDirectPortService.Pending);
        await HostedPanelState.SaveAsync(new() { Choice = "hosted", PanelId = "test-panel", Credential = "test-panel." + new string('a', 64), SetupCompleted = true });
        Assert.True(QuasarDirectPortService.Pending);
        await using var native = new QuasarPeerProcess(Options.Create(new QuasarRtcOptions { PeerExecutable = Path.Combine(root, "absent-peer") }));
        var service = new QuasarDirectPortService(native, NullLogger<QuasarDirectPortService>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ConfigureAsync(80, false, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ConfigureAsync(null, false, default));
        // A missing component is reported on the saved port rather than failing setup.
        var saved = await service.ConfigureAsync(3479, false, default);
        Assert.Equal(3479, saved.Port);
        Assert.False(saved.Reachable);
        Assert.Contains("not installed", saved.Error);
        Assert.False(QuasarDirectPortService.Pending);
        Assert.True((await service.ConfigureAsync(null, true, default)).Skipped);
        Assert.False(QuasarDirectPortService.Pending);
        await HostedPanelState.SaveAsync(HostedPanelState.Read()! with { Choice = "self" });
        await Assert.ThrowsAsync<Quasar.Plugin.Abstractions.Operations.QuasarOperationException>(() => service.ConfigureAsync(3479, false, default));
    }

    [RtcPeerProcessFact]
    public async Task RealPeerOpensThePortAndTheProbeReportsItsReachability()
    {
        await HostedPanelState.SaveAsync(new() { Choice = "hosted", PanelId = "test-panel", Credential = "test-panel." + new string('a', 64), SetupCompleted = true });
        using var open = new FakeDiscovery(sendProbe: true);
        using var closed = new FakeDiscovery(sendProbe: false);
        var options = new QuasarRtcOptions { PeerExecutable = Environment.GetEnvironmentVariable("QUASAR_RTC_TEST_PEER")!, StunUrls = ["stun:" + open.Address] };
        await using var native = new QuasarPeerProcess(Options.Create(options));
        var service = new QuasarDirectPortService(native, NullLogger<QuasarDirectPortService>.Instance);
        var port = FreeUdpPort();

        var result = await service.ConfigureAsync(port, false, default);
        Assert.Equal(new QuasarDirectPortBinding(port, null), native.DirectPort);
        Assert.True(result.Reachable, result.Error);
        Assert.Equal("127.0.0.1", result.Address);
        Assert.False(result.AddressPublic);
        Assert.NotNull(result.CheckedAt);
        Assert.Equal(1, open.Probes);

        // The probe arrives only through the opened port, never as a reply on the request socket.
        options.StunUrls = ["stun:" + closed.Address];
        result = await service.ProbeAsync(default);
        Assert.False(result.Reachable);
        Assert.Equal("127.0.0.1", result.Address);
        Assert.Null(result.Error);

        // An occupied port leaves the peer on ephemeral ports and says why.
        using var occupied = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var busy = ((IPEndPoint)occupied.Client.LocalEndPoint!).Port;
        result = await service.ConfigureAsync(busy, false, default);
        Assert.False(result.Reachable);
        Assert.Contains("in use", result.Error);
        Assert.Null(native.DirectPort!.Port);
    }

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    public void Dispose()
    {
        cache.SetValue(null, previous);
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
    }

    // Answers STUN Binding requests and, like Nebula, sends the probe datagram to the
    // sender's address at the requested port from the service socket.
    private sealed class FakeDiscovery : IDisposable
    {
        private readonly UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource stop = new();
        public int Probes;
        public string Address => socket.Client.LocalEndPoint!.ToString()!;

        public FakeDiscovery(bool sendProbe) => _ = ServeAsync(sendProbe);

        private async Task ServeAsync(bool sendProbe)
        {
            try
            {
                while (true)
                {
                    var received = await socket.ReceiveAsync(stop.Token);
                    var request = received.Buffer;
                    if (request.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(request) != 0x0001) continue;
                    var source = received.RemoteEndPoint;
                    var reply = new byte[32];
                    BinaryPrimitives.WriteUInt16BigEndian(reply, 0x0101);
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), 12);
                    request.AsSpan(4, 16).CopyTo(reply.AsSpan(4)); // Cookie and transaction ID.
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(20), 0x0020);
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(22), 8);
                    reply[25] = 1;
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(26), (ushort)(source.Port ^ 0x2112));
                    var address = source.Address.MapToIPv4().GetAddressBytes();
                    for (var i = 0; i < 4; i++) reply[28 + i] = (byte)(address[i] ^ request[4 + i]);
                    await socket.SendAsync(reply, source, stop.Token);
                    for (var offset = 20; sendProbe && offset + 4 <= request.Length;)
                    {
                        var type = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(offset));
                        var length = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(offset + 2));
                        if (type == 0xC051 && length == 68 && offset + 4 + length <= request.Length)
                        {
                            var value = request.AsSpan(offset + 4, length);
                            var probe = "QUASAR-PROBE-1\0\0"u8.ToArray().Concat(value[4..20].ToArray()).ToArray();
                            Interlocked.Increment(ref Probes);
                            await socket.SendAsync(probe, new IPEndPoint(source.Address, BinaryPrimitives.ReadUInt16BigEndian(value)), stop.Token);
                            break;
                        }
                        offset += 4 + (length + 3) / 4 * 4;
                    }
                }
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        public void Dispose() { stop.Cancel(); socket.Dispose(); }
    }
}

public sealed class RtcPeerProcessFactAttribute : FactAttribute
{
    public RtcPeerProcessFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("QUASAR_RTC_TEST_PEER")))
            Skip = "Build Quasar.Peer and set QUASAR_RTC_TEST_PEER for the native peer checks.";
    }
}
