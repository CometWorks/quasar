using System;

namespace Magnetar.Protocol.Discovery;

public class WebServiceDiscoveryManifest
{
    public string InstanceId { get; set; } = string.Empty;

    public string NodeId { get; set; } = string.Empty;

    public string MachineName { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    public string BaseUrl { get; set; } = string.Empty;

    public DateTimeOffset StartedAtUtc { get; set; }
}
