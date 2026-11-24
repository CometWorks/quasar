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
    /// (e.g. <see cref="ServerCommandType.SetPlayerPromoteLevel"/> target level,
    /// <see cref="ServerCommandType.ListEntities"/> filter,
    /// <see cref="ServerCommandType.DeleteEntity"/> target,
    /// <see cref="ServerCommandType.PluginRequest"/> companion request, or
    /// <see cref="ServerCommandType.SendFactionChat"/> faction tag). Empty for simple commands.
    /// </summary>
    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset IssuedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// How long the sender waits for the result, in milliseconds; 0 when it doesn't wait. The agent drops a
    /// command still queued for the game thread after this long instead of running it unobserved. Save and
    /// stop never expire. Older agents ignore it.
    /// </summary>
    public int TimeoutMilliseconds { get; set; }
}
