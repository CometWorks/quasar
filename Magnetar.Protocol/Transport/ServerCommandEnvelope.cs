using System;

namespace Magnetar.Protocol.Transport;

public class ServerCommandEnvelope
{
    public string CommandId { get; set; } = Guid.NewGuid().ToString("N");

    public string InstanceId { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    public string ServerId { get; set; } = string.Empty;

    public ServerCommandType CommandType { get; set; }

    public string Text { get; set; } = string.Empty;

    public long? SteamId { get; set; }

    public DateTimeOffset IssuedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
