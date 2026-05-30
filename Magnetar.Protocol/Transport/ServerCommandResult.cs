using System;

namespace Magnetar.Protocol.Transport;

public class ServerCommandResult
{
    public string CommandId { get; set; } = string.Empty;

    public string UniqueName { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    public string ServerId { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Optional JSON response body for commands that return structured data
    /// (e.g. <see cref="ServerCommandType.ListEntities"/> returns an
    /// <c>EntityListResult</c>). Empty for simple commands.
    /// </summary>
    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset CompletedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
