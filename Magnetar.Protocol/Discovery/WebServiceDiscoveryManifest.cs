using System;

namespace Magnetar.Protocol.Discovery;

public class WebServiceDiscoveryManifest
{
    public string WorkerId { get; set; } = string.Empty;

    public string HostId { get; set; } = string.Empty;

    public string MachineName { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    public string BaseUrl { get; set; } = string.Empty;

    public DateTimeOffset StartedAtUtc { get; set; }
}
