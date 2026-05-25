using System;

namespace Magnetar.Protocol.Transport;

public class ServerCommandResult
{
    public string CommandId { get; set; } = string.Empty;

    public string InstanceId { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    public string ServerId { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public DateTimeOffset CompletedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
