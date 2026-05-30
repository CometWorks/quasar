using System;

namespace Magnetar.Protocol.Transport;

public class ServerCommandEnvelope
{
    public string CommandId { get; set; } = Guid.NewGuid().ToString("N");

    public string UniqueName { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    public string ServerId { get; set; } = string.Empty;

    public ServerCommandType CommandType { get; set; }

    public string Text { get; set; } = string.Empty;

    public long? SteamId { get; set; }

    /// <summary>
    /// Optional JSON request body for commands that carry structured parameters
    /// (e.g. <see cref="ServerCommandType.ListEntities"/> filter,
    /// <see cref="ServerCommandType.DeleteEntity"/> target). Empty for simple commands.
    /// </summary>
    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset IssuedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
